using System;
using System.Collections.Generic;
using System.Globalization;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// THE TURN (ruled 2026-09-14, the Vinni_Pukh thread): the replacement for the BRE spoilage
/// taper's blind dice roll. A ferment can start to go sideways partway through the seal, the
/// cask SAYS so, and a player who answers in time saves the batch. The only path to rot is
/// being online, told, and absent: every loss is attributable.
///
/// THE SHAPE:
/// - At seal, the batch's size against the sealer's rank decides how many turns the ferment
///   will take: one per safe-fraction beyond the first. Safe fraction 1/4 Untrained and
///   Novice, 1/2 Apprentice, 3/4 Journeyman, everything at Master I and up (rank law
///   stretched one rung by ruling: SOME need to check remains until Master). An Untrained
///   sealer always owes at least one turn, the teaching turn, so the verb is learned on the
///   first cask and not the fortieth.
/// - Fire points are drawn per seal from world RNG inside the 35-75% band of the seal time
///   (min gap 5%) and STORED, so no cask repeats another's rhythm ("not a predictable ten
///   hours from now", ruled) while a given cask stays deterministic: same seal, same fate,
///   and the Apprentice-and-up read can honestly say when it will want tending.
/// - When a turn fires the cask is TURNING: the mouseover says so to every viewer (rank buys
///   the window, never the knowledge), and the sealer gets a chat line if online. The window
///   burns ONLY while the sealer is online (a neighbour keeping the chunk loaded must not eat
///   an absent player's batch). Expired: the batch is marked and completes as rot. Tended:
///   clean, no scar.
/// - The tend is a plain right-click on the sealed cask, which vanilla wastes anyway:
///   beverages want an empty-hand skim, preserves want a hand of salt pressed in (one salt,
///   consumed). The classifier's existing beverage/preserve split IS the verb split. Any
///   player may tend, not only the sealer: the skill judgment happened at seal.
/// - A ferment that reaches its end with a turn outstanding HOLDS: sealed, "stands ready but
///   wants tending", craft deferred until someone tends it. It never rots while waiting, and
///   it can never be collected untended. Logging out dodges nothing; it only postpones.
///
/// PLUMBING. State lives in the barrel's OWN tree attribute ("almanactcmTurn", one packed
/// string) via To/FromTreeAttributes postfixes: the server cache repopulates on chunk load,
/// the client learns display state through vanilla's normal BE sync, and a broken vessel
/// takes its state with it. No savegame blob, no network channel.
///
/// VESSELS. Both the vanilla barrel and Fermentaria's clay fermenter, since 2026-09-15. The
/// fermenter mirrors the barrel's shape closely enough to share the whole system (a real
/// BarrelRecipe in CurrentRecipe, a Sealed flag, an inventory, CapacityLitres), so it is read
/// reflectively rather than reimplemented. Two differences are handled, not assumed: it runs
/// five inventory slots against the barrel's two, so the batch's liquid is found by SCANNING
/// rather than by index, and it holds 30 litres against the barrel's 50, so the same absolute
/// batch is a larger FRACTION of it and rightly owes more tending.
/// </summary>
public static class BreTurnSystem
{
    // ------------------------------------------------------------ state

    public const int PhaseNone = 0, PhaseTurning = 1, PhaseSpoiled = 2, PhaseHeld = 3;
    public const int VerbSkim = 0, VerbSalt = 1;

    private class TurnState
    {
        public string SealerUid = "";
        public int Tier;
        public bool Preserve;             // decides the tend verb: salt vs skim
        public bool Spoiled;
        public bool ActiveTurn;           // a fired, untended turn
        public bool Held;                 // ready to craft, waiting on a tend
        public int NextFire;              // index into FirePoints
        public double WindowLeftHours;    // burns only while the sealer is online
        public double SealedAtHours;      // world calendar TotalHours at seal
        public double SealHours;          // recipe duration
        public double LastTickHours;      // for the online burn delta
        public double[] FirePoints = Array.Empty<double>();   // fractions of SealHours, sorted

        public string Pack()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join("|", SealerUid, Tier.ToString(ic), Preserve ? 1 : 0, Spoiled ? 1 : 0,
                ActiveTurn ? 1 : 0, Held ? 1 : 0, NextFire.ToString(ic),
                WindowLeftHours.ToString("R", ic), SealedAtHours.ToString("R", ic),
                SealHours.ToString("R", ic), LastTickHours.ToString("R", ic),
                string.Join(";", Array.ConvertAll(FirePoints, p => p.ToString("R", ic))));
        }

