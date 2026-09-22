using System;
using System.Collections.Generic;

namespace AlmanacTcm.Engine;

/// <summary>
/// The say-nay curve (RULED 2026-09-22 for FOR gathering, generalized the same day when the
/// Frostbound reports showed the identical grind wearing three coats: sapling fields for
/// foraging, all-day knapping for mining, craft-and-uncraft at the bench for metalworking).
///
/// One shape for all of them: the first `free` acts inside a scope each in-game day pay full
/// raw, every act past that pays decay^n, so at the 0.1 default the tenth is one millionth.
/// Repetition itself stops paying; the caller decides what "the same act" means by what it
/// puts in the scope key (FOR scopes per species, MIN and MET per technique), and the ledger's
/// existing saturation and dedup stay untouched underneath.
///
/// The counter advances even when the ledger's 90s ring later dedups the credit, which is the
/// cheaper honesty: an act performed is an act counted. Transient by design (a restart
/// forgives at most one extra free count per scope) and cleared on Dispose, because a second
/// world in the same process has its own calendar. Stale-day entries are overwritten in place
/// on next touch, so the dictionary holds one entry per (player, scope) pair, ever.
/// </summary>
public static class RepeatDecay
{
    private static readonly Dictionary<(string uid, string scope), (int day, int count)> counts = new();

    public static void ClearCaches() => counts.Clear();

    public static double Mult(string uid, string scope, int day, int free, double decay)
    {
        var key = (uid, scope);
        int count = counts.TryGetValue(key, out var e) && e.day == day ? e.count : 0;
        counts[key] = (day, count + 1);

        if (count < free) return 1.0;
        return Math.Pow(decay, count - free + 1);
    }
}
