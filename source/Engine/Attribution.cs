using System;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Engine;

/// <summary>
/// The probe: run a vanilla getter with TCM's own contributions switched off, so a tooltip can
/// state what TCM actually changed instead of each domain narrating its own factor in isolation.
///
/// WHY THIS EXISTS (the bug it fixes, 2026-08-12): COO and FAR both postfix
/// <c>CollectibleObject.GetTransitionRateMul</c> and both multiply the same perish rate by 0.70
/// at Grandmaster. They COMPOSE: 0.70 x 0.70 = 0.49, roughly 51% slower. But each domain used
/// to print its own isolated "spoils 30% slower" clause on its provenance line. A GM-cooked,
/// GM-grown food told the player 30% twice for a real 51%, four rows under vanilla's own
/// "Fresh for N days" line, which already stated the composed truth correctly.
///
/// The fix is a deletion plus this probe. Vanilla already ships the single iterator:
/// <c>GetTransitionRateMul</c> is public virtual (vsapi Collectible.cs:2892) and vanilla calls it
/// from BOTH the describe path (:2223, inside AppendPerishableInfoText) and the grant path
/// (:3035). The grant composition was therefore always correct. Only the description was wrong.
/// </summary>
public static class Attribution
{
    // A depth counter, not a bool: probes must nest safely if one ever wraps another.
    [ThreadStatic] private static int depth;

    /// <summary>True while a probe is running. Every TCM postfix that contributes to a value a
    /// tooltip will attribute must return early on this, or the probe measures itself.</summary>
    public static bool Suppressed => depth > 0;

    /// <summary>Run <paramref name="probe"/> with TCM's contributions suppressed on this thread.</summary>
    public static T Without<T>(Func<T> probe)
    {
        depth++;
        try { return probe(); }
        finally { depth--; }
    }
}

/// <summary>
/// Annotates vanilla's own freshness line with TCM's true composed contribution.
///
/// Bracket, do not string-match. The prefix records <c>dsc.Length</c> and the postfix rewrites
/// exactly the region vanilla just wrote, so there is no locale dependency, no dependency on what
/// any other mod appended before or after, and no fail-open matching. The annotation lands on the
/// line that already carries the true number, which is the one place it cannot contradict.
///
/// Registered through <c>Try("perish-attribution", ...)</c> rather than by attribute, per
/// CONVENTIONS.md section 6: a bad seam must warn and skip, never abort Start.
/// </summary>
public static class PerishAttributionPatch
{
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var target = AccessTools.Method(typeof(CollectibleObject),
            nameof(CollectibleObject.AppendPerishableInfoText),
            new[] { typeof(ItemSlot), typeof(StringBuilder), typeof(IWorldAccessor), typeof(TransitionState), typeof(bool) });

        if (target == null)
        {
            TcmLog.Error(api, "perish attribution: CollectibleObject.AppendPerishableInfoText(5) not found; freshness annotation inactive");
            return;
        }

        harmony.Patch(target,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PerishAttributionPatch), nameof(Prefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(PerishAttributionPatch), nameof(Postfix))));

        TcmLog.Cat(api, "coo", "perish attribution hooked (freshness line carries the composed TCM delta)");
    }

    public static void Prefix(StringBuilder dsc, out int __state) => __state = dsc?.Length ?? 0;

    public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world,
                               TransitionState state, int __state)
    {
        try
        {
            if (dsc == null || world == null || state?.Props == null) return;
            if (state.Props.Type != EnumTransitionType.Perish) return;
            if (dsc.Length <= __state) return;                       // vanilla wrote nothing
            if (inSlot?.Inventory is CreativeInventoryTab) return;   // vanilla forces rate 1 here
            if (state.TransitionLevel > 0) return;                   // "spoiling X%", not a duration

            var coll = inSlot?.Itemstack?.Collectible;
            if (coll == null) return;

            float trueRate = coll.GetTransitionRateMul(world, inSlot, EnumTransitionType.Perish);
            if (trueRate <= 0) return;                               // "never spoils"; no delta to state

            float baseRate = Attribution.Without(() => coll.GetTransitionRateMul(world, inSlot, EnumTransitionType.Perish));
            if (baseRate <= 0) return;
            if (Math.Abs(trueRate - baseRate) < 0.0001f) return;      // TCM changed nothing

            // Vanilla printed FreshHoursLeft / rate. A lower rate means more time.
            double trueHours = state.FreshHoursLeft / trueRate;
            double baseHours = state.FreshHoursLeft / baseRate;
            double deltaHours = trueHours - baseHours;

            // Express the delta in the SAME unit vanilla chose, or the annotation reads wrong.
            // Selection mirrors Collectible.cs:2245-2266 exactly.
            float hoursPerDay = world.Calendar.HoursPerDay;
            float daysPerYear = world.Calendar.DaysPerYear;
            double delta =
                trueHours / hoursPerDay / daysPerYear >= 1.0 ? deltaHours / hoursPerDay / daysPerYear
                : trueHours > hoursPerDay ? deltaHours / hoursPerDay
                : deltaHours;

            string suffix = TcmTooltip.DeltaSuffix(delta);
            if (suffix.Length == 0) return;                           // under MinDelta: noise, not information

            // Splice into the region we own. Vanilla's write ends with a line terminator; put the
            // annotation before it so the line reads "Fresh for 12.3 days (+5.4)".
            string written = dsc.ToString(__state, dsc.Length - __state);
            string trimmed = written.TrimEnd('\r', '\n');
            if (trimmed.Length == 0) return;

            dsc.Length = __state;
            dsc.Append(trimmed).Append(suffix).AppendLine();
        }
        catch (Exception e)
        {
            var api = world?.Api;
            if (api != null)
                TcmLog.Error(api, $"perish attribution postfix failed ({e.Message}); freshness line left as vanilla wrote it");
        }
    }
}
