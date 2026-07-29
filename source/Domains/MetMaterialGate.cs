using System.Collections.Generic;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

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
        currentLevel = MetLevelOf(api, player);
        return currentLevel >= requiredLevel;
    }

    private static int metDomainId = -2;   // -2 = not yet resolved

    /// <summary>MET's registry id, for the client-side level lookup (client Domains are
    /// keyed by id). Resolved once from the roster.</summary>
    private static int MetDomainId()
    {
        if (metDomainId != -2) return metDomainId;
        metDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == MetDomain.Code) { metDomainId = i; break; }
        return metDomainId;
    }

    /// <summary>The interacting player's MET level, read from whichever side is live: the
    /// server ledger, or (client) the synced state of the local player. This is what lets
    /// the gate run client-side too, so a blocked action is never mispredicted.</summary>
    private static int MetLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(MetDomain.Code)?.Level ?? 0;

        LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = MetDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    /// <summary>The seam decision: true = BLOCK this working-of-metal action, because the
    /// gate is on, the stack's metal is gated above the player's MET rank, and the server
    /// is not in hardcore mode. Sends the throttled warning as a side effect. Hardcore
    /// lifts the block so the attempt proceeds (true consume-and-ruin waste is a documented
    /// future refinement). Client side, disabled gate, no player, or a stack with no
    /// resolvable metal (fuel, tools, non-metal) → false (allow).</summary>
    /// <summary>SINGLEPLAYER-ONLY override of the gate, set by `/tcm gate on|off` and stored in
    /// that world's savegame. Null = no override, the server config decides (every dedicated
    /// server and every multiplayer client is always null).
    ///
    /// Deliberately ONE process-wide static rather than a flag on each side's config. Blocks()
    /// runs on both sides, and in singleplayer the client and server ModSystems share statics
    /// (the same fact behind the 0.4.3 zero-XP bug) — so a single static flips both sides at once.
    /// Flipping the server config alone would leave the client still predicting a block, because
    /// ClientInstance.GlobalConfig is never loaded from disk and always holds the shipped default.
    ///
    /// Reset to null at every session start, so a gate disabled in a singleplayer world cannot
    /// linger into a server join made without restarting the game.</summary>
    public static bool? SinglePlayerOverride;

    public static bool Blocks(ICoreAPI? api, IPlayer? player, ItemStack? metalStack)
    {
        if (api == null || player == null) return false;
        // The singleplayer override wins on both sides when it is set (see above).
        if (SinglePlayerOverride == false) return false;

        // Runs on BOTH sides: the client uses its synced MET level to avoid predicting a
        // placement the server will reject (the ghost-ingot desync). Config here is the
        // client's default when unsynced — fine while the gate ships enabled; syncing the
        // toggle to clients is a future refinement.
        var cfg = (api.Side == EnumAppSide.Server
            ? AlmanacTcmModSystem.ServerInstance
            : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
        if (SinglePlayerOverride != true && (cfg == null || !cfg.MaterialGateMET)) return false;

        string? metal = MetalOf(api.World, metalStack);
        if (metal == null) return false;
        // Null-safe: an override of `true` can reach here with no config loaded (the client
        // side never loads one), so fall back to the shipped defaults rather than throwing.
        if (IsWorkable(api, player, metal, cfg?.MaterialGateMETUnmappedLevel ?? 0, out _, out int required)) return false;
        if (cfg?.MaterialGateMETHardcore == true) return false;

        Warn(api, player, metal, required);
        return true;
    }

    private static void Warn(ICoreAPI api, IPlayer player, string metal, int requiredLevel)
    {
        string key = player.PlayerUID + ":" + metal;
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(key, out long last) && now - last < 2000) return;
        lastWarn[key] = now;

        string metalName = char.ToUpperInvariant(metal[0]) + metal.Substring(1);
        string rank = Domain.RankName(requiredLevel);

        // Show the red ingame-error on the CLIENT — that's where the acting player sees it,
        // the client gate always runs for the local player, and it never doubles. Relying
        // on the server would be fragile: once the client cancels the interaction, the
        // interact packet may never reach the server to fire the error.
        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "metalgate", Lang.Get("almanactcm:gate-blocked", metalName, rank));
        else
            TcmLog.Cat(api, TcmLog.Hooks, $"gate: {player.PlayerName} blocked from working {metal} (needs {rank})");
    }
}
