using System;
using System.Collections;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO Phase 2b — the ImmersiveWoodworking station verbs. IW 1.1.0 is a COMPLETELY SEPARATE system
/// from IDG: no shared recipe registry, no `ToolMode`, no `SpawnOutput`, and zero Harmony of its own
/// — so <see cref="WooIdgPatches"/> reaches none of it. Each IW station is its own BlockEntity with
/// its own completion sink, resolved here by name at runtime (IW-conditional; absent-IW loads clean).
///
/// Three sinks are wired, all server-side, all crediting ONCE PER WHOLE LOG (seam study 2026-07-16):
///   • Manual chopping block — <c>BlockEntityChoppingBlock.Chop(IPlayer)</c>. A whole log fires Chop
///     up to THREE times (log→2 half-logs, then each half-log→firewood via MakeHalfLogs/GetChopResult),
///     so we credit ONLY the full-log input stage → exactly one chopping credit per log, matching IDG
///     and the powered chopper. TechChopping.
///   • Sawhorse sawing — <c>BlockEntitySawhorse.ConsumeLog()</c>, the once-per-log boundary (NOT the
///     per-stage CompleteSawStage, which fires StageFractions.Length× per log). Two-person tool: the
///     sawyers live in the private `sawEnds` dict, credited each once. TechSawing.
///   • Sawhorse debarking — <c>BlockEntitySawhorse.CompleteDebark()</c>, once per log (BarkStageFractions
///     is a single stage). RULED 2026-07-16: FOLD INTO HEWING (IDG already treats bark-stripping as its
///     'hewing' tool mode, so this stays consistent with the existing model). TechHewing.
///
/// Deliberately NOT wired (seam study, §Wiring): the powered chopper and powered sawmill (CompletePass
/// has no player and IW stores no placer/owner UID — no honest attribution, and they are AFK
/// automation), and driftwood hand-snapping (RULED uncredited — trivial yield, not a craft).
/// </summary>
public static class WooIwPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("immersivewoodworking")) return;

        var chopBlock = AccessTools.TypeByName("ImmersiveWoodworking.BlockEntityChoppingBlock");
        var sawhorse = AccessTools.TypeByName("ImmersiveWoodworking.BlockEntitySawhorse");

        var chop = chopBlock == null ? null : AccessTools.Method(chopBlock, "Chop", new[] { typeof(IPlayer) });
        var consumeLog = sawhorse == null ? null : AccessTools.Method(sawhorse, "ConsumeLog");
        var completeDebark = sawhorse == null ? null : AccessTools.Method(sawhorse, "CompleteDebark");

        if (chop == null && consumeLog == null && completeDebark == null)
        {
            TcmLog.Warn(api, "immersivewoodworking present but no station sink (Chop/ConsumeLog/CompleteDebark) was found; WOO IW verbs inactive");
            return;
        }

        if (chop != null)
            harmony.Patch(chop, prefix: new HarmonyMethod(AccessTools.Method(typeof(ChoppingBlockPatch), "Prefix")));
        // Sawing MUST be a prefix: ConsumeLog's ResetSawState() calls sawEnds.Clear() on its way out,
        // so a postfix would read an empty sawyer dict.
        if (consumeLog != null)
            harmony.Patch(consumeLog, prefix: new HarmonyMethod(AccessTools.Method(typeof(SawingPatch), "Prefix")));
        if (completeDebark != null)
            harmony.Patch(completeDebark, prefix: new HarmonyMethod(AccessTools.Method(typeof(DebarkPatch), "Prefix")));

        TcmLog.Info(api, "WOO station verbs hooked to ImmersiveWoodworking (chopping block, sawhorse sawing + debark)");
    }

    /// <summary>Manual chopping block. Credit only the full-log input stage (log→half-logs), skipping
    /// the half-log→firewood and firewood→stick stages, so one whole log = one chopping credit.</summary>
    public static class ChoppingBlockPatch
    {
        public static void Prefix(object __instance, IPlayer byPlayer)
        {
            if (byPlayer == null || (__instance as BlockEntity)?.Api?.Side != EnumAppSide.Server) return;

            // Read the input BEFORE Chop's TakeOutWhole consumes it (private `inventory` field).
            InventoryBase? inv = Traverse.Create(__instance).Field("inventory").GetValue<InventoryBase>();
            ItemStack? input = inv != null && inv.Count > 0 ? inv[0]?.Itemstack : null;
            if (!IsFullLog(input)) return; // half-log / firewood stages are the same log, already credited

            Credit((BlockEntity)__instance, byPlayer, WooDomain.TechChopping);
        }
    }

    /// <summary>Sawhorse, once per log at ConsumeLog. Credits every active sawyer (the pit saw is a
    /// two-person tool; both did the work).</summary>
    public static class SawingPatch
    {
        public static void Prefix(object __instance) => CreditHolders(__instance, "sawEnds", WooDomain.TechSawing);
    }

    /// <summary>Sawhorse debarking, once per log. Folded into hewing (ruled 2026-07-16).</summary>
    public static class DebarkPatch
    {
        public static void Prefix(object __instance) => CreditHolders(__instance, "barkSides", WooDomain.TechHewing);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Reads a private Dictionary&lt;int, SawyerHold&gt; of active tool-holders (sawEnds /
    /// barkSides) and credits each holder once. SawyerHold is a private nested class, so its Uid is
    /// read reflectively.</summary>
    private static void CreditHolders(object beObj, string dictField, string technique)
    {
        if (beObj is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
        if (Traverse.Create(beObj).Field(dictField).GetValue() is not IDictionary holds || holds.Count == 0) return;

        foreach (object? hold in holds.Values)
        {
            if (hold == null) continue;
            string? uid = Traverse.Create(hold).Field("Uid").GetValue<string>();
            if (string.IsNullOrEmpty(uid)) continue;
            IPlayer? player = be.Api.World.PlayerByUid(uid);
            if (player != null) Credit(be, player, technique);
        }
    }

    /// <summary>The one credit path. contextHash = technique + a 1s bucket (the WooIdgPatches /
    /// MET-assembly precedent), deliberately NOT position: processing repeats at one fixed station,
    /// so a position key would dedup the whole verb away. Every completion consumes a real log — the
    /// grind ceiling is K, not the dedup guard.</summary>
    private static void Credit(BlockEntity be, IPlayer player, string technique)
    {
        Core?.Ledger?.Log(player, WooDomain.Code, technique,
            HashCode.Combine(technique, be.Api.World.ElapsedMilliseconds / 1000));
    }

    /// <summary>Replicates IW's private static BlockEntityChoppingBlock.IsFullLog: the input block's
    /// first code part is "log" or "debarkedlog" (half-logs are items with first part "halflog").</summary>
    private static bool IsFullLog(ItemStack? stack)
    {
        string? part = stack?.Block?.Code?.FirstCodePart();
        return part == "log" || part == "debarkedlog";
    }
}