        public static TurnState? Unpack(string s)
        {
            try
            {
                var ic = CultureInfo.InvariantCulture;
                string[] p = s.Split('|');
                if (p.Length < 12) return null;
                return new TurnState
                {
                    SealerUid = p[0],
                    Tier = int.Parse(p[1], ic),
                    Preserve = p[2] == "1",
                    Spoiled = p[3] == "1",
                    ActiveTurn = p[4] == "1",
                    Held = p[5] == "1",
                    NextFire = int.Parse(p[6], ic),
                    WindowLeftHours = double.Parse(p[7], ic),
                    SealedAtHours = double.Parse(p[8], ic),
                    SealHours = double.Parse(p[9], ic),
                    LastTickHours = double.Parse(p[10], ic),
                    FirePoints = p[11].Length == 0
                        ? Array.Empty<double>()
                        : Array.ConvertAll(p[11].Split(';'), x => double.Parse(x, ic)),
                };
            }
            catch { return null; }
        }
    }

    private const string TreeKey = "almanactcmTurn";

    /// <summary>Server-side authoritative state, keyed by pos. Repopulated from the barrel's
    /// tree on chunk load, so it survives restarts without its own store.</summary>
    private static readonly Dictionary<string, TurnState> states = new();

    /// <summary>Client-side display state, fed by FromTreeAttributes on ordinary BE sync:
    /// phase, verb, and the next fire time for the foresight read.</summary>
    private static readonly Dictionary<string, (int phase, int verb, double nextFireAtHours)> clientDisplay = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    /// <summary>What the client knows of a cask, for the barrel read. phase PhaseNone with a
    /// positive nextFireAtHours means quiet now but a turn is coming (the foresight line).</summary>
    public static (int phase, int verb, double nextFireAtHours) ClientStateFor(BlockPos pos)
        => clientDisplay.TryGetValue(PosKey(pos), out var v) ? v : (PhaseNone, VerbSkim, 0);

    // ------------------------------------------------------------ vessel

    // The Turn runs on any barrel-SHAPED sealed vessel, not only the vanilla barrel. Fermentaria's
    // clay fermenter mirrors the members that matter (Sealed, a real BarrelRecipe in CurrentRecipe,
    // an inventory, CapacityLitres) but is not in our reference set, so it is read reflectively.
    // Every read is guarded: a vessel that stops mirroring the shape degrades to "full batch",
    // never to an exception on a 3-second tick.

    private static bool VesselSealed(BlockEntity be)
    {
        if (be is BlockEntityBarrel b) return b.Sealed;
        try { return Traverse.Create(be).Field("Sealed").GetValue<bool>(); }
        catch { return false; }
    }

    private static BarrelRecipe? VesselRecipe(BlockEntity be)
    {
        if (be is BlockEntityBarrel b) return b.CurrentRecipe;
        try { return Traverse.Create(be).Field("CurrentRecipe").GetValue() as BarrelRecipe; }
        catch { return null; }
    }

    private static IInventory? VesselInv(BlockEntity be) =>
        be is BlockEntityBarrel b ? b.Inventory : (be as BlockEntityContainer)?.Inventory;

    private static int VesselCapacity(BlockEntity be)
    {
        if (be is BlockEntityBarrel b) return Math.Max(1, b.CapacityLitres);
        try
        {
            int cap = Traverse.Create(be).Property("CapacityLitres").GetValue<int>();
            if (cap > 0) return cap;
        }
        catch { /* fall through to the blocktype */ }
        return Math.Max(1, be.Block?.Attributes?["capacityLitres"]?.AsInt(50) ?? 50);
    }

    // ------------------------------------------------------------ seal

    /// <summary>Called from the barrel seal hook for allowlisted ferments only. Draws the turn
    /// schedule for this batch; stores nothing when the sealer's rank owes none.</summary>
    public static void OnSealed(BlockEntity be, IPlayer player, bool preserve)
    {
        var recipe = be == null ? null : VesselRecipe(be);
        if (be?.Api?.Side != EnumAppSide.Server || recipe == null || recipe.SealHours <= 0) return;

        int tier = BreDomain.LevelOf(player);
        if (tier >= Rank.Master) { Drop(be.Pos); return; }   // seal and forget

        double safe = SafeFractionFor(tier);
        double fraction = BatchFraction(be);
        int turns = Math.Max(0, (int)Math.Ceiling(fraction / safe - 1e-9) - 1);
        if (tier <= 0 && BreDomain.Knob(BreDomain.TurnTeachingUntrained, 1) > 0)
            turns = Math.Max(turns, 1);   // the teaching turn
        if (turns == 0) { Drop(be.Pos); return; }

        // The variance ruling: fire points drawn fresh per seal, sorted, min gap 5% of the
        // seal, all inside the 35-75% band so every turn fires before completion when loaded.
        var rand = be.Api.World.Rand;
        var points = new double[turns];
        for (int i = 0; i < turns; i++) points[i] = 0.35 + rand.NextDouble() * 0.40;
        Array.Sort(points);
        for (int i = 1; i < turns; i++)
            if (points[i] - points[i - 1] < 0.05) points[i] = Math.Min(0.90, points[i - 1] + 0.05);

        double now = be.Api.World.Calendar.TotalHours;
        states[PosKey(be.Pos)] = new TurnState
        {
            SealerUid = player.PlayerUID,
            Tier = tier,
            Preserve = preserve,
            SealedAtHours = now,
            SealHours = recipe.SealHours,
            LastTickHours = now,
            FirePoints = points,
        };
        be.MarkDirty();
        TcmLog.Cat(be.Api, "bre", $"seal at {be.Pos}: {turns} turn(s) drawn for {player.PlayerName} " +
            $"(tier {tier}, batch {fraction:P0}, safe {safe:P0}) at " +
            string.Join(", ", Array.ConvertAll(points, p => p.ToString("P0"))));
    }

    /// <summary>Batch size as a fraction of the vessel: litres of liquid over capacity. Every
    /// allowlisted ferment carries its bulk as liquid (juice, brine, milk). The liquid is found
    /// by SCANNING the slots, not by index: the vanilla barrel keeps it in slot 1, but the clay
    /// fermenter runs five slots and promises no such layout. No readable liquid means a
    /// conservative full batch.</summary>
    private static double BatchFraction(BlockEntity be)
    {
        try
        {
            var inv = VesselInv(be);
            if (inv == null) return 1.0;
            double litres = 0;
            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack == null) continue;
                var props = BlockLiquidContainerBase.GetContainableProps(stack);
                if (props == null || props.ItemsPerLitre <= 0) continue;
                litres += stack.StackSize / props.ItemsPerLitre;
            }
            if (litres <= 0) return 1.0;
            return GameMath.Clamp(litres / VesselCapacity(be), 0.05, 1.0);
        }
        catch { return 1.0; }
    }

    private static double SafeFractionFor(int tier) =>
        tier >= Rank.Journeyman ? BreDomain.Knob(BreDomain.TurnSafeJourneyman, 0.75)
        : tier >= Rank.Apprentice ? BreDomain.Knob(BreDomain.TurnSafeApprentice, 0.25)
        : tier >= Rank.Novice ? BreDomain.Knob(BreDomain.TurnSafeNovice, 0.25)
        : BreDomain.Knob(BreDomain.TurnSafeUntrained, 0.25);

    private static double WindowHoursFor(int tier) =>
        tier >= Rank.Journeyman ? BreDomain.Knob(BreDomain.TurnWindowJourneyman, 24)
        : tier >= Rank.Apprentice ? BreDomain.Knob(BreDomain.TurnWindowApprentice, 18)
        : tier >= Rank.Novice ? BreDomain.Knob(BreDomain.TurnWindowNovice, 12)
        : BreDomain.Knob(BreDomain.TurnWindowUntrained, 6);

    // ------------------------------------------------------------ tick

    /// <summary>Runs from the barrel's own 3-second tick (server). Fires due turns, burns the
    /// window while the sealer is online, expires it into a spoil mark, and answers whether
    /// the craft must be HELD (completion reached with a turn still outstanding).</summary>
    public static bool Tick(BlockEntity be)
    {
        if (be?.Api?.Side != EnumAppSide.Server || !VesselSealed(be)) return false;
        if (!states.TryGetValue(PosKey(be.Pos), out var st)) return false;

        var world = be.Api.World;
        double now = world.Calendar.TotalHours;

        // CLAMPED, and this is the whole point of the mechanic (found in testing 2026-09-15).
        // The window measures the player's real OPPORTUNITY to answer, so it may only burn at
        // the rate the world actually ticks. Raw calendar deltas jump: an admin `/time set`
        // skips hours in one tick, and — the one that matters in play — a chunk that unloads
        // while the sealer wanders off comes back with a delta of DAYS, which would expire the
        // window on the reload tick and rot the batch without the player ever seeing the turn.
        // That is the exact blind, unanswerable loss this system exists to abolish. A barrel
        // ticks every 3 seconds, which is ~0.0125 in-game hours at CSM 0.25 and ~0.025 at the
        // stock calendar, so this ceiling never binds during normal play; it only bites the
        // jumps. Firing a turn still keys off calendar PROGRESS, so a cask whose fire point
        // passed while unloaded turns the moment it loads. Only the answering clock is clamped.
        double raw = Math.Max(0, now - st.LastTickHours);
        double elapsed = Math.Min(raw, BreDomain.Knob(BreDomain.TurnWindowMaxBurnPerTick, 0.25));
        st.LastTickHours = now;
        double progress = st.SealHours <= 0 ? 1.0 : (now - st.SealedAtHours) / st.SealHours;

        if (st.Spoiled) return false;   // let it complete; CompletionEffects turns it to rot

        // Fire the next due turn. One at a time: a cask that slept through several points
        // serves them serially, each wanting its own tend.
        if (!st.ActiveTurn && st.NextFire < st.FirePoints.Length && progress >= st.FirePoints[st.NextFire])
        {
            st.ActiveTurn = true;
            st.NextFire++;
            st.WindowLeftHours = WindowHoursFor(st.Tier);
            be.MarkDirty();
            Announce(be, st, "almanactcm:bre-turn-chat-fired");
            TcmLog.Cat(be.Api, "bre", $"cask at {be.Pos} turned ({st.NextFire}/{st.FirePoints.Length}, " +
                $"window {st.WindowLeftHours:0.#}h, sealer {(SealerOnline(be, st) != null ? "online" : "offline")})");
        }

        bool complete = progress >= 1.0;

        if (st.ActiveTurn)
        {
            if (complete)
            {
                // The cask holds: never rots while waiting, never collected untended.
                if (!st.Held) { st.Held = true; be.MarkDirty(); }
                return true;
            }
            var sealer = SealerOnline(be, st);
            if (sealer != null)
            {
                st.WindowLeftHours -= elapsed;
                if (st.WindowLeftHours <= 0)
                {
                    st.ActiveTurn = false;
                    st.Spoiled = true;
                    be.MarkDirty();
                    Announce(be, st, "almanactcm:bre-turn-chat-expired");
                    TcmLog.Cat(be.Api, "bre", $"cask at {be.Pos} went untended past its window -> marked spoiled");
                }
            }
        }

        return false;
    }

    private static IServerPlayer? SealerOnline(BlockEntity be, TurnState st)
    {
        foreach (var p in be.Api.World.AllOnlinePlayers)
            if (p.PlayerUID == st.SealerUid) return p as IServerPlayer;
        return null;
    }

    private static void Announce(BlockEntity be, TurnState st, string langKey)
    {
        var sealer = SealerOnline(be, st);
        sealer?.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.GetL(sealer.LanguageCode ?? "en", langKey), EnumChatType.Notification);
    }

    // ------------------------------------------------------------ tend

    /// <summary>The tend, on the sealed barrel's otherwise-dead right-click. Server side is
    /// authoritative; the client mirror only consumes the click and voices the wrong-hand
    /// case, off its synced display state.</summary>
    [HarmonyPatch(typeof(BlockBarrel), nameof(BlockBarrel.OnBlockInteractStart))]
    public static class TendInteractPatch
    {
        public static bool Prefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
            => TryTend(world, byPlayer, blockSel, ref __result);
    }

    /// <summary>The tend, on a sealed vessel's otherwise-dead right-click. Shared by the barrel
    /// patch above and the reflectively-wired clay-fermenter patch, so both vessels answer a turn
    /// the same way. Returns false to swallow the click (vanilla's own interact must not run).</summary>
    public static bool TryTend(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        {
            if (world?.Api == null || byPlayer == null || blockSel?.Position == null) return true;
            var be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (be == null || !VesselSealed(be)) return true;

            if (world.Api is ICoreClientAPI capi)
            {
                var (phase, verb, _) = ClientStateFor(blockSel.Position);
                if (phase != PhaseTurning && phase != PhaseHeld) return true;
                if (verb == VerbSalt && !HoldingSalt(byPlayer))
                {
                    capi.TriggerIngameError(typeof(BreTurnSystem), "breturn",
                        Lang.Get("almanactcm:bre-turn-wantsalt"));
                    __result = true;
                    return false;
                }
                __result = true;   // the tend itself resolves server-side
                return false;
            }

            if (!states.TryGetValue(PosKey(blockSel.Position), out var st)) return true;
            if (!st.ActiveTurn && !st.Held) return true;

            if (st.Preserve)
            {
                if (!HoldingSalt(byPlayer)) { __result = true; return false; }   // client already voiced it
                var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
                slot.TakeOut(1);
                slot.MarkDirty();
            }
            else if (!(byPlayer.InventoryManager?.ActiveHotbarSlot?.Empty ?? false))
            {
                (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:bre-turn-wantskim"),
                    EnumChatType.Notification);
                __result = true;
                return false;
            }

            st.ActiveTurn = false;
            st.Held = false;   // a held cask crafts on its next tick
            be.MarkDirty();
            (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:bre-turn-tended"),
                EnumChatType.Notification);
            TcmLog.Cat(world.Api, "bre", $"cask at {blockSel.Position} tended by {byPlayer.PlayerName}");
            __result = true;
            return false;
        }
    }

    private static bool HoldingSalt(IPlayer player)
        => player.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.Path == "salt";

    // ------------------------------------------------------------ completion / teardown

    /// <summary>Whether this cask's batch was spoiled by a missed turn. Consumes the state:
    /// called exactly once, from CompletionEffects, when the craft lands.</summary>
    public static bool ConsumeSpoiled(BlockPos pos)
    {
        if (!states.Remove(PosKey(pos), out var st)) return false;
        return st.Spoiled;
    }

    /// <summary>A vessel broken, unsealed early, or resealed: its turn state goes with it.</summary>
    public static void Drop(BlockPos pos) => states.Remove(PosKey(pos));

    // ------------------------------------------------------------ tree ride-along

    /// <summary>The state rides the barrel's own tree: persistence and client sync for free,
    /// and a broken barrel cleans up after itself.</summary>
    [HarmonyPatch(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.ToTreeAttributes))]
    public static class ToTreePatch
    {
        public static void Postfix(BlockEntityBarrel __instance, ITreeAttribute tree) => WriteTree(__instance, tree);
    }

    /// <summary>State rides the vessel's own tree: persistence and client sync for free, and a
    /// broken vessel cleans up after itself. Shared by both vessel types.</summary>
    public static void WriteTree(BlockEntity be, ITreeAttribute tree)
    {
        if (be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;
        if (states.TryGetValue(PosKey(be.Pos), out var st))
            tree.SetString(TreeKey, st.Pack());
    }

    [HarmonyPatch(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.FromTreeAttributes))]
    public static class FromTreePatch
    {
        public static void Postfix(BlockEntityBarrel __instance, ITreeAttribute tree, IWorldAccessor worldForResolving)
            => ReadTree(__instance, tree, worldForResolving);
    }

    /// <summary>The read half of the tree ride-along. Server restores authoritative state; client
    /// keeps display state only.</summary>
    public static void ReadTree(BlockEntity be, ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        {
            string? packed = tree.GetString(TreeKey);
            var pos = be?.Pos;
            if (pos == null) return;

            if (worldForResolving.Side == EnumAppSide.Server)
            {
                if (packed == null) return;
                var st = TurnState.Unpack(packed);
                if (st != null) states[PosKey(pos)] = st;
                return;
            }

            // Client: display state only.
            if (packed == null) { clientDisplay.Remove(PosKey(pos)); return; }
            var cs = TurnState.Unpack(packed);
            if (cs == null) return;
            int phase = cs.Spoiled ? PhaseSpoiled : cs.Held ? PhaseHeld : cs.ActiveTurn ? PhaseTurning : PhaseNone;
            double nextAt = phase == PhaseNone && cs.NextFire < cs.FirePoints.Length
                ? cs.SealedAtHours + cs.FirePoints[cs.NextFire] * cs.SealHours
                : 0;
            clientDisplay[PosKey(pos)] = (phase, cs.Preserve ? VerbSalt : VerbSkim, nextAt);
        }
    }
}
