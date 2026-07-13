using System.Collections.Generic;
using Newtonsoft.Json;

namespace AlmanacTcm.Engine;

/// <summary>
/// One player's unconsolidated practice — the copybook page being written today.
/// Server-side only; zeroed exclusively by a real boundary consolidation (relogs
/// never touch it) and scattered (λ) by death. Also carries the rolling
/// per-technique banked history that elects the depth-phase dominant technique.
/// </summary>
public class PracticeLedger
{
    /// <summary>Raw accumulators x_t: domain code → technique → today's raw practice.</summary>
    public Dictionary<string, Dictionary<string, double>> Accumulators { get; set; } = new();

    /// <summary>Rolling banked-per-technique history: domain → technique → (boundary index →
    /// banked that day). Pruned to the dominant-window length at consolidation.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<long, double>>> History { get; set; } = new();

    /// <summary>Boundary index of the last consolidation. Compared against the CURRENT
    /// calendar-derived index; consolidation no-ops unless current is greater, which is
    /// the whole relog/concavity guard. Offline players consolidate exactly once at
    /// login no matter how many boundaries passed.</summary>
    public long LastConsolidatedBoundary { get; set; }

    /// <summary>Transient dedup ring (not persisted): recent context fingerprints.</summary>
    [JsonIgnore]
    public Queue<(string domain, string technique, int contextHash, long elapsedMs)> DedupRing { get; } = new();

    public Dictionary<string, double> AccumulatorsFor(string domainCode)
    {
        if (!Accumulators.TryGetValue(domainCode, out var techs))
        {
            techs = new Dictionary<string, double>();
            Accumulators[domainCode] = techs;
        }
        return techs;
    }

    public void RecordHistory(string domainCode, string technique, long boundary, double banked)
    {
        if (!History.TryGetValue(domainCode, out var techs))
        {
            techs = new Dictionary<string, Dictionary<long, double>>();
            History[domainCode] = techs;
        }
        if (!techs.TryGetValue(technique, out var days))
        {
            days = new Dictionary<long, double>();
            techs[technique] = days;
        }
        days[boundary] = banked;
    }

    /// <summary>Dominant technique = highest banked sum over the rolling window ending
    /// at the given boundary. Falls back to today's largest accumulator when the
    /// history is empty (a fresh depth-phase player's first day).</summary>
    public string? DominantTechnique(string domainCode, long currentBoundary, int windowDays)
    {
        string? best = null;
        double bestSum = 0;
        if (History.TryGetValue(domainCode, out var techs))
        {
            foreach (var (technique, days) in techs)
            {
                double sum = 0;
                foreach (var (boundary, banked) in days)
                {
                    if (boundary > currentBoundary - windowDays) sum += banked;
                }
                if (sum > bestSum) { bestSum = sum; best = technique; }
            }
        }
        if (best != null) return best;

        if (Accumulators.TryGetValue(domainCode, out var accs))
        {
            double bestX = 0;
            foreach (var (technique, x) in accs)
            {
                if (x > bestX) { bestX = x; best = technique; }
            }
        }
        return best;
    }

    public void PruneHistory(long currentBoundary, int windowDays)
    {
        foreach (var techs in History.Values)
        {
            foreach (var days in techs.Values)
            {
                List<long>? stale = null;
                foreach (long boundary in days.Keys)
                {
                    if (boundary <= currentBoundary - windowDays) (stale ??= new List<long>()).Add(boundary);
                }
                if (stale != null) foreach (long b in stale) days.Remove(b);
            }
        }
    }

    /// <summary>Death scatter: every domain's pending practice loses fraction λ.
    /// Banked XP is structurally out of reach from here — that's the design.</summary>
    public void Scatter(double lambda)
    {
        foreach (var techs in Accumulators.Values)
        {
            foreach (string technique in new List<string>(techs.Keys))
            {
                techs[technique] *= 1.0 - lambda;
            }
        }
    }

    public void ClearDay()
    {
        Accumulators.Clear();
    }
}
