using System.Text;
using AlmanacTcm.Leveling;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AlmanacTcm.Engine;

/// <summary>
/// /tcm — the T1.5 presentation slice. `status` is every player's read (G6
/// framing: today's page filling, never "capped/denied"); `practice` and
/// `nextday` are admin/test tools that drive the REAL engine paths (nextday
/// rewinds the ledger's boundary marker so the genuine consolidation runs on
/// the next tick — there is no separate test-only consolidation code).
/// </summary>
public class TcmCommands
{
    private readonly ICoreServerAPI sapi;
    private readonly AlmanacTcmModSystem core;

    public TcmCommands(ICoreServerAPI sapi, AlmanacTcmModSystem core)
    {
        this.sapi = sapi;
        this.core = core;
        var parsers = sapi.ChatCommands.Parsers;

        sapi.ChatCommands.Create("tcm")
            .WithDescription("The Almanac: Trades, Callings & Mastery")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("status")
                .WithDescription("Your trades: rank and today's practice")
                .HandleWith(OnStatus)
            .EndSubCommand()
            .BeginSubCommand("practice")
                .WithDescription("(admin) Inject practice events through the real engine path")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("domain"), parsers.Word("technique"), parsers.OptionalInt("times", 1))
                .HandleWith(OnPractice)
            .EndSubCommand()
            .BeginSubCommand("nextday")
                .WithDescription("(admin) Rewind the boundary marker so the next engine tick consolidates")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnNextDay)
            .EndSubCommand()
            .BeginSubCommand("setlevel")
                .WithDescription("(admin) Set a domain's rank directly, for testing (Master I = 13)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("domain"), parsers.Int("level"))
                .HandleWith(OnSetLevel)
            .EndSubCommand()
            .BeginSubCommand("inspect")
                .WithDescription("(admin) Print the held item's TCM attributes, server-side truth")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnInspect)
            .EndSubCommand()
            .BeginSubCommand("knowledge")
                .WithDescription("(admin) List, set, or clear knowledge keys (guide reveal testing)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("action"), parsers.OptionalWord("key"))
                .HandleWith(OnKnowledge)
            .EndSubCommand();

        // SINGLEPLAYER ONLY, by construction. The subcommand is not registered at all on a
        // dedicated server, so there is nothing on The Quire to run, mistype, or bypass, so the
        // guarantee is structural rather than a runtime privilege check.
        if (!sapi.Server.IsDedicated)
        {
            sapi.ChatCommands.Get("tcm")
                .BeginSubCommand("gate")
                    .WithDescription("(singleplayer) Turn the Metalworking material gate on or off for this world")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.OptionalWord("state"))
                    .HandleWith(OnGate)
                .EndSubCommand();

            RestoreGateOverride();
        }
    }

    // ------------------------------------------------------------------ material gate

    /// <summary>Savegame key for the singleplayer gate override. Stored in the WORLD, never in
    /// ModConfig: global.json is per install and shared with the same client when it joins a
    /// server, and because the gate is also evaluated client-side, a value that followed the
    /// player onto The Quire would leave their client predicting no block while the server still
    /// blocked, which is the ghost-ingot desync. A savegame value cannot travel.</summary>
    private const string GateOverrideKey = "almanactcm:materialgateoverride";

    private void RestoreGateOverride()
    {
        try
        {
            byte[]? stored = sapi.WorldManager.SaveGame.GetData(GateOverrideKey);
            if (stored == null || stored.Length == 0) return;
            Domains.MetMaterialGate.SinglePlayerOverride = stored[0] != 0;
            TcmLog.Cat(sapi, TcmLog.Config,
                $"singleplayer material-gate override restored: {(stored[0] != 0 ? "on" : "off")}");
        }
        catch (System.Exception e)
        {
            TcmLog.Warn(sapi, $"material-gate override unreadable ({e.Message}); using the server config");
        }
    }

    private TextCommandResult OnGate(TextCommandCallingArgs args)
    {
        bool configDefault = core.GlobalConfig.MaterialGateMET;
        bool effective = Domains.MetMaterialGate.SinglePlayerOverride ?? configDefault;

        string? state = args.Parsers[0].IsMissing ? null : (string?)args[0];
        if (state == null)
        {
            return TextCommandResult.Success(
                $"Metalworking material gate is {(effective ? "ON" : "OFF")} for this world"
                + (Domains.MetMaterialGate.SinglePlayerOverride == null
                    ? " (server config default)."
                    : " (set for this world; /tcm gate " + (effective ? "off" : "on") + " to flip)."));
        }

        bool wanted;
        switch (state.ToLowerInvariant())
        {
            case "on": case "true": case "enable": case "enabled": wanted = true; break;
            case "off": case "false": case "disable": case "disabled": wanted = false; break;
            default: return TextCommandResult.Error("Use: /tcm gate [on|off]");
        }

        // One process-wide static: singleplayer shares it between the client and server
        // ModSystems, so both the real check and the client's prediction flip together.
        Domains.MetMaterialGate.SinglePlayerOverride = wanted;
        sapi.WorldManager.SaveGame.StoreData(GateOverrideKey, new byte[] { wanted ? (byte)1 : (byte)0 });

        TcmLog.Cat(sapi, TcmLog.Config, $"singleplayer material-gate override set to {(wanted ? "on" : "off")}");
        return TextCommandResult.Success(wanted
            ? "Metalworking material gate ON. Metals above your rank are blocked again."
            : "Metalworking material gate OFF for this world. Every metal is workable at any rank. "
              + "This world only. Multiplayer servers are unaffected.");
    }

    /// <summary>Guide reveal testing: list shows every key the player holds, set/clear
    /// flip one key so a gated section can be exercised without a fresh character.
    /// Writes go through SetKnowledge, so the client store syncs immediately.</summary>
    private TextCommandResult OnKnowledge(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        PlayerDomainSet? domainSet = core.Server?.GetDomainSet(player);
        if (domainSet == null) return TextCommandResult.Error("No domain data yet.");

        string action = args[0] as string ?? "";
        string? key = args.Parsers.Count > 1 ? args[1] as string : null;

        switch (action)
        {
            case "list":
            {
                if (domainSet.Knowledge.Count == 0) return TextCommandResult.Success("No knowledge keys yet.");
                StringBuilder sb = new();
                foreach (var (k, v) in domainSet.Knowledge) sb.AppendLine($"{k} = {v}");
                return TextCommandResult.Success(sb.ToString());
            }
            case "set":
                if (string.IsNullOrEmpty(key)) return TextCommandResult.Error("Usage: /tcm knowledge set <key>");
                core.Server!.SetKnowledge(player, key, 1);
                return TextCommandResult.Success($"{key} = 1 (synced)");
            case "clear":
                if (string.IsNullOrEmpty(key)) return TextCommandResult.Error("Usage: /tcm knowledge clear <key>");
                if (!domainSet.Knowledge.ContainsKey(key)) return TextCommandResult.Error($"No such key: {key}");
                core.Server!.SetKnowledge(player, key, 0);
                domainSet.Knowledge.Remove(key);
                return TextCommandResult.Success($"{key} cleared (client sees 0 until relog fully drops it)");
            default:
                return TextCommandResult.Error("Usage: /tcm knowledge <list|set|clear> [key]");
        }
    }

    private static string RankName(int level) => Domain.RankName(level);

    private TextCommandResult OnStatus(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        PlayerDomainSet? domainSet = core.Server?.GetDomainSet(player);
        if (domainSet == null || core.Ledger == null) return TextCommandResult.Error("No domain data yet.");

        PracticeLedger ledger = core.Ledger.LedgerFor(player);
        StringBuilder sb = new();
        sb.AppendLine("== The Copybook page for today ==");

        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (!playerDomain.Domain.Enabled || playerDomain.Hidden) continue;

            string line = $"{playerDomain.Domain.DisplayName}: {RankName(playerDomain.Level)}";
            if (playerDomain.Level > 0 && playerDomain.Level < playerDomain.Domain.MaxLevel)
            {
                line += $" ({playerDomain.Experience:0}/{playerDomain.RequiredExperience:0})";
            }

            if (ledger.Accumulators.TryGetValue(playerDomain.Domain.Code, out var accs) && accs.Count > 0)
            {
                sb.AppendLine(line);
                foreach (var (technique, x) in accs)
                {
                    sb.AppendLine($"   today's {technique}: {x:0.#} practice, settling at rest");
                }
            }
            else
            {
                sb.AppendLine(line + ", a fresh page today");
            }
        }

        int gmCount = AffinitySystem.GmCount(domainSet);
        sb.AppendLine($"Great Works declared: {gmCount}/{core.GlobalConfig.GmDomainCap}");
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult OnPractice(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        string domain = ((string)args[0]).ToUpperInvariant();
        string technique = (string)args[1];
        int times = (int)args[2];

        for (int i = 0; i < times; i++)
        {
            // Distinct context hashes so the dedup guard doesn't zero the batch.
            core.Ledger?.Log(player, domain, technique, System.HashCode.Combine(i, sapi.World.ElapsedMilliseconds));
        }
        return TextCommandResult.Success($"Logged {times}x {domain}/{technique} practice. See /tcm status.");
    }

    private TextCommandResult OnInspect(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        var stack = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
        if (stack == null) return TextCommandResult.Error("Hold the item to inspect.");

        StringBuilder sb = new();
        sb.AppendLine($"{stack.Collectible?.Code} (tool={stack.Collectible?.Tool}, tier={stack.Collectible?.ToolTier})");
        bool any = false;
        foreach (var (key, value) in stack.Attributes)
        {
            if (!key.StartsWith("almanactcm")) continue;
            sb.AppendLine($"  {key} = {value.GetValue()}");
            any = true;
        }
        if (!any) sb.AppendLine("  (no almanactcm attributes)");
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult OnSetLevel(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        string domain = ((string)args[0]).ToUpperInvariant();
        int level = (int)args[1];

        PlayerDomainSet? domainSet = core.Server?.GetDomainSet(player);
        PlayerDomain? pd = domainSet?.FindDomain(domain);
        if (pd == null) return TextCommandResult.Error($"No such domain '{domain}'.");

        pd.Hidden = false;
        pd.Level = level;
        core.Server?.SyncDomain(player, pd);
        // ARC re-roots the RBM mana pool off its rank; apply it immediately so the pool snaps now instead
        // of on the next 2s reconcile tick (the "set it and wait a minute to catch" bug).
        if (domain == "ARC") Domains.ArcPatches.ApplyReRoot(player);
        return TextCommandResult.Success($"{domain} set to {RankName(level)} (level {level}). Reopen the station or book to see it.");
    }

    private TextCommandResult OnNextDay(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Player-only command.");
        PracticeLedger ledger = core.Ledger!.LedgerFor(player);
        ledger.LastConsolidatedBoundary -= 1;
        return TextCommandResult.Success(
            "Boundary marker rewound. The real consolidation runs on the next engine tick (within ~5s).");
    }
}
