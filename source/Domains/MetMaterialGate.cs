using System.Collections.Generic;
using AlmanacTcm.Leveling;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// The MET material gate (rank-bonus-design.md §162, Axis 5): a metal cannot be
/// SMELTED, FORMED, or CAST below the MET rank its tier demands. Assembly — hafting
/// a finished head to a handle — is deliberately NOT gated: a novice may buy a
/// master's iron head and fit it themselves (RULED 2026-07-14). The gate keys on a
/// metal→tier classification, never on bespoke per-recipe checks.
///
/// Tier→rank is §162 design law; the metal→tier map is the classification ruled
/// 2026-07-14 against the full 24-metal vanilla roster. NOTE: vanilla's own per-metal
/// `tier` field is MINING hardness (it lists tin at 4, steel-level, and nickel at 3),
/// so it is NOT usable as a smithing-progression tier — this map is by role instead.
/// Server-owned toggle <see cref="Config.TcmGlobalConfig.MaterialGateMET"/>; bespoke to
/// MET so other domains (ALC's chromium/titanium chemistry) get their own gate later.
/// </summary>
public static class MetMaterialGate
{
    // §162 tier thresholds, as levels: copper 0, bronze Apprentice I, iron/nickel/
    // meteoric Journeyman I, steel + non-tool exotics Master I.
    public const int Untrained = 0;
    public const int ApprenticeI = Domain.SubLevelsPerTier + 1;       // 5
    public const int JourneymanI = 2 * Domain.SubLevelsPerTier + 1;   // 9
    public const int MasterI = 3 * Domain.SubLevelsPerTier + 1;       // 13

    /// <summary>metal code → required MET level. Absent = UNMAPPED: logged once and
    /// defaulted (see <see cref="RequiredLevel"/>), never silently gated wrong.</summary>
    private static readonly Dictionary<string, int> Required = new()
    {
        // Tier I — always workable: the start metal + soft / precious / ingredient /
        // currency metals. Tin/zinc/bismuth MUST stay free or bronze is unmakeable;
        // molybdochalkos sits below copper (vanilla tier 0); cupronickel is an easy
        // alloy though its nickel input is itself gated.
        ["copper"] = Untrained,
        ["lead"] = Untrained,
        ["tin"] = Untrained,
        ["zinc"] = Untrained,
        ["bismuth"] = Untrained,
        ["silver"] = Untrained,
        ["gold"] = Untrained,
        ["electrum"] = Untrained,
        ["cupronickel"] = Untrained,
        ["molybdochalkos"] = Untrained,

        // Tier II — Apprentice (bronze alloys). Brass is a tool-capable bronze-equivalent
        // and must gate here or it becomes a bronze-gate bypass.
        ["tinbronze"] = ApprenticeI,
        ["bismuthbronze"] = ApprenticeI,
        ["blackbronze"] = ApprenticeI,
        ["brass"] = ApprenticeI,

        // Tier III — Journeyman (iron age). Nickel gates here: its ore already needs
        // iron-tier mining, so tier I would be inconsistent (RULED 2026-07-14).
        ["iron"] = JourneymanI,
        ["meteoriciron"] = JourneymanI,
        ["nickel"] = JourneymanI,

        // Tier IV — Master (steel + non-tool exotics). Chromium/titanium/etc. arrive via
        // ALC chemistry, not MET smelting; parked at Master pending the ALC gate review
        // (RULED 2026-07-14), so they are never left ungated in the meantime.
        ["steel"] = MasterI,
        ["stainlesssteel"] = MasterI,
        ["chromium"] = MasterI,
        ["platinum"] = MasterI,
        ["titanium"] = MasterI,
        ["uranium"] = MasterI,
    };

    /// <summary>Item-code prefixes whose last '-' segment names the metal directly.</summary>
    private static readonly string[] MetalForms =
    {
        "ingot", "metalplate", "plate", "nugget", "metalbit", "metalmass", "metalpile",
        "workitem", "metalblock", "anvil", "toolmold", "ingotmold",
    };

    private static readonly HashSet<string> unmappedLogged = new();

    /// <summary>Required MET level to work a metal. Unmapped metals are logged once and
    /// fall back to <paramref name="unmappedDefault"/> (0 = allow), so a mod-added metal
    /// is never silently locked nor silently mis-gated — it shows up in the log first.</summary>
    public static int RequiredLevel(ICoreAPI api, string? metalCode, int unmappedDefault)
    {
        if (string.IsNullOrEmpty(metalCode)) return unmappedDefault;
        if (Required.TryGetValue(metalCode, out int lvl)) return lvl;
        if (unmappedLogged.Add(metalCode))
        {
            TcmLog.Cat(api, TcmLog.Hooks,
                $"material-gate: UNMAPPED metal '{metalCode}' → default level {unmappedDefault}; add it to the classification before it matters");
        }
        return unmappedDefault;
    }

    /// <summary>Best-effort metal code from a stack. Direct metal-named forms
    /// (ingot-/plate-/nugget-/metalmass-…-{metal}) resolve by their trailing segment;
    /// otherwise the metal its combustible SmeltedStack yields (ore, crushed ore,
    /// scrap, bloom). Null when the stack carries no resolvable metal.</summary>
    public static string? MetalOf(IWorldAccessor world, ItemStack? stack)
    {
        if (stack?.Collectible?.Code == null) return null;

        string path = stack.Collectible.Code.Path;
        int dash = path.LastIndexOf('-');
        if (dash >= 0 && dash < path.Length - 1)
        {
            string prefix = path.Substring(0, path.IndexOf('-'));
            string tail = path.Substring(dash + 1);
            if (System.Array.IndexOf(MetalForms, prefix) >= 0 && Required.ContainsKey(tail))
                return tail;
            // A recognized metal token anywhere as the trailing segment (covers modded
            // metal-form prefixes we didn't enumerate, e.g. metalbutton-copper).
            if (Required.ContainsKey(tail)) return tail;
        }

        // Ore / crushed ore / roastable / scrap: read where it smelts TO.
        ItemStack? smelted = stack.Collectible.CombustibleProps?.SmeltedStack?.ResolvedItemstack;
        if (smelted != null && smelted.Collectible != stack.Collectible)
            return MetalOf(world, smelted);

        return null;
    }

    /// <summary>Whether the player's MET rank clears the metal. Out params carry the
    /// requirement for the warning line. Metals with no resolvable code pass (nothing
    /// to gate on) — the seam should have already checked the gate is enabled.</summary>
    public static bool IsWorkable(ICoreAPI api, IPlayer player, string? metalCode, int unmappedDefault, out int currentLevel, out int requiredLevel)
    {
        requiredLevel = RequiredLevel(api, metalCode, unmappedDefault);
        var domainSet = AlmanacTcmModSystem.Instance?.Server?.GetDomainSet(player);
        currentLevel = domainSet?.FindDomain(MetDomain.Code)?.Level ?? 0;
        return currentLevel >= requiredLevel;
    }
}
