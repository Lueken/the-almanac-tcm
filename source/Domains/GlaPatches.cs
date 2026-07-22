using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

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
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    private const string AnnealAttr = "glassmaking:anneal"; // vanilla raw-glass marker
    public const string TolAttr = "almanactcm:glatol";      // maker tier -> the shatter window
    public const string GlaByAttr = "almanactcm:glaby";     // maker uid
    public const string GlaByNameAttr = "almanactcm:glabyname";

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

        // Workbench cold-working step completion.
        Hook(api, harmony, "GlassMaking.Blocks.BlockEntityWorkbench", "TryCompleteStep", null, nameof(WorkbenchPostfix), "GLA workbench");

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

        // The provenance tooltip is an attribute patch below (applied by the Start PatchAll pass;
        // harmless without GLA since no stack carries the mark).
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

    /// <summary>Blowing into a mold: same verb, mold completion hook.</summary>
    public static void BlowMoldPostfix(BlockEntity __instance, EntityAgent byEntity)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(PlayerOf(byEntity), GlaDomain.TechBlowing,
            HashCode.Combine("blowmold", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
    }

    /// <summary>Ladle casting: credit at the successful collect of a hardened cast.</summary>
    public static void CastPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(byPlayer, GlaDomain.TechCasting,
            HashCode.Combine("cast", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
    }

    /// <summary>Workbench cold-working: credit each completed step.</summary>
    public static void WorkbenchPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        Grant(byPlayer, GlaDomain.TechWorkbench,
            HashCode.Combine("wb", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (serverWorld?.ElapsedMilliseconds ?? 0) / 1000));
    }

    // ------------------------------------------------------------ annealing + the window stamp

    /// <summary>Load vs take: a held raw-glass piece means this interact LOADS the annealer, and
    /// this is where the window + maker are stamped (upgrade-only, so a master's mark survives a
    /// novice's handling). __state carries whether this was a load, so the postfix only credits a
    /// TAKE of a converted piece.</summary>
    public static void AnnealPrefix(BlockEntity __instance, IPlayer byPlayer, ItemSlot slot, out bool __state)
    {
        __state = false;
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        if (!IsRawGlass(slot?.Itemstack)) return;
        __state = true; // a load

        int tier = GlaDomain.LevelOf(byPlayer);
        var attrs = slot!.Itemstack.Attributes;
        if (attrs.HasAttribute(TolAttr) && attrs.GetInt(TolAttr) >= tier) return; // upgrade-only
        attrs.SetInt(TolAttr, tier);
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
            if (int.TryParse(p[2], out int tier)) st.Attributes.SetInt("almanactcm:glaprovtier", tier);
            inv[i].MarkDirty();
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Glassmaker's Mark line (Journeyman up): Blown by / Master-blown by / a
    /// flawless-work line. Last priority, bottom of the tooltip after a blank line — the same
    /// placement as the Cook's/Potter's mark. Non-stacking vessels carry it durably.</summary>
    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.GetDescription))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    public static class ProvenancePatch
    {
        public static void Postfix(ItemStack __instance, ref string __result)
        {
            var attrs = __instance?.Attributes;
            string? name = attrs?.GetString(GlaByNameAttr);
            if (string.IsNullOrEmpty(name) || __result == null) return;
            // The annealed piece carries the provenance tier; a still-raw piece carries the maker
            // tolerance tier. Either reads the same rank for the tooltip.
            int tier = attrs!.HasAttribute("almanactcm:glaprovtier")
                ? attrs.GetInt("almanactcm:glaprovtier") : attrs.GetInt(TolAttr);
            string? line =
                tier >= GlaDomain.ProvGm ? Lang.Get("almanactcm:flawless-by", name)
                : tier >= GlaDomain.ProvMaster ? Lang.Get("almanactcm:masterblown-by", name)
                : tier >= GlaDomain.ProvJourneyman ? Lang.Get("almanactcm:blown-by", name)
                : null;
            if (line != null) __result = __result.TrimEnd() + "\n\n" + line + "\n";
        }
    }
}
