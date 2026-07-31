using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// BEE, "Beekeeping" (the Hive Warden), the roster's first any-of conditional domain
/// (bee-domain-design.md, all rulings 2026-07-28/30). Registers enabled when OrekiWoof's
/// Beehives OR From Golden Combs is present; on bare vanilla the domain sits disabled and
/// beekeeping stays FAR technique #10 exactly as FarPatches has wired it since 0.3.135
/// (RULED 2026-07-30, superseding the ANI-host plan).
///
/// The RouteBeekeeping switch is realized at PATCH TIME, not per grant: FarPatches skips
/// its beekeeping seams when this domain is enabled and BeePatches takes them over, so
/// exactly one owner ever holds a seam and nothing can double-grant.
///
/// Phase 1 (this build): the practice verbs + the Axis 1 penalty band (sting, crushed
/// comb). Axes 2-4 and 6 (feed economy, colony survival, yield, the Keeper's Eye) are the
/// follow-up pass; Axis 5 is RULED EMPTY (material access is the gate).
/// </summary>
public static class BeeDomain
{
    public const string Code = "BEE";

    public const string ModOreki = "orekiwoofsbeehives";
    public const string ModFgc = "fromgoldencombs";

    // The four ruled verbs. Wintering is Oreki-only; rendering is vanilla-floored
    // (the fruit press); hiving and combwork exist on both hive mods.
    public const string TechHiving = "hiving";         // colony into a box (populate / stock retrieval)
    public const string TechCombwork = "combwork";     // filled frames out, ripe skeps down: the spine
    public const string TechWintering = "wintering";   // feed frames in before the cold (oreki)
    public const string TechRendering = "rendering";   // honeycomb through the press (vanilla)

    // ---- Axis 1 knobs (the only bonus band this build). Live in ModConfig/almanactcm/BEE.json.
    /// <summary>The sting: factor an Untrained keeper's skep-break beemob roll is raised by
    /// (vanilla beemobSpawnChance, default 0.4). Clears at Novice I and never rises again.</summary>
    public const string StingUntrained = "stingUntrained";
    /// <summary>Crushed comb: chance an Untrained skep harvest mishandles the comb and one
    /// honeycomb is lost from the drop (never below one). Clears at Novice I.</summary>
    public const string CrushChanceUntrained = "crushChanceUntrained";
    /// <summary>The focus grace: seconds after a crushed comb in which the penalty band stands
    /// down, so one bad moment cannot cascade through a harvest (the 0.4.10 anvil precedent).</summary>
    public const string FocusCooldownSeconds = "focusCooldownSeconds";

    /// <summary>The detection pass: the domain (and its seams, and FAR's stand-down) all key
    /// off this one test, per the conditional-registration rule.</summary>
    public static bool Enabled(ICoreAPI api)
        => api.ModLoader.IsModEnabled(ModOreki) || api.ModLoader.IsModEnabled(ModFgc);

    /// <summary>Server-side BEE level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.ServerInstance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // RULED: m = 3 against 4 techniques, the first deliberate departure from "m = count".
        // Combwork and rendering are sequential halves of one harvest day; m = 4 would let a
        // single afternoon claim two full technique caps for one day's work.
        M = 3,
        // Husbandry of a colony sited in a cultivated landscape; the sigma spillover channel,
        // fading across Journeyman. NO co-grants in either direction (the pollination demotion).
        Adjacency = new List<string> { "FAR", "ANI" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Small K for the rare, high-judgment acts: one real act banks most of the share.
            [TechHiving] = new() { Raw = 6, K = 8 },
            [TechWintering] = new() { Raw = 6, K = 8 },
            // The practice spine: larger K so an eight-frame pull saturates rather than paying
            // eight times over, per-frame contexts notwithstanding.
            [TechCombwork] = new() { Raw = 3, K = 24 },
            [TechRendering] = new() { Raw = 2, K = 15 },
        },
        Bonus = new Dictionary<string, double>
        {
            [StingUntrained] = 1.75,
            [CrushChanceUntrained] = 0.35,
            [FocusCooldownSeconds] = 5,
        },
    };
}
