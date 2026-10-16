using System.Collections.Generic;
using Vintagestory.API.Server;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Config;

/// <summary>
/// The boot audit for `M`, the most error-prone value in the whole config (LGD-292). Two checks,
/// both read-only: NOTHING here changes a configured value, ever. The bespoke clamps in
/// EngMilestones and FisTrapPatches do the changing, each for the one domain somebody thought to
/// write a clamp for. This is the net underneath every domain nobody wrote one for.
///
/// WHY M AND NOT ANY OTHER KNOB. A domain's daily value splits across M verbs, so M above the verbs
/// a pack can reach makes part of every day unearnable by any route. It reads to a player as "this
/// trade pays far too little" and to an operator as nothing at all, because nothing logged it. It
/// has shipped wrong three times: PAN (0.5.15, player-reported), ENG (0.4.x, caught in review), FIS
/// (0.5.16, player-reported). Each time the arithmetic was visible at boot and nobody could see it.
///
/// CHECK 1, REACHABILITY. Count the verbs whose grant seams can attach on this pack (VerbReach) and
/// compare against the live M. Runs on a delay, AFTER the two clamps, so a clamped domain is judged
/// on its effective M rather than its configured one.
///
/// CHECK 2, DRIFT, which matters more. M takes no part in the three-way config merge, so a changed
/// shipped default reaches new worlds only and an existing server keeps whatever its file holds,
/// silently, forever. Three manual edits were needed across two releases (PAN to 2, FIS to 2, ANI
/// to 4) and every one of them was a person remembering. This compares live against shipped and
/// says so when they differ.
///
/// WHAT CHECK 2 CANNOT DO, STATED PLAINLY. Live != shipped has two causes: the operator tuned it,
/// or a shipped default never landed. Telling them apart needs a merge baseline, and M has none.
/// So the warning fires on BOTH and names both causes rather than guessing between them. A
/// heuristic here (say, "it matches an older shipped default, so it is probably stale") would
/// mislabel the operator who deliberately chose that same number, and a diagnostic that tells you
/// confidently wrong things is worse than one that tells you less.
/// </summary>
public static class MConfigAudit
{
    /// <summary>How long after startup the reachability pass runs. Must stay above the 5000 ms
    /// both bespoke clamps use (EngMilestones.cs:73, FisTrapPatches.cs:83), or the audit would
    /// warn about a shortfall a clamp was in the middle of fixing.</summary>
    private const int ReachabilityDelayMs = 8000;

    /// <summary>Configured M per domain as read off disk, captured before the deferred clamps can
    /// move it. The reachability pass compares against it so it can say "clamped from" rather than
    /// reporting a clamp as a mystery.</summary>
    private static readonly Dictionary<string, int> configuredM = new();

    // ---------------------------------------------------------------- check 2: shipped-default drift

    /// <summary>
    /// Compare live M against the shipped default and warn on any difference.
    ///
    /// CALL THIS SYNCHRONOUSLY FROM StartServerSide. It must observe M as the operator's file holds
    /// it, and the two clamps mutate M in memory from a 5000 ms callback; a callback cannot run
    /// before mod startup returns, so calling here is what makes "live" mean "on disk".
    /// </summary>
    public static void AuditDrift(ICoreServerAPI sapi, DomainSetTemplate template,
        Dictionary<string, DomainConfig> configs)
    {
        configuredM.Clear();
        int drifted = 0, dormant = 0;

        foreach (Domain domain in template.Domains)
        {
            if (!configs.TryGetValue(domain.Code, out var dc)) continue;
            configuredM[domain.Code] = dc.M;

            // A dormant domain grants nothing on this pack, so its M cannot affect anybody this
            // session. Counted, not warned about: a flood of findings nobody can act on is how a
            // useful warning gets trained into background noise.
            if (!domain.Enabled) { dormant++; continue; }

            if (!Engine.LedgerSystem.DefaultFactories.TryGetValue(domain.Code, out var factory)) continue;
            int shipped = factory().M;
            if (dc.M == shipped) continue;

            drifted++;
            TcmLog.Warn(sapi,
                $"config: {domain.Code} m is {dc.M}, the shipped default is {shipped}. m takes no part in the "
                + "three-way merge, so this is EITHER deliberate tuning on this server OR a shipped default "
                + "that never reached this file. Those two cannot be told apart here, because m has no merge "
                + $"baseline. If you set it, ignore this line. If you did not, write {shipped} into "
                + $"ModConfig/almanactcm/{domain.Code}.json and restart; leaving it "
                + (dc.M > shipped
                    ? "high splits the day across more verbs than the current balance expects."
                    : "low pays a full day for fewer verbs than the current balance expects."));
        }

        TcmLog.Cat(sapi, TcmLog.Config,
            $"m drift audit: {configuredM.Count - dormant} live domain(s) checked, {drifted} differ from the "
            + $"shipped default, {dormant} dormant and skipped");
    }

