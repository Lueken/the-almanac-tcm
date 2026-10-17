using ProtoBuf;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlmanacTcm.Engine;

/// <summary>The declaration's packet: the player chooses to raise one Calling to Grandmaster from
/// the Callings detail page and the client asks the server to do it (client -> server).</summary>
[ProtoContract]
public class GmAscendPacket
{
    [ProtoMember(1)] public int DomainId;
}

/// <summary>
/// GRANDMASTER BY DECLARATION (Track 2, base-TCM half — ruled 2026-10-16).
///
/// Practice stops at Master IV for every affinity band (AffinitySystem.BandFor: every
/// non-negative score ceilings at Rank.MasterIV, ruled 2026-08-19), so Grandmaster is not
/// reachable by work at all. This is the only route to it, and until now it did not exist: the
/// rank was defined, the cap was configured, GmCount and GreatWorkNames were live, and
/// CanAscend had NO CALLERS. A lifetime cap nothing consults is not a cap.
///
/// Base TCM asks one question and keeps no ceremony of its own: at Master IV the Calling page
/// offers the choice, and saying yes spends one of the configured lifetime slots
/// (TcmGlobalConfig.GmDomainCap, default 2). The EARNING of a Grandmastery — commissions, proofs,
/// the whole quest shape — belongs to Charter, which is not requiredOnClient and can afford to be
/// interesting. That split is what lets TCM stabilise while the design continues elsewhere.
///
/// THE SERVER IS SOLE AUTHORITY, and this is the one place in TCM where that had to be said out
/// loud. The emphasis toggles (AlcEmphasis, TaiEmphasis) let the client write optimistically and
/// correct later, because being briefly wrong about Potent-versus-Lasting costs nothing. This
/// spends an irreversible lifetime slot, so the client sends a REQUEST and renders nothing until
/// the server's own domain sync comes back. A refusal is spoken, never silent: an offer that
/// vanishes with no explanation is how players conclude a mod is broken.
///
/// IDEMPOTENT BY RE-READ. Every gate below is checked against live state inside the handler, so a
/// double-click, a replayed packet or two clients on one account cannot spend two slots for one
/// decision — the second call finds the domain already at Grandmaster and refuses.
/// </summary>
public static class GmAscension
{
    private const string ChannelName = "almanactcmgm";

    private static IClientNetworkChannel? clientChannel;
    private static ICoreServerAPI? sapi;

    /// <summary>Set by Charter (or any later gatekeeper) to take ownership of WHO may ascend.
    /// Returning false refuses with the reason given, after TCM's own structural gates have
    /// passed. Null means base TCM's rule stands: Master IV and a free slot is enough.
    ///
    /// This seam exists now, while it is nearly free, because the ruled design moves the earning
    /// to Charter. Without it Charter would have to patch or fork this flow rather than gate it.</summary>
    public static System.Func<IPlayer, PlayerDomain, (bool ok, string? reason)>? ApprovalProvider { get; set; }

    // ------------------------------------------------------------------ eligibility (shared)

    /// <summary>Why a Calling cannot be declared right now, or null when it can. Pure and
    /// side-effect free, because BOTH sides call it: the server to authorize, and the book to
    /// decide whether to draw the offer at all. One predicate, so the page can never offer
    /// something the server will refuse.
    ///
    /// <paramref name="affinityScore"/> is -9 when unknown (the client passes its synced band,
    /// the server its live grid); unknown never blocks, because the server re-checks.</summary>
    public static string? RefusalFor(int level, bool enabled, int domainMaxLevel,
        int gmCount, int gmDomainCap, int affinityScore)
    {
        if (!enabled) return "disabled";

        // Already there. This is the gate a replayed packet lands on.
        if (level >= Rank.Grandmaster) return "already";

        // The rank is the price of admission: practice must have taken it as far as practice goes.
        if (level < Rank.MasterIV) return "not-master";

        // A resisted trade loses Grandmaster outright (-1), and Master too (-2). Such a player
        // cannot normally stand at Master IV, but a band reconfigure or an older save could leave
        // them there, and the ladder already draws their Grandmaster pip as barred. Refusing here
        // keeps the page and the server telling one story.
        if (affinityScore != UnknownAffinity && affinityScore < 0) return "resisted";

        // A domain whose own MaxLevel was lowered below Grandmaster cannot hold the rank: the
        // Level setter would clamp it and the declaration would spend a slot for nothing.
        if (domainMaxLevel < Rank.Grandmaster) return "capped-domain";

        if (gmCount >= gmDomainCap) return "no-slots";
        return null;
    }

    /// <summary>Primitives, not a PlayerDomain, deliberately: the client has no PlayerDomain to
    /// hand over (its ladder is a synced DomainState) and PlayerDomain's constructor refuses a
    /// null set, so a stand-in object would throw. Primitives let one predicate serve both sides
    /// honestly instead of a half-built object serving one of them badly.</summary>
    public static string? RefusalFor(PlayerDomain? pd, int gmCount, int gmDomainCap, int affinityScore)
        => pd == null ? "unknown-calling"
            : RefusalFor(pd.Level, pd.Domain.Enabled, pd.Domain.MaxLevel, gmCount, gmDomainCap, affinityScore);

    public const int UnknownAffinity = -9;

