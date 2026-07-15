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
            .EndSubCommand();
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
