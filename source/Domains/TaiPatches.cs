using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// TAI — the verbs that GRANT rank, and the knitted-garment Tailor's Mark stamp (rank-bonus-design.md
/// §TAI). Four grant layers, all isolated (warns-and-skips on a missing seam, so a moved/renamed path
/// simply logs inactive and is refined at playtest — it never breaks patch load):
///
///   • Spinning [spinningwheel, conditional] — the handheld spindle (ItemDropSpindle.ExtractTwine,
///     version-stable) grants TAI when twine is drawn off a full spindle; the powered wheel
///     (BlockEntitySpinningWheel.SpinInput) grants the mounted tailor on each output cycle. A fibre-
///     thrift proc drops the occasional extra unit for a master.
///   • Weaving [spinningwheel, conditional] — the fly-shuttle loom (BlockEntityFlyShuttleLoom.WeaveInput)
///     grants the mounted tailor on each cloth cycle, with the same fibre-thrift proc.
///   • Knitting [knitting, conditional] — ItemKnittingNeedles.OnHeldInteractStop grants TAI on a
///     completed knit AND stamps the Tailor's Mark on the knitted garment (grant+stamp). The output is
///     created as a local inside the method, so a prefix captures the input + the maker's rank/emphasis
///     and a postfix finds the freshly-given garment (matched by the mod's CLOTH_OUTPUTS map) and stamps it.
///   • Sewing / repair [vanilla, always] — the vanilla floor. Grid-crafting a garment stamps the mark
///     (stamp-only, no XP — the assembly grid grants nothing; XP is at spin/weave/knit). A vanilla
///     clothing-repair recipe grants the sew verb AND runs the repair-gate: an under-ranked repair
///     strips the mark (see TaiMarkPatches for the gate + the mark read).
///
/// NOTE: spinningwheel 1.2.12 (live) restructured from the designed 1.2.9; every spinning/weaving seam
/// here is reflected by name and isolated, so a rename logs inactive rather than throwing.
/// </summary>
public static class TaiPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    /// <summary>Captured at the knitting OnHeldInteractStop prefix (the output is a method local), read
    /// in the postfix to find + stamp the freshly-given garment. Single-threaded server -> one at a time.</summary>
    private static CollectibleObject? pendingKnitOutput;
    private static int pendingKnitLevel;
    private static int pendingKnitEmphasis;
    /// <summary>The twine stack as the Stop began: the collectible and how many (LGD-92).</summary>
    private static CollectibleObject? pendingKnitInput;
    private static int pendingKnitTwineBefore;

    public static void RegisterServer(ICoreServerAPI api) => sapi = api;

    // ------------------------------------------------------------ conditional patches

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // ---- Spinning: the handheld spindle (stable) + the powered wheel.
        var spindle = AccessTools.TypeByName("SpinningWheel.Items.ItemDropSpindle");
        var extract = spindle == null ? null : AccessTools.DeclaredMethod(spindle, "ExtractTwine");
        if (extract != null)
        {
            harmony.Patch(extract,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(SpindleExtractPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(SpindleExtractPostfix))));
            TcmLog.Info(api, "TAI spindle spinning hooked (ExtractTwine grant + steady fibre economy)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TAI spindle seam absent (spinningwheel); handheld spin grant inactive");

        var wheel = AccessTools.TypeByName("SpinningWheel.BlockEntities.BlockEntitySpinningWheel");
        var spinInput = wheel == null ? null : AccessTools.DeclaredMethod(wheel, "SpinInput");
        if (spinInput != null)
        {
            harmony.Patch(spinInput, postfix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(WheelSpinPostfix))));
            TcmLog.Info(api, "TAI spinning wheel hooked (SpinInput grant + fibre thrift)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TAI spinning-wheel seam absent (spinningwheel 1.2.12 restructure?); powered spin grant inactive");

        // ---- Weaving: the fly-shuttle loom.
        var loom = AccessTools.TypeByName("SpinningWheel.BlockEntities.BlockEntityFlyShuttleLoom");
        var weaveInput = loom == null ? null : AccessTools.DeclaredMethod(loom, "WeaveInput");
        if (weaveInput != null)
        {
            harmony.Patch(weaveInput, postfix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(LoomWeavePostfix))));
            TcmLog.Info(api, "TAI loom weaving hooked (WeaveInput grant + fibre thrift)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TAI loom seam absent (spinningwheel 1.2.12 restructure?); weave grant inactive");

        // ---- Knitting: grant + stamp on a completed knit.
        var needles = AccessTools.TypeByName("Knitting.Items.ItemKnittingNeedles");
        var knitStop = needles == null ? null : AccessTools.DeclaredMethod(needles, "OnHeldInteractStop");
        if (knitStop != null)
        {
            harmony.Patch(knitStop,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(KnitStopPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TaiPatches), nameof(KnitStopPostfix))));
            TcmLog.Info(api, "TAI knitting hooked (OnHeldInteractStop grant + Tailor's Mark stamp)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TAI knitting seam absent (knitting mod); knit grant inactive");
    }

    // ------------------------------------------------------------ spinning

    /// <summary>Fibre economy (Axis 2, steady) on the handheld spindle: scale the spindle's stored
    /// outputQuantity by the spinner's TAI rank BEFORE the vanilla extract reads and gives it — a master
    /// draws more twine per fibre, an Untrained less (the penalty lands here, on the primary hand verb).
    /// The fractional part rolls a proc; floored at 1 (you always get at least one length).</summary>
    public static void SpindleExtractPrefix(ItemSlot spindleSlot, IPlayer player)
    {
        var attrs = spindleSlot?.Itemstack?.Attributes;
        if (attrs == null || player?.Entity?.World?.Side != EnumAppSide.Server) return;
        int baseQty = attrs.GetInt("outputQuantity", 1);
        if (baseQty <= 0) return;
        double q = baseQty * TaiDomain.FiberEconomy(TaiDomain.LevelOf(player));
        int whole = (int)q;
        if (player.Entity.World.Rand.NextDouble() < q - whole) whole += 1;
        attrs.SetInt("outputQuantity", Math.Max(1, whole));
    }

    /// <summary>Drawing twine off a full handheld spindle grants the spin verb (the economy is applied in
    /// the prefix above). Every draw registers at the FULL hand rate: the spindle is a held tool, not a
    /// station, and it shared GrantSpin's per-minute bucket only by accident (0.5.15, LGD-238).</summary>
    public static void SpindleExtractPostfix(IPlayer player)
    {
        if (player?.Entity?.World?.Side != EnumAppSide.Server) return;
        GrantSpin(player, "spindle", player.Entity.EntityId, 1.0);
    }

    /// <summary>Each wheel output cycle grants the mounted tailor the spin verb, plus the fibre-economy
    /// bonus as a per-cycle proc (discrete output can't take a steady fractional, so the positive part of
    /// the economy rolls a chance of an extra unit; the penalty tooth lives on the spindle). The mount is
    /// the BE's MountedBy entity.</summary>
    public static void WheelSpinPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        IPlayer? player = MountedPlayer(__instance);
        if (player == null) return;
        GrantSpin(player, "wheel", HashCode.Combine(__instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z),
            TaiDomain.Knob(TaiDomain.StationRawMult, 0.5));
        EconomyBonusToSlot(player, OutputSlotOf(__instance));
    }

    /// <summary>Monotonic salt for station grants (0.5.15, LGD-238). The context must be UNIQUE per
    /// cycle so the 90s dedup ring cannot swallow a batch; the per-minute bucket it replaces was the
    /// cause of "the chat logs XP only about 1 in 10, the rest say repeat action". Volume is governed
    /// by the saturation curve and, for a station, by <see cref="TaiDomain.StationRawMult"/>.
    /// Interlocked because loom and wheel block entities tick off the main thread.</summary>
    private static int stationSeq;

    private static void GrantSpin(IPlayer player, string src, long saltA, double mult)
    {
        Core?.Ledger?.Log(player, TaiDomain.Code, TaiDomain.TechSpin,
            HashCode.Combine(src, saltA, System.Threading.Interlocked.Increment(ref stationSeq)), mult);
    }

    // ------------------------------------------------------------ weaving

    /// <summary>Each loom cloth cycle grants the mounted tailor the weave verb + the fibre-economy bonus
    /// proc (as on the wheel — discrete per-cycle output).</summary>
    public static void LoomWeavePostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        IPlayer? player = MountedPlayer(__instance);
        if (player == null) return;
        Core?.Ledger?.Log(player, TaiDomain.Code, TaiDomain.TechWeave,
            HashCode.Combine("loom", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                System.Threading.Interlocked.Increment(ref stationSeq)),
            TaiDomain.Knob(TaiDomain.StationRawMult, 0.5));
        EconomyBonusToSlot(player, OutputSlotOf(__instance));
    }

    // ------------------------------------------------------------ knitting (grant + stamp)

    /// <summary>Snapshot the maker + the expected knitted output before the method consumes the input
    /// and gives the garment (the output stack is a local we cannot reach from a postfix). Only when the
    /// knit will actually complete (secondsUsed past the knit time) and the input is knittable.</summary>
    public static void KnitStopPrefix(float secondsUsed, EntityAgent byEntity)
    {
        pendingKnitOutput = null;
        if (byEntity?.World?.Side != EnumAppSide.Server) return;
        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;

        var input = byEntity.LeftHandItemSlot?.Itemstack?.Collectible;
        if (input == null) return;

        // Resolve the expected output via the mod's CLOTH_OUTPUTS map (Item -> garment), reflected.
        var map = Traverse.CreateWithType("Knitting.Items.ItemKnittingNeedles").Field("CLOTH_OUTPUTS").GetValue()
                  as System.Collections.IDictionary;
        if (map == null || !map.Contains(input)) return;

        pendingKnitOutput = map[input] as CollectibleObject;
        pendingKnitLevel = TaiDomain.LevelOf(player);
        pendingKnitEmphasis = TaiEmphasis.EmphasisOf(player);
        pendingKnitInput = input;
        pendingKnitTwineBefore = byEntity.LeftHandItemSlot?.Itemstack?.StackSize ?? 0;
    }

    /// <summary>After the knit completes, grant the knit verb and stamp the Tailor's Mark on the freshly
    /// given garment (the newest matching, unmarked stack in the maker's inventory).</summary>
    public static void KnitStopPostfix(float secondsUsed, EntityAgent byEntity)
    {
        var output = pendingKnitOutput;
        var input = pendingKnitInput;
        int twineBefore = pendingKnitTwineBefore;
        pendingKnitOutput = null;
        pendingKnitInput = null;
        if (output == null || byEntity?.World?.Side != EnumAppSide.Server) return;
        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;

        // PAY FOR THE CLOTH, NOT THE RELEASE (LGD-92: releasing the needles before the knit
        // finished still granted full TAI, with the twine kept — a repeatable farm on any twine).
        //
        // The mod's own OnHeldInteractStop opens with `if (secondsUsed < GetKnitTime) return`
        // (decompiled from knitting 2.0.3), and a Harmony postfix runs after that early return
        // just the same, so this grant fired on every release of the mouse button. The design
        // ruling is explicit the other way (rank-bonus-design.md: "partial work banks nothing").
        //
        // A completed knit is the one path that takes twine out of the left hand (TakeOut(4)),
        // so the hand is asked instead of the clock: the stack shrank, changed or emptied — a
        // garment exists and the grant stands. Untouched twine means the mod refused (released
        // early, twine fell short, no mapped output), and nothing banks. Mirroring the time test
        // would also have worked, but it reads a private config through reflection and pays for
        // one case the mod itself refuses (twine dropped below four mid-hold).
        var hand = byEntity.LeftHandItemSlot?.Itemstack;
        bool consumed = hand == null || hand.Collectible != input || hand.StackSize < twineBefore;
        if (!consumed) return; // released early: no cloth was made, nothing to pay or stamp

        Core?.Ledger?.Log(player, TaiDomain.Code, TaiDomain.TechKnit,
            HashCode.Combine("knit", output.Id, (int)(byEntity.World.ElapsedMilliseconds / 1000)));

        ItemSlot? madeSlot = FindUnmarked(player, output);
        if (madeSlot?.Itemstack != null)
        {
            TaiMark.Stamp(madeSlot.Itemstack, player.PlayerUID, player.PlayerName, pendingKnitLevel, pendingKnitEmphasis);
            madeSlot.MarkDirty();
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The player mounted on a spinning-wheel / loom BE, via its MountedBy entity field.</summary>
    private static IPlayer? MountedPlayer(BlockEntity be)
    {
        var mounted = Traverse.Create(be).Field("MountedBy").GetValue<EntityAgent>();
        return (mounted as EntityPlayer)?.Player;
    }

    /// <summary>The station's output slot, read off the BE's own public <c>OutputSlot</c> property.
    ///
    /// This hardcoded slot 1 "on both the wheel and the loom" until 0.5.16, which was true of the
    /// wheel and never of the loom: the fly-shuttle loom's inventory is slots 0-2 weaving inputs,
    /// 3 THE OUTPUT, 4-7 pattern inputs (InventoryFlyshuttleLoom, decompiled from 1.2.12, the live
    /// version; the BE's own property reads `inventory?[3]`). So the Master+ fibre-thrift proc on
    /// the loom was adding its bonus unit to a weaving INPUT slot — free twine, the economy paid in
    /// raw material instead of cloth — or doing nothing when slot 1 sat empty. Found under LGD-119.
    /// Both BEs declare `public ItemSlot OutputSlot`, so asking the mod beats asserting an index,
    /// and a future restructure degrades to a skipped proc instead of a wrong slot.</summary>
    private static ItemSlot? OutputSlotOf(BlockEntity be)
    {
        if (be == null) return null;
        try { return Traverse.Create(be).Property("OutputSlot").GetValue() as ItemSlot; }
        catch { return null; }
    }

    /// <summary>Apply the positive part of the fibre economy as a per-cycle proc: with probability equal
    /// to the economy's fractional excess (e.g. 0.15 at GM), add one extra unit into the station's output
    /// slot. Discrete per-cycle output can't take a steady fractional, so the wheel/loom ride the economy
    /// curve as a bonus proc (the Untrained penalty tooth lives on the spindle's batch output).</summary>
    private static void EconomyBonusToSlot(IPlayer player, ItemSlot? outputSlot)
    {
        var made = outputSlot?.Itemstack;
        if (made == null) return;
        double p = TaiDomain.FiberEconomy(TaiDomain.LevelOf(player)) - 1.0;
        if (p <= 0 || player.Entity.World.Rand.NextDouble() >= p) return;
        made.StackSize += 1;
        outputSlot!.MarkDirty();
    }

    /// <summary>Find the slot holding a fresh unmarked stack of the given collectible in the player's
    /// inventory — the garment just knitted (no Tailor's Mark yet). Skips the creative inventory:
    /// InventoryPlayerCreative.Count reads currentTab.Inventory.Count, and currentTab is never built on
    /// a dedicated server, so enumerating it throws. An exception escaping the knit postfix disconnects
    /// the player ("Threw an exception at the server"), which is what happened on 2026-08-20.</summary>
    private static ItemSlot? FindUnmarked(IPlayer player, CollectibleObject want)
    {
        foreach (var inv in player.InventoryManager.Inventories.Values)
        {
            if (inv == null || inv.ClassName == GlobalConstants.creativeInvClassName) continue;

            try
            {
                foreach (var slot in inv)
                {
                    var st = slot?.Itemstack;
                    if (st?.Collectible == want && !TaiMark.HasMark(st)) return slot;
                }
            }
            catch (Exception e)
            {
                // Warns and skips, per this file's contract: a cosmetic mark must never cost a session.
                var api = player.Entity?.Api;
                if (api != null)
                {
                    TcmLog.Warn(api, $"TAI mark: inventory '{inv.ClassName}' could not be enumerated, skipped ({e.Message})");
                }
            }
        }
        return null;
    }
}