    /// <summary>The spoken form of a refusal, in the gate-blocked register.</summary>
    public static string RefusalText(string reason, string callingName, int cap) => reason switch
    {
        "already" => Lang.Get("almanactcm:gm-refuse-already", callingName),
        "not-master" => Lang.Get("almanactcm:gm-refuse-not-master",
            Domain.RankName(Rank.MasterIV), callingName),
        "resisted" => Lang.Get("almanactcm:gm-refuse-resisted", callingName),
        "no-slots" => Lang.Get("almanactcm:gm-refuse-no-slots", cap),
        "capped-domain" => Lang.Get("almanactcm:gm-refuse-capped", callingName),
        "disabled" => Lang.Get("almanactcm:gm-refuse-disabled", callingName),
        _ => Lang.Get("almanactcm:gm-refuse-unknown"),
    };

    // ------------------------------------------------------------------------ server

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<GmAscendPacket>()
            .SetMessageHandler<GmAscendPacket>(OnAscendRequest);
    }

    private static void OnAscendRequest(IServerPlayer fromPlayer, GmAscendPacket packet)
    {
        var core = AlmanacTcmModSystem.ServerInstance;
        var leveling = core?.Server;
        ICoreAPI? api = sapi;
        if (core == null || leveling == null || fromPlayer == null || api == null) return;

        PlayerDomainSet? domainSet = leveling.GetDomainSet(fromPlayer);
        PlayerDomain? pd = DomainOf(domainSet, packet?.DomainId ?? -1);
        string callingName = pd?.Domain.DisplayName ?? "that Calling";
        int cap = core.GlobalConfig?.GmDomainCap ?? 2;

        if (domainSet == null || pd == null)
        {
            Refuse(api, fromPlayer, "unknown-calling", callingName, cap);
            return;
        }

        int score = core.Affinity?.ScoreFor(fromPlayer, pd.Domain.Code) ?? 0;
        string? refusal = RefusalFor(pd, AffinitySystem.GmCount(domainSet), cap, score);
        if (refusal != null)
        {
            Refuse(api, fromPlayer, refusal, callingName, cap);
            return;
        }

        // Charter's seam, consulted last: TCM's structural rules are not negotiable, but WHO has
        // earned the right is somebody else's question once a gatekeeper is installed.
        var approval = ApprovalProvider;
        if (approval != null)
        {
            var (ok, reason) = approval(fromPlayer, pd);
            if (!ok)
            {
                Speak(fromPlayer, reason ?? Lang.Get("almanactcm:gm-refuse-unapproved", callingName));
                TcmLog.Cat(api, TcmLog.Affinity,
                    $"GM declaration refused by approval provider: {fromPlayer.PlayerName} {pd.Domain.Code}");
                return;
            }
        }

        Ascend(api, leveling, fromPlayer, domainSet, pd, cap);
    }

    /// <summary>The grant itself. Deliberately NOT routed through AddExperience: the ladder's
    /// own rule is that experience only ever arrives through the consolidation flush, and this
    /// rank is not bought with experience. The level is set, which zeroes Experience through the
    /// setter (at MaxLevel it is discarded by design), then synced and announced on the same
    /// ceremony pipeline every other rank-up uses — whose own comment anticipated exactly this.</summary>
    private static void Ascend(ICoreAPI api, LevelingServer leveling, IServerPlayer player,
        PlayerDomainSet domainSet, PlayerDomain pd, int cap)
    {
        pd.Level = Rank.Grandmaster;
        pd.Hidden = false;
        leveling.SyncDomain(player, pd);
        leveling.QueueRankUpCeremony(player, Domain.RankName(pd.Level), pd.Domain.DisplayName, delayed: false);

        int spent = AffinitySystem.GmCount(domainSet);
        Speak(player, Lang.Get("almanactcm:gm-declared", pd.Domain.DisplayName, spent, cap));

        // Logged at Info, not under a category: a lifetime, irreversible choice belongs in the
        // server log unconditionally, because the question "who declared what, and when" is asked
        // after the fact and a category toggle would have lost the answer.
        TcmLog.Info(api, $"GREAT WORK DECLARED: {player.PlayerName} raised {pd.Domain.Code} to "
            + $"{Domain.RankName(pd.Level)} ({spent}/{cap} lifetime slots now spent)");
    }

    private static void Refuse(ICoreAPI api, IServerPlayer player, string reason, string callingName, int cap)
    {
        Speak(player, RefusalText(reason, callingName, cap));
        TcmLog.Cat(api, TcmLog.Affinity,
            $"GM declaration refused ({reason}): {player.PlayerName} / {callingName}");
    }

    private static void Speak(IServerPlayer player, string message)
        => player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);

    private static PlayerDomain? DomainOf(PlayerDomainSet? domainSet, int domainId)
    {
        if (domainSet == null || domainId < 0) return null;
        foreach (PlayerDomain pd in domainSet.PlayerDomains)
            if (pd.Domain.Id == domainId) return pd;
        return null;
    }

    // ------------------------------------------------------------------------ client

    public static void RegisterClient(ICoreClientAPI api)
    {
        clientChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<GmAscendPacket>();
    }

    /// <summary>Ask the server to raise this Calling. Nothing is written locally and nothing is
    /// predicted: the page changes when the server's domain sync says it changed.</summary>
    public static void Request(int domainId)
        => clientChannel?.SendPacket(new GmAscendPacket { DomainId = domainId });
}