    // ---------------------------------------------------------------- check 1: verb reachability

    /// <summary>Queue the reachability pass. Deferred past both clamps on purpose; see
    /// <see cref="ReachabilityDelayMs"/>.</summary>
    public static void RegisterReachabilityPass(ICoreServerAPI sapi, DomainSetTemplate template,
        Dictionary<string, DomainConfig> configs)
        => sapi.Event.RegisterCallback(_ => AuditReachability(sapi, template, configs), ReachabilityDelayMs);

    private static void AuditReachability(ICoreServerAPI sapi, DomainSetTemplate template,
        Dictionary<string, DomainConfig> configs)
    {
        int shortfalls = 0, dormant = 0, live = 0, verbless = 0;
        var unaudited = new List<string>();

        foreach (Domain domain in template.Domains)
        {
            if (!configs.TryGetValue(domain.Code, out var dc)) continue;
            if (!domain.Enabled) { dormant++; continue; }

            // A config with no techniques is the first-boot scaffold a roster domain gets before
            // its build ships. It has no verbs at all, so its m is meaningless rather than wrong,
            // and warning here would be the audit's one route to a finding nobody can act on.
            // Every current domain ships techniques, so this is a guard, not a live case.
            if (dc.Techniques.Count == 0) { verbless++; continue; }
            live++;

            int reachable = 0, assumed = 0;
            var blocked = new List<string>();

            foreach (var (technique, _) in dc.Techniques)
            {
                var gates = VerbReach.GatesFor(domain.Code, technique);
                if (gates == null)
                {
                    // No table entry. Counted REACHABLE so a verb shipped after this table was
                    // written cannot invent a shortfall, and named below so the gap gets filled.
                    reachable++;
                    assumed++;
                    unaudited.Add($"{domain.Code}/{technique}");
                    continue;
                }
                if (VerbReach.IsReachable(sapi, gates)) reachable++;
                else blocked.Add($"{technique} ({VerbReach.Missing(gates)})");
            }

            if (dc.M <= reachable) continue;

            shortfalls++;
            int lost = dc.M - reachable;
            configuredM.TryGetValue(domain.Code, out int wasConfigured);
            string clamped = wasConfigured != 0 && wasConfigured != dc.M
                ? $" (already clamped from {wasConfigured} at boot)"
                : "";

            // Two shapes of the same shortfall. Mod-blocked verbs are specific to this pack and can
            // be fixed from either end; m above the number of techniques the domain even DEFINES
            // needs no table and admits no doubt, because no pack of any size can satisfy it.
            string cause = blocked.Count > 0
                ? $"Unreachable: {string.Join("; ", blocked)}. Fix either way: lower m to {reachable} in "
                  + $"ModConfig/almanactcm/{domain.Code}.json, or install a mod that supplies those verbs."
                : $"No verb is mod-blocked here: m simply exceeds the {dc.Techniques.Count} technique(s) "
                  + $"the domain defines, so NO pack can satisfy it. Lower m to at most {reachable} in "
                  + $"ModConfig/almanactcm/{domain.Code}.json.";
            string caveat = assumed > 0
                ? $" {assumed} verb(s) have no recorded gate and were counted as reachable, so the real "
                  + "shortfall can only be larger than this."
                : "";

            TcmLog.Warn(sapi,
                $"config: {domain.Code} m is {dc.M}{clamped} but only {reachable} of {dc.Techniques.Count} "
                + $"verb(s) are reachable on this pack. A domain day splits across m verbs, so {lost} of "
                + $"{dc.M} daily share(s) cannot be earned by any route and the trade reads as paying far too "
                + $"little. {cause} Nothing was changed.{caveat}");
        }

        string skipped = $"{dormant} dormant"
            + (verbless > 0 ? $" and {verbless} without techniques yet" : "") + ", skipped";
        if (shortfalls == 0)
            TcmLog.Cat(sapi, TcmLog.Config,
                $"m reachability audit: {live} live domain(s) clear, {skipped}");
        else
            TcmLog.Warn(sapi,
                $"config: {shortfalls} of {live} live domain(s) have an m above their reachable verb count "
                + "(detailed above). Nothing was changed; this audit only reports.");

        // A verb with no gate entry is the one way this audit goes quietly blind, so it is said out
        // loud rather than folded into the per-domain lines.
        if (unaudited.Count > 0)
            TcmLog.Warn(sapi,
                $"config: {unaudited.Count} technique(s) have no entry in VerbReach and were assumed "
                + $"reachable: {string.Join(", ", unaudited)}. Add them, or the m audit understates any "
                + "shortfall in those domains.");
    }
}
