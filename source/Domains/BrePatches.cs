using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// BRE — the two brewing verbs (technique-maps §BRE, both [vanilla]) plus the signature.
///
/// ATTRIBUTION MODEL (resolved 2026-07-22 with Jeffrey): the ruled "grant at completion / bank
/// while offline" cannot work — the Ledger only appends for an ONLINE player, and a seal completes
/// days later when the sealer is almost always offline. So the GRANT lands at the SEAL / IGNITE
/// (the skilled act, player online, recipe already known so the output class is known), deduped by
/// ferment type. This rewards actual play time, not idle offline barrels; the existing dedup ring
/// + daily K ceiling are the only cap needed. The completion-time EFFECTS that do not touch the
/// ledger — the spoilage taper voiding the batch, the portion dock, the Brewer's Mark stamp — still
/// fire at completion using the owner's rank FROZEN at seal (stored in a persisted pos map).
///
/// Output-classified grant (RULED pickling split): alcoholic ferments = BRE 100; non-alcoholic
/// preserves (pickle/brine/cured/vinegar/cheese/yogurt) = COO 50 / BRE 50. Distillation = BRE 100.
///
/// The spoilage taper (THE FRAMEWORK'S ONE RULED EXCEPTION): a rank-scaled chance the ferment voids
/// at completion — full while Untrained, tapering to ZERO at Journeyman I (NOT snap-at-Novice). This
/// single lever is both the Axis 1 penalty and the Axis 3 reliability spine. Plus reduced portions
/// while Untrained, and the Brewer's Mark ("Cured by X") on SOLID preserves only (liquids merge and
/// erase a mark). Deferred thin (ruling flags droppable): seal-time/boiler-fuel economy, input-waste
/// thrift, the exceptional-batch proc (inert without a variant asset).
/// </summary>
public static class BrePatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    public const string BreByAttr = "almanactcm:breby";
    public const string BreByNameAttr = "almanactcm:brebyname";
    public const string BreTierAttr = "almanactcm:bretier";

    /// <summary>Vessel pos -> the sealer's frozen mark, packed "uid|name|tier". Set at seal, read at
    /// the unattended completion (spoilage/portion/mark), then removed. Persisted: a seal matures
    /// across days and restarts.</summary>
    private static Dictionary<string, string> sealOwners = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverWorld = api.World;
        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacBreSealOwners");
                if (data != null)
                    sealOwners = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
                TcmLog.Cat(api, TcmLog.Config, $"BRE seal owners loaded: {sealOwners.Count} sealed vessel(s)");
            }
            catch (Exception e) { TcmLog.Error(api, $"BRE seal-owner map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacBreSealOwners",
                Vintagestory.API.Util.SerializerUtil.Serialize(sealOwners));
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Barrel (vanilla): grant at the seal packet, apply effects at completion.
        var tb = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityBarrel");
        if (tb != null)
        {
            harmony.Patch(AccessTools.DeclaredMethod(tb, "OnReceivedClientPacket"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(BarrelSealPostfix))));
            harmony.Patch(AccessTools.DeclaredMethod(tb, "OnEvery3Second"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(FermentPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(FermentPostfix))));
            var brk = AccessTools.DeclaredMethod(tb, "OnBlockBroken");
            if (brk != null) harmony.Patch(brk, postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(VesselBrokenPostfix))));
            TcmLog.Info(api, "BRE fermentation hooked (barrel seal grant + completion effects + break cleanup)");
        }
        else TcmLog.Warn(api, "BRE barrel seam not found (BlockEntityBarrel); fermentation verb inactive");

        // Distillation (vanilla): grant at the boiler interact (owner online), the still-operation act.
        var to = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityBoiler");
        var mo = to == null ? null : AccessTools.DeclaredMethod(to, "OnInteract");
        if (mo != null)
        {
            harmony.Patch(mo, postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(BoilerPostfix))));
            TcmLog.Info(api, "BRE distillation hooked (boiler OnInteract)");
        }
        else TcmLog.Warn(api, "BRE distillation seam not found (BlockEntityBoiler.OnInteract); distilling verb inactive");

        // Fermentaria clay fermenter (conditional): the pre-metal seal, same verb, its own BE.
        var tf = AccessTools.TypeByName("Fermentaria.BESimpleFermentingContainer")
              ?? AccessTools.TypeByName("FermentariaForked.BESimpleFermentingContainer");
        if (tf != null)
        {
            var pkt = AccessTools.DeclaredMethod(tf, "OnReceivedClientPacket");
            var tick = AccessTools.DeclaredMethod(tf, "OnEvery3Second");
            var fbrk = AccessTools.DeclaredMethod(tf, "OnBlockBroken");
            if (pkt != null) harmony.Patch(pkt, postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(FermenterSealPostfix))));
            if (tick != null) harmony.Patch(tick,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(FermenterTickPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(FermenterTickPostfix))));
            if (fbrk != null) harmony.Patch(fbrk, postfix: new HarmonyMethod(AccessTools.Method(typeof(BrePatches), nameof(VesselBrokenPostfix))));
            TcmLog.Info(api, "BRE fermentaria clay-fermenter hooked (parallel seal grant + completion)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "BRE fermentaria absent; clay-fermenter variant inactive (barrel unaffected)");
        // The Brewer's Mark line is contributed to Engine.ProvenanceLine (see MarkLine below),
        // which owns the whole provenance block's order and spacing.
    }

    // ------------------------------------------------------------ classification + grant

    private static bool IsPreserve(string code) =>
        code.Contains("pickl") || code.Contains("brine") || code.Contains("cured")
        || code.Contains("vinegar") || code.Contains("rennet") || code.Contains("cheese")
        || code.Contains("curd") || code.Contains("yogurt") || code.Contains("saltedmeat");

    /// <summary>The leather-making barrel chain (hide -> soaked -> prepared -> leather -> dyed): a
    /// sealed barrel process, but NOT brewing. Folded to HUN 2026-07-22 (the leatherworking-domain
    /// question — the crafts that use leather are grant-less grid crafts, so tanning is the only
    /// earnable leather verb, and it is the end of HUN's carcass chain). May re-home when TAI ships.</summary>
    private static bool IsTanning(string code) =>
        code.Contains("leather") || code.Contains("hide") || code.Contains("pelt");

    /// <summary>Barrel recipes that earn nothing: reagent prep (lime slaking, tannin steeping) that
    /// FEEDS tanning but is not itself the transformation verb. Skipped by every domain.</summary>
    private static bool IsNonEarning(string code) =>
        code.Contains("limewater") || code.Contains("slakedlime") || code.Contains("tannin");

    /// <summary>A dye bath: any ingredient whose code path starts with "dye". Ingredient-keyed
    /// rather than output-keyed so vanilla and wool-mod dye recipes both match unenumerated.</summary>
    private static bool IsDyeing(BarrelRecipe recipe)
    {
        if (recipe.Ingredients == null) return false;
        foreach (var ing in recipe.Ingredients)
        {
            string? path = ing?.Code?.Path;
            if (path != null && path.StartsWith("dye")) return true;
        }
        return false;
    }

    /// <summary>The seal is the skilled act: grant BRE (output-classified) to the online sealer and
    /// freeze their rank for the completion-time effects. Called from both barrel and fermenter.</summary>
    private static void StoreAndGrantSeal(ICoreAPI api, BlockPos pos, IPlayer player, BarrelRecipe? recipe)
    {
        if (player == null || recipe?.Output?.ResolvedItemStack?.Collectible == null) return;
        string code = recipe.Output.ResolvedItemStack.Collectible.Code?.ToString() ?? "ferment";
        int cx = HashCode.Combine("ferment", code, (serverWorld?.ElapsedMilliseconds ?? 0) / 60000);

        // Leather tanning is a barrel seal, but it is HUN's verb (butchery -> tanning), not BRE.
        if (IsTanning(code))
        {
            Core?.Ledger?.Log(player, HunDomain.Code, HunDomain.TechTanning, cx);
            TcmLog.Cat(api, "bre", $"seal at {pos}: leather tanning ({code}) -> HUN tanning for {player.PlayerName}");
            return; // no BRE grant, no completion effects (tanning has no ruled spoilage/mark)
        }
        // Dyeing is a barrel seal, but it is TAI's verb (RULED 2026-08-08): cloth + dye in,
        // dyed textile out. Detected by a dye ingredient, so it covers vanilla and the wool
        // mod without enumerating outputs. Consumes real dye, so it is farm-resistant by cost.
        if (IsDyeing(recipe))
        {
            Core?.Ledger?.Log(player, TaiDomain.Code, TaiDomain.TechDye, cx);
            TcmLog.Cat(api, "bre", $"seal at {pos}: dye bath ({code}) -> TAI dye for {player.PlayerName}");
            return; // no BRE grant, no completion effects
        }
        if (IsNonEarning(code)) // lime/tannin reagent prep: feeds tanning, earns nothing
        {
            TcmLog.Cat(api, "bre", $"seal at {pos}: non-earning barrel prep ({code}); no grant");
            return;
        }
        bool preserve = IsPreserve(code);

        // Alcoholic = BRE 100; non-alcoholic preserve = COO 50 / BRE 50 (the ruled pickling split).
        Core?.Ledger?.Log(player, BreDomain.Code, BreDomain.TechFermenting, cx, preserve ? 0.5 : 1.0);
        if (preserve)
            Core?.Ledger?.Log(player, CooDomain.Code, CooDomain.TechSalting, cx, 0.5);

        sealOwners[PosKey(pos)] = $"{player.PlayerUID}|{player.PlayerName}|{BreDomain.LevelOf(player)}";
        TcmLog.Cat(api, "bre", $"seal at {pos} by {player.PlayerName} -> {(preserve ? "preserve (COO/BRE split)" : "beverage (BRE)")}: {code}");
    }

    /// <summary>Completion-time effects, no ledger touch (works offline): the spoilage taper voids
    /// the batch by the sealer's frozen rank, else dock portions (Untrained) and stamp the Brewer's
    /// Mark on solids. Called once, when a sealed vessel crafts.</summary>
    private static void CompletionEffects(ICoreAPI api, BlockPos pos, ItemSlot? outSlot)
    {
        string key = PosKey(pos);
        if (!sealOwners.TryGetValue(key, out string? packed) || packed == null) return;
        sealOwners.Remove(key);
        var stack = outSlot?.Itemstack;
        if (stack?.Collectible == null) return;

        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int tier)) return;

        // The spoilage taper (the ruled exception): a bad-ratio ferment fails outright.
        double spoil = BreDomain.SpoilChance(tier);
        if (spoil > 0 && api.World.Rand.NextDouble() < spoil)
        {
            var rot = api.World.GetItem(new AssetLocation("game:rot"));
            outSlot!.Itemstack = rot != null ? new ItemStack(rot, Math.Max(1, stack.StackSize / 2)) : null;
            outSlot.MarkDirty();
            TcmLog.Cat(api, "bre", $"seal at {pos} SPOILED (tier {tier}, chance {spoil:P0}) -> batch lost");
            return;
        }

        // Reduced portions while Untrained (clears at Novice).
        if (tier <= 0)
        {
            double f = BreDomain.Knob(BreDomain.PortionUntrained, 0.75);
            int docked = Math.Max(1, (int)Math.Floor(stack.StackSize * f));
            if (docked < stack.StackSize) { stack.StackSize = docked; outSlot!.MarkDirty(); }
        }

        // The Brewer's Mark: durable only on SOLID preserves (liquids merge and erase it).
        if (tier >= Rank.Journeyman && BlockLiquidContainerBase.GetContainableProps(stack) == null)
        {
            stack.Attributes.SetString(BreByAttr, p[0]);
            stack.Attributes.SetString(BreByNameAttr, p[1]);
            stack.Attributes.SetInt(BreTierAttr, tier);
            outSlot!.MarkDirty();
            TcmLog.Cat(api, "bre", $"seal at {pos}: Brewer's Mark of {p[1]} on {stack.Collectible.Code?.Path}");
        }

        // The Brewmaster's measure (0.5 ruling, 2026-08-22): a Grandmaster's seal can pay over
        // the rating. Runs after the spoil roll (a GM never spoils; the taper is long spent)
        // and never meets the Untrained dock, so the levers cannot stack. The capability lives
        // in the COUNT so liquids stay attribute-clean; barrel and fermenter both route here.
        if (tier >= Rank.Grandmaster
            && api.World.Rand.NextDouble() < BreDomain.Knob(BreDomain.MeasureChanceGm, 0.25))
        {
            int bonus = Math.Max(1, (int)Math.Round(
                stack.StackSize * BreDomain.Knob(BreDomain.MeasureBonusFraction, 0.10)));
            stack.StackSize += bonus;
            outSlot!.MarkDirty();
            TcmLog.Cat(api, "bre", $"seal at {pos}: the Brewmaster's measure pays +{bonus} over the rating");
        }
    }

    // ------------------------------------------------------------ barrel hooks (direct cast)

    public static void BarrelSealPostfix(BlockEntity __instance, IPlayer player, int packetid)
    {
        if (packetid != 1337 || __instance is not BlockEntityBarrel be || be.Api?.Side != EnumAppSide.Server) return;
        StoreAndGrantSeal(be.Api, be.Pos, player, be.CurrentRecipe);
    }

    public static void FermentPrefix(BlockEntity __instance, out string? __state)
    {
        __state = null;
        if (__instance is not BlockEntityBarrel be || be.Api?.Side != EnumAppSide.Server) return;
        if (be.Sealed && be.CurrentRecipe?.Output?.ResolvedItemStack?.Collectible != null)
            __state = be.CurrentRecipe.Output.ResolvedItemStack.Collectible.Code?.ToString();
    }

    public static void FermentPostfix(BlockEntity __instance, string? __state)
    {
        if (__state == null || __instance is not BlockEntityBarrel be || be.Api?.Side != EnumAppSide.Server) return;
        var outSlot = be.Inventory?[0];
        // Crafted this tick iff slot 0 now holds the expected output code (was the input before).
        if (outSlot?.Itemstack?.Collectible?.Code?.ToString() != __state) return;
        CompletionEffects(be.Api, be.Pos, outSlot);
    }

    /// <summary>A sealed vessel destroyed before completion (broken, or burned in a fire) leaves an
    /// orphaned owner entry — drop it. The sealer keeps the XP already banked at seal (the skilled
    /// act happened); only the physical contents are lost, exactly as vanilla loses a sealed barrel's
    /// contents on break. No exploit either way. A no-op if completion already consumed the entry.</summary>
    public static void VesselBrokenPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (sealOwners.Remove(PosKey(__instance.Pos)))
            TcmLog.Cat(__instance.Api, "bre", $"sealed vessel at {__instance.Pos} destroyed pre-completion; owner entry dropped (seal XP kept)");
    }

    // ------------------------------------------------------------ boiler (distillation)

    public static void BoilerPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        // Operating the still is the online act; deduped per boiler per minute (per-session verb).
        Core?.Ledger?.Log(byPlayer, BreDomain.Code, BreDomain.TechDistilling,
            HashCode.Combine("distill", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 60000));
    }

    // ------------------------------------------------------------ fermentaria hooks (Traverse)

    private static (bool sealed_, BarrelRecipe? recipe, IInventory? inv) ReadFermenter(BlockEntity be)
    {
        var tv = Traverse.Create(be);
        bool s = tv.Field("Sealed").GetValue<bool>();
        var r = tv.Field("CurrentRecipe").GetValue() as BarrelRecipe;
        var inv = (tv.Property("Inventory").GetValue() ?? tv.Field("inventory").GetValue()) as IInventory;
        return (s, r, inv);
    }

    public static void FermenterSealPostfix(BlockEntity __instance, IPlayer player, int packetid)
    {
        if (packetid != 1337 || __instance?.Api?.Side != EnumAppSide.Server) return;
        StoreAndGrantSeal(__instance.Api, __instance.Pos, player, ReadFermenter(__instance).recipe);
    }

    public static void FermenterTickPrefix(BlockEntity __instance, out string? __state)
    {
        __state = null;
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var (s, r, _) = ReadFermenter(__instance);
        if (s && r?.Output?.ResolvedItemStack?.Collectible != null)
            __state = r.Output.ResolvedItemStack.Collectible.Code?.ToString();
    }

    public static void FermenterTickPostfix(BlockEntity __instance, string? __state)
    {
        if (__state == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = ReadFermenter(__instance).inv;
        var outSlot = inv != null && inv.Count > 0 ? inv[0] : null;
        if (outSlot?.Itemstack?.Collectible?.Code?.ToString() != __state) return;
        CompletionEffects(__instance.Api, __instance.Pos, outSlot);
    }

    // ------------------------------------------------------------ Brewer's Mark tooltip

    /// <summary>The Brewer's Mark line (Journeyman up, solid preserves only): Cured by / Aged by /
    /// a masterwork-preserve line. Placement, order and spacing belong to
    /// <see cref="Engine.ProvenanceLine"/>; this only decides what BRE has to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(BreByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        int tier = attrs!.GetInt(BreTierAttr);
        return
            tier >= Rank.Grandmaster ? Lang.Get("almanactcm:masterpreserve-by", name)
            : tier >= Rank.Master ? Lang.Get("almanactcm:aged-by", name)
            : tier >= Rank.Journeyman ? Lang.Get("almanactcm:cured-by", name)
            : null;
    }
}
