using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Engine;

/// <summary>
/// The single arbiter for the domain provenance block on <c>ItemStack.GetDescription</c>.
///
/// WHY THIS EXISTS (2026-08-13). Seven domains each carried their own Harmony postfix on
/// <c>ItemStack.GetDescription</c> (ALC, BRE, COO, FAR, GLA, POT, TAI). Every one declared
/// <c>[HarmonyPriority(Priority.Last)]</c> and ended with the same statement:
///
///     __result = __result.TrimEnd() + "\n\n" + line + "\n";
///
/// Three things were wrong with that, in order of how much they bite.
///
/// ORDER WAS NOT OURS TO SET. Seven patches at IDENTICAL priority give Harmony's sort nothing to
/// sort by, so it falls back to the order patches were registered, which under PatchAll is
/// <c>Assembly.GetTypes()</c> order. The CLR does not guarantee that ordering. In practice it
/// tracks metadata token order, which means it is stable for a given build and can shift when an
/// unrelated class is added elsewhere. A crock carrying both a Potter's mark and a Cook's mark
/// rendered them in whichever order that lottery happened to produce, and nothing in the mod
/// could state which order was intended.
///
/// SPACING COMPOUNDED. Each patch prepended its own blank line, so two marks rendered as two
/// stranded paragraphs instead of one block, and three as three. The tooltip grew loosest on
/// exactly the items that had earned the most marks.
///
/// NOTHING COULD SEE THE WHOLE. No code ever held all the marks on an item at once, so there was
/// nowhere to put a cross-domain rule: no cap on how many marks show, no ordering by meaning, and
/// no way to state an effect that spans two domains. That last point is why the REVISIT block in
/// PotBonusPatches had to end in "decide later" rather than a fix: POT's preservation figure and
/// COO's are true separately and unstated together, and there was no vantage point from which to
/// compose them. This class is that vantage point.
///
/// Registered through <c>Try("provenance-line", ...)</c> rather than by attribute, per
/// CONVENTIONS.md section 6. That also retires seven annotation classes from the one unguarded
/// patch surface in the mod: <c>harmony.PatchAll</c> is the single patch call NOT wrapped in
/// <c>Try</c>, so an annotated target that ever fails to resolve aborts Start before any guarded
/// patch runs.
/// </summary>
public static class ProvenanceLine
{
    /// <summary>A domain's contribution: the finished, localized line for this stack, or null
    /// when the stack carries no mark from that domain. A pure read, no side effects.</summary>
    public delegate string? Contributor(ItemStack stack);

    /// <summary>
    /// The marks, in DISPLAY ORDER. Array order IS the order. There is deliberately no numeric
    /// rank column: a number whose only job is to restate the array position is a second source
    /// of truth that can disagree with the first.
    ///
    /// The order follows the chain of making. What the material was, then what was done to it,
    /// then what holds it. Vessels sit last because a crock of stew is a stew first and a crock
    /// second. Moving a row changes every affected tooltip, so move it deliberately.
    /// </summary>
    private static readonly (string Domain, Contributor Line)[] Contributors =
    {
        ("FAR", Domains.FarBonusPatches.MarkLine),   // grown
        ("COO", Domains.CooBonusPatches.MarkLine),   // cooked
        ("BRE", Domains.BrePatches.MarkLine),        // cured or fermented
        ("ALC", Domains.AlcBrandPatches.MarkLine),   // compounded
        ("TAI", Domains.TaiMarkPatches.MarkLine),    // sewn
        ("POT", Domains.PotBonusPatches.MarkLine),   // the vessel holding it
        ("GLA", Domains.GlaPatches.MarkLine),        // the glass holding it
    };

    private static ICoreAPI? api;

    /// <summary>Domains whose contributor has thrown this session. Bounded by the contributor
    /// count, so it cannot grow.</summary>
    private static readonly HashSet<string> faulted = new();

    public static void PatchConditional(ICoreAPI coreApi, Harmony harmony)
    {
        api = coreApi;

        var target = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.GetDescription));
        if (target == null)
        {
            TcmLog.Error(coreApi, "provenance line: ItemStack.GetDescription not found; every domain mark is inactive");
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(ProvenanceLine), nameof(Postfix))) { priority = Priority.Last });

        TcmLog.Info(coreApi, $"provenance line hooked ({Contributors.Length} domain marks, one ordered block)");
    }

    public static void Postfix(ItemStack __instance, ref string __result)
    {
        if (__instance == null || __result == null) return;

        List<string>? lines = null;
        foreach ((string domain, Contributor line) in Contributors)
        {
            string? text;
            try
            {
                text = line(__instance);
            }
            catch (Exception e)
            {
                // One domain's bad read must not cost the other six their marks. Under the old
                // shape a throwing postfix took out every patch after it in the chain, which was
                // invisible because nobody could tell a missing mark from an unmarked item.
                //
                // Logged ONCE per domain per session: this runs on every tooltip draw, so an
                // unguarded log here would bury the file within seconds of hovering the item.
                if (api != null && faulted.Add(domain))
                    TcmLog.Error(api, $"provenance line: {domain} threw ({e.Message}); its mark is skipped for the rest of this session");
                continue;
            }

            if (!string.IsNullOrEmpty(text)) (lines ??= new List<string>()).Add(text!);
        }

        if (lines == null) return;

        // ONE blank line before the block, single-spaced within it, whatever the count.
        __result = __result.TrimEnd() + "\n\n" + string.Join("\n", lines) + "\n";
    }
}
