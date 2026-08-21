using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// GLA — the whole-domain conditional (rank-bonus-design §GLA / gla-glass-study, adopted 9/9;
/// seams verified against glassmakingfork 1.5.8). Vanilla ships no glassmaking verbs, so every
/// hook is guarded: absent glassmakingfork => the domain is dormant, nothing patched, no crash.
///
/// Phase 1 — the five verbs: melt (smeltery/large-hearth TryAdd, owner-at-charge), blowing (the
/// freehand pipe recipe complete + the mold TakeGlass), casting (the mold TryTakeContents
/// collect), workbench (TryCompleteStep), annealing (credit at retrieval of a CONVERTED piece).
///
/// The thermal window (Axis 3 + the GM signature): shatter is a deterministic deadline
/// (ShouldShatter: temp &lt; 100C literal), so the lever is the threshold. A per-piece tolerance
/// stamped by the maker's rank at annealer load (the one clean point where the raw piece is a
/// known stack in a known hand — and it serves the ownerless annealer, the live cooling-shatter
/// threat). One postfix on the static ShouldShatter reads it: Untrained 120C (penalty), Novice
/// 100C, GM 80C (never immune). Upgrade-only, so a GM-blown piece stays forgiving in a novice's
/// hands (the Heirloom philosophy).
///
/// Provenance (the tradeable token): the anneal conversion clones the recipe output, wiping the
/// maker mark — so it is snapshot/restored across BlockEntityAnnealer.OnCommonTick, then shown in
/// the tooltip (Blown by / Master-blown by), Journeyman up.
/// </summary>
public static class GlaPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    private const string AnnealAttr = "glassmaking:anneal"; // vanilla raw-glass marker
    public const string TolAttr = "almanactcm:glatol";      // maker tier -> the shatter window
    public const string GlaByAttr = "almanactcm:glaby";     // maker uid
    public const string GlaByNameAttr = "almanactcm:glabyname";

    /// <summary>The glassmaker's rank on the piece. Named 2026-08-12; it was a bare literal at
    /// three sites (one write, two reads). NOTE the key string says "tier" but the value is a
    /// LEVEL (0-17), matching the other eight *tier-named keys in the mod. The key string is
    /// deliberately NOT renamed: it already holds the right value and renaming costs a migration
    /// for nothing.</summary>
    public const string GlaProvAttr = "almanactcm:glaprovtier";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverWorld = api.World;
    }

    private static bool IsRawGlass(ItemStack? stack)
    {
        var attrs = stack?.Collectible?.Attributes;
        return attrs != null && attrs[AnnealAttr].Exists;
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled(GlaDomain.RequiredMod))
        {
            TcmLog.Cat(api, TcmLog.Config, "GLA dormant: glassmakingfork absent (no verbs, banked progress preserved)");
            return;
        }

        // Melt (owner-at-charge): both the smeltery and the large-hearth multiblock (same verb).
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityGlassSmeltery", "TryAdd", null, nameof(MeltPostfix), "GLA melt (smeltery)");
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityLargeSmelteryHearth", "TryAdd", null, nameof(MeltPostfix), "GLA melt (large hearth)");

        // Blowing: freehand pipe completion + mold blow.
        Hook(api, harmony, "GlassMaking.Items.Behavior.GlasspipeRecipeBehavior", "OnRecipeUpdated", null, nameof(BlowFreehandPostfix), "GLA blowing (freehand)");
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityGlassBlowingMold", "TakeGlass", null, nameof(BlowMoldPostfix), "GLA blowing (mold)");

        // Casting: the collect.
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityGlassCastingMold", "TryTakeContents", null, nameof(CastPostfix), "GLA casting (collect)");

        // Workbench cold-working step completion. The prefix/postfix pair also carries the
        // maker mark across the final step's template-clone swap (workpieceSlot.Itemstack =
        // recipe.Output.ResolvedItemStack.Clone(), decompile :908): without it, a piece
        // stamped at creation and then cold-worked arrived at the annealer bare.
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityWorkbench", "TryCompleteStep", nameof(WorkbenchMarkPrefix), nameof(WorkbenchPostfix), "GLA workbench");

        // Annealing: the load prefix stamps the window + maker on the held raw piece; the take
        // postfix credits a retrieved CONVERTED piece.
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityAnnealer", "TryInteract", nameof(AnnealPrefix), nameof(AnnealPostfix), "GLA annealing");

        // The thermal window read (the single static shatter funnel).
        var ts = AccessTools.TypeByName("GlassMaking.Common.GlassShatter");
        var ms = ts == null ? null : AccessTools.DeclaredMethod(ts, "ShouldShatter");
        if (ms != null)
        {
            harmony.Patch(ms, postfix: new HarmonyMethod(AccessTools.Method(typeof(GlaPatches), nameof(ShatterPostfix))));
            TcmLog.Info(api, "GLA thermal window hooked (GlassShatter.ShouldShatter)");
        }
        else TcmLog.Warn(api, "GLA thermal window seam not found (GlassShatter.ShouldShatter); window inactive");

        // Provenance re-stamp across the annealer's clone-based conversion.
        var ta = AccessTools.TypeByName("GlassMaking.Blocks.BlockEntityAnnealer");
        var ma = ta == null ? null : AccessTools.DeclaredMethod(ta, "OnCommonTick");
        if (ma != null)
        {
            harmony.Patch(ma,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(GlaPatches), nameof(TickSnapshotPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(GlaPatches), nameof(TickRestorePostfix))));
            TcmLog.Info(api, "GLA provenance re-stamp hooked (annealer OnCommonTick snapshot/restore)");
        }
        else TcmLog.Warn(api, "GLA provenance seam not found (BlockEntityAnnealer.OnCommonTick); anneal mark inactive");

        // The Glassmaker's Mark line is contributed to Engine.ProvenanceLine (see MarkLine
        // below); harmless without GLA since no stack carries the mark.
    }

    private static void Hook(ICoreAPI api, Harmony harmony, string typeName, string method, string? prefix, string? postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive"); return; }
        harmony.Patch(m,
            prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(GlaPatches), prefix)),
            postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(GlaPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    // ------------------------------------------------------------ grant helper

    private static void Grant(IPlayer? player, string tech, int contextHash)
    {
        if (player == null) return;
        Core?.Ledger?.Log(player, GlaDomain.Code, tech, contextHash);
    }

    private static IPlayer? PlayerOf(Entity? e) => (e as EntityPlayer)?.Player;
    private static IPlayer? PlayerOf(ItemSlot? slot) => (slot?.Inventory as InventoryBasePlayer)?.Player;

    // ------------------------------------------------------------ Phase 1 verbs

    /// <summary>Smeltery/hearth charge: credit the charger, deduped per smeltery per minute so
    /// loading 20 blend at once does not farm (the state machine bank is per session).</summary>
    public static void MeltPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(byPlayer, GlaDomain.TechMelting,
            HashCode.Combine("melt", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 60000)));
    }

    /// <summary>Freehand pipe recipe completion: the pipe is held, so the maker is the slot's
    /// owning player.</summary>
    public static void BlowFreehandPostfix(ItemSlot slot, bool isComplete)
    {
        if (!isComplete) return;
        IPlayer? p = PlayerOf(slot);
        if (p?.Entity?.World?.Side != EnumAppSide.Server) return;
        Grant(p, GlaDomain.TechBlowing,
            HashCode.Combine("blow", p.PlayerUID, (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
    }

    /// <summary>Blowing into a mold: same verb, mold completion hook. Also the maker stamp:
    /// TakeGlass hands the piece to the blower, so the fresh stack is in their inventory by
    /// postfix time and the mark names the hands that blew it (0.5, verb-review blocker 2).</summary>
    public static void BlowMoldPostfix(BlockEntity __instance, EntityAgent byEntity)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        IPlayer? maker = PlayerOf(byEntity);
        Grant(maker, GlaDomain.TechBlowing,
            HashCode.Combine("blowmold", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
        StampFreshInInventory(maker);
    }

    /// <summary>Ladle casting: credit at the successful collect of a hardened cast, and the
    /// maker stamp on the collected pieces (TryTakeContents gives them to the collector).</summary>
    public static void CastPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(byPlayer, GlaDomain.TechCasting,
            HashCode.Combine("cast", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
        StampFreshInInventory(byPlayer);
    }

    /// <summary>Stamps the maker's window + name on every unmarked raw-glass stack in the
    /// player's inventory. Called from the creation seams (mold blow, cast collect), where the
    /// just-produced pieces are certainly among them; any OTHER unmarked raw glass the maker
    /// happens to carry gets the same holder-stamp the old annealer-load rule would have given
    /// it eventually, so nothing is mislabeled that was ever labeled right.
    ///
    /// WHY AT CREATION (2026-08-21, verb-review blocker 2, the POT defect class). The stamp
    /// used to be written at annealer LOAD by whoever held the piece: the mark could name the
    /// wrong hand, a Grandmaster could proxy-stamp a novice's work by loading it, and the
    /// ruled carry-phase thermal window did not exist during the carry, which is exactly when
    /// cooling shatter is the live threat for a mold or cast piece travelling in inventory.
    /// (A freehand piece rides the PIPE to the annealer and is never a raw-glass stack, so the
    /// window never applied to it; freehand pieces annealed off the pipe carry no mark today,
    /// unchanged by this fix and noted as the remaining gap.)
    ///
    /// The CLONE-SWAP is load-bearing: glassmakingfork's TakeGlass hands out the recipe's
    /// ResolvedItemStack itself, un-cloned, in its give branch. Stamping that instance would
    /// poison the shared template for every future take of the recipe. Cloning the slot's
    /// stack before writing severs any shared reference. Inventory walk per the knit lesson:
    /// creative skipped by class name, and an inventory that refuses enumeration is skipped,
    /// never thrown out of.</summary>
    private static void StampFreshInInventory(IPlayer? maker)
    {
        if (maker?.InventoryManager?.Inventories == null) return;
        int tier = GlaDomain.LevelOf(maker);
        foreach (var inv in maker.InventoryManager.Inventories.Values)
        {
            if (inv == null || inv.ClassName == GlobalConstants.creativeInvClassName) continue;
            try
            {
                foreach (var slot in inv)
                {
                    var st = slot?.Itemstack;
                    if (st == null || !IsRawGlass(st) || st.Attributes.HasAttribute(TolAttr)) continue;
                    ItemStack clone = st.Clone();
                    clone.Attributes.SetInt(TolAttr, tier);
                    clone.Attributes.SetString(GlaByAttr, maker.PlayerUID);
                    clone.Attributes.SetString(GlaByNameAttr, maker.PlayerName);
                    slot!.Itemstack = clone;
                    slot.MarkDirty();
                }
            }
            catch (Exception e)
            {
                TcmLog.Warn(sapi, $"GLA stamp: inventory '{inv.ClassName}' could not be enumerated, skipped ({e.Message})");
            }
        }
    }

    /// <summary>Snapshot the workpiece's maker mark before the step. The final step swaps the
    /// workpiece for a clone of the recipe's output template, discarding input attributes (the
    /// clone-based pipeline, gla-glass-study claim 7), so the mark must be carried by hand.</summary>
    public static void WorkbenchMarkPrefix(BlockEntity __instance, out string?[]? __state)
    {
        __state = null;
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var slot = Traverse.Create(__instance).Property("workpieceSlot").GetValue<ItemSlot>();
        var attrs = slot?.Itemstack?.Attributes;
        if (attrs == null || !attrs.HasAttribute(TolAttr)) return;
        __state = new[]
        {
            attrs.GetInt(TolAttr).ToString(),
            attrs.GetString(GlaByAttr),
            attrs.GetString(GlaByNameAttr),
        };
    }

    /// <summary>Workbench cold-working: credit each completed step, and restore the maker mark
    /// if this step's completion replaced the workpiece with a bare template clone.</summary>
    public static void WorkbenchPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result, string?[]? __state)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(byPlayer, GlaDomain.TechWorkbench,
            HashCode.Combine("wb", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));

        if (__state == null) return;
        var slot = Traverse.Create(__instance).Property("workpieceSlot").GetValue<ItemSlot>();
        var st = slot?.Itemstack;
        // Two swaps can strip the mark: the FIRST step replaces a non-workpiece input with the
        // intermediate glassmaking:workpiece item, and the FINAL step swaps in the output
        // template clone (:908). Keying the restore on mark-absence covers both, and skips the
        // mid-recipe steps where the same stack persists with its mark intact.
        if (st == null || st.Attributes.HasAttribute(TolAttr)) return;
        if (int.TryParse(__state[0], out int tier)) st.Attributes.SetInt(TolAttr, tier);
        if (__state[1] != null) st.Attributes.SetString(GlaByAttr, __state[1]);
        if (__state[2] != null) st.Attributes.SetString(GlaByNameAttr, __state[2]);
        slot!.MarkDirty();
    }

    // ------------------------------------------------------------ annealing + the window stamp

    /// <summary>Load vs take: a held raw-glass piece means this interact LOADS the annealer.
    /// __state carries whether this was a load, so the postfix only credits a TAKE of a
    /// converted piece.
    ///
    /// SINCE 0.5 the stamp here is a FALLBACK, absent-only (verb-review blocker 2). The mark is
    /// written at the creation seams (StampFreshInInventory), so a stamped piece arriving here
    /// is already labeled with its true maker and this method must not touch it. The old rule
    /// was upgrade-only, which let a higher-ranked LOADER overwrite the actual maker's mark:
    /// that is the proxy-stamp exploit, and it dies here. What still lands in the fallback:
    /// pieces made before 0.5, and any path that never saw a creation stamp. Those get the
    /// holder's stamp exactly as they always did, which is the honest available answer.</summary>
    public static void AnnealPrefix(BlockEntity __instance, IPlayer byPlayer, ItemSlot slot, out bool __state)
    {
        __state = false;
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        if (!IsRawGlass(slot?.Itemstack)) return;
        __state = true; // a load

        var attrs = slot!.Itemstack.Attributes;
        if (attrs.HasAttribute(TolAttr)) return; // stamped at creation is final
        attrs.SetInt(TolAttr, GlaDomain.LevelOf(byPlayer));
        attrs.SetString(GlaByAttr, byPlayer.PlayerUID);
        attrs.SetString(GlaByNameAttr, byPlayer.PlayerName);
        slot.MarkDirty();
    }

    /// <summary>A TAKE that pulled a CONVERTED (annealed, no longer raw-glass) piece is a finished
    /// anneal: credit it. Retrieving an unfinished (still raw) piece grants nothing (anti-farm).</summary>
    public static void AnnealPostfix(BlockEntity __instance, IPlayer byPlayer, ItemSlot slot, bool __state, bool __result)
    {
        if (!__result || __state || __instance?.Api?.Side != EnumAppSide.Server) return;
        var stack = slot?.Itemstack;
        if (stack?.Collectible == null || IsRawGlass(stack)) return;
        Grant(byPlayer, GlaDomain.TechAnnealing,
            HashCode.Combine("anneal", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
    }

    // ------------------------------------------------------------ the thermal window read

    /// <summary>The single static shatter funnel (both the player scan and the ownerless annealer
    /// call it). If the piece carries a maker tolerance, recompute the deadline against the ranked
    /// threshold; unstamped pieces keep vanilla behaviour.</summary>
    public static void ShatterPostfix(IWorldAccessor world, ItemStack stack, ref bool __result)
    {
        if (stack?.Collectible == null || !IsRawGlass(stack)) return;
        var attrs = stack.Attributes;
        if (!attrs.HasAttribute(TolAttr)) return;
        float temp = stack.Collectible.GetTemperature(world, stack);
        __result = temp < GlaDomain.ShatterThreshold(attrs.GetInt(TolAttr));
    }

    // ------------------------------------------------------------ provenance re-stamp

    /// <summary>Snapshot the maker mark of every raw-glass slot before the tick (a conversion this
    /// tick clones the recipe output and drops it). Keyed by slot index.</summary>
    public static void TickSnapshotPrefix(BlockEntity __instance, out string?[]? __state)
    {
        __state = null;
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (Traverse.Create(__instance).Field("inventory").GetValue() is not IInventory inv) return;
        var snap = new string?[inv.Count];
        for (int i = 0; i < inv.Count; i++)
        {
            var st = inv[i]?.Itemstack;
            if (st != null && IsRawGlass(st) && st.Attributes.HasAttribute(GlaByAttr))
                snap[i] = $"{st.Attributes.GetString(GlaByAttr)}|{st.Attributes.GetString(GlaByNameAttr)}|{st.Attributes.GetInt(TolAttr)}";
        }
        __state = snap;
    }

    /// <summary>After the tick, any slot that converted (now holds a non-raw-glass output with no
    /// mark) inherits the snapshotted maker, so the finished vessel carries the Glassmaker's Mark.</summary>
    public static void TickRestorePostfix(BlockEntity __instance, string?[]? __state)
    {
        if (__state == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        if (Traverse.Create(__instance).Field("inventory").GetValue() is not IInventory inv) return;
        for (int i = 0; i < inv.Count && i < __state.Length; i++)
        {
            if (__state[i] == null) continue;
            var st = inv[i]?.Itemstack;
            if (st?.Collectible == null || IsRawGlass(st) || st.Attributes.HasAttribute(GlaByAttr)) continue;
            string[] p = __state[i]!.Split('|');
            if (p.Length < 3) continue;
            st.Attributes.SetString(GlaByAttr, p[0]);
            st.Attributes.SetString(GlaByNameAttr, p[1]);
            if (int.TryParse(p[2], out int tier)) st.Attributes.SetInt(GlaProvAttr, tier);
            inv[i].MarkDirty();
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Glassmaker's Mark line (Journeyman up): Blown by / Master-blown by / a
    /// flawless-work line. Non-stacking vessels carry it durably. Placement, order and spacing
    /// belong to <see cref="Engine.ProvenanceLine"/>; this only decides what GLA has to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(GlaByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        // The annealed piece carries the provenance tier; a still-raw piece carries the maker
        // tolerance tier. Either reads the same rank for the tooltip.
        int tier = attrs!.HasAttribute(GlaProvAttr)
            ? attrs.GetInt(GlaProvAttr) : attrs.GetInt(TolAttr);
        return
            tier >= Rank.Grandmaster ? Lang.Get("almanactcm:flawless-by", name)
            : tier >= Rank.Master ? Lang.Get("almanactcm:masterblown-by", name)
            : tier >= Rank.Journeyman ? Lang.Get("almanactcm:blown-by", name)
            : null;
    }
}
