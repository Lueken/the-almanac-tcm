// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace AlmanacTcm.Leveling;

/// <summary>What kind of moment a discovery banner is carrying. The renderer needs to tell
/// them apart because a knowledge earn wrote a page the player can go and read (so it gets
/// the "which key opens the book" subline) and a rank-up did not.</summary>
public enum BannerKind
{
    Knowledge,
    RankUp,
}

/// <summary>
/// Client mirror of the local player's domain state — a plain read model for
/// HUD/status surfaces. Deliberately thinner than xLib's client: the client
/// receives state packets and holds them; no curve math, no constants, no
/// client→server XP messages exist.
///
/// Discovery moments (live named knowledge earns, rank-up ceremonies) raise the
/// Banner event; DiscoveryBannerRenderer subscribes and owns sequencing, so a 3am
/// morning that ranks three domains at once reads as three moments, not one
/// overwritten flicker. The join-time knowledge batch applies silently — replay is
/// never ceremony.
/// </summary>
public class LevelingClient
{
    public class DomainState
    {
        public int Level;
        public float Experience;
        public float RequiredExperience;
        public bool Hidden = true;

        /// <summary>Today's unsettled practice, projected — the pencil wash on the bar.</summary>
        public float PendingBanked;
    }

    /// <summary>Local player's synced state, keyed by domain id.</summary>
    public Dictionary<int, DomainState> Domains { get; } = new();

    /// <summary>Synced knowledge/discovery store.</summary>
    public Dictionary<string, int> Knowledge { get; } = new();

    /// <summary>Synced affinity band per domain id (−2 … +3): the player's aptitude for
    /// each trade given their class. Drives the detail page's "why you started" line.</summary>
    public Dictionary<int, int> Affinity { get; } = new();

    /// <summary>Raised per surviving practice gain (domainCode, technique, raw); the
    /// toast renderer subscribes. Display sensation only — no state lives here.</summary>
    public event System.Action<string, string, float>? PracticeGain;

    /// <summary>Raised per discovery moment (a live named knowledge earn, a rank-up
    /// ceremony); DiscoveryBannerRenderer subscribes and owns sequencing. Display
    /// sensation only, same contract as PracticeGain.</summary>
    public event System.Action<string, BannerKind>? Banner;

    /// <summary>Raised per LIVE first knowledge earn, with the full key.
    /// QuestStepToastRenderer subscribes and asks Illuminated whether any quest step is
    /// completed by that key.
    ///
    /// RETUNED 2026-08-08, same day it was settled: the first cut suppressed step toasts
    /// for keys that also banner, on the one-earn-one-moment argument. Jeffrey's ask was
    /// the other way (a step completing should always show its check landing), and the two
    /// surfaces sit 95 GUI px apart with separate queues, so the banner above and the tick
    /// below read as one composed moment, not a repeat. Every live first earn raises this
    /// now; keys with no matching quest step cost one empty lookup.</summary>
    public event System.Action<string>? QuestKnowledge;

    public LevelingClient(ICoreClientAPI capi)
    {
        IClientNetworkChannel channel = capi.Network.RegisterChannel("almanactcm");
        channel.RegisterMessageType(typeof(PlayerDomainPacket));
        channel.RegisterMessageType(typeof(KnowledgePacket));
        channel.RegisterMessageType(typeof(KnowledgeBatchPacket));
        channel.RegisterMessageType(typeof(AffinityPacket));
        channel.RegisterMessageType(typeof(ClientConfigPacket));
        channel.RegisterMessageType(typeof(PracticeGainPacket));
        channel.RegisterMessageType(typeof(RankUpPacket));
        channel.SetMessageHandler<PlayerDomainPacket>(OnDomainPacket);
        channel.SetMessageHandler<KnowledgePacket>(OnKnowledgePacket);
        channel.SetMessageHandler<KnowledgeBatchPacket>(OnKnowledgeBatchPacket);
        channel.SetMessageHandler<AffinityPacket>(OnAffinityPacket);
        channel.SetMessageHandler<ClientConfigPacket>(OnClientConfigPacket);
        channel.SetMessageHandler<PracticeGainPacket>(OnPracticeGainPacket);
        channel.SetMessageHandler<RankUpPacket>(OnRankUpPacket);
    }

    private void OnPracticeGainPacket(PracticeGainPacket packet)
    {
        if (packet.domainCode == null) return;
        PracticeGain?.Invoke(packet.domainCode, packet.technique ?? "", packet.raw);
    }

    private void OnClientConfigPacket(ClientConfigPacket packet)
    {
        var core = AlmanacTcmModSystem.ClientInstance;
        if (core != null) core.AlloyLedgerGated = packet.alloyLedgerGated;
    }

    private void OnAffinityPacket(AffinityPacket packet)
    {
        if (packet.domainId >= 0) Affinity[packet.domainId] = packet.band;
    }

    private void OnDomainPacket(PlayerDomainPacket packet)
    {
        if (packet.domainId < 0) return;
        Domains[packet.domainId] = new DomainState
        {
            Level = packet.level,
            Experience = packet.experience,
            RequiredExperience = packet.requiredExperience,
            Hidden = packet.hidden,
            PendingBanked = packet.pendingBanked
        };
    }

    private void OnKnowledgePacket(KnowledgePacket packet)
    {
        if (packet.name == null) return;
        bool isNew = !Knowledge.TryGetValue(packet.name, out int previous) || packet.level > previous;
        Knowledge[packet.name] = packet.level;

        // Only a LIVE first earn with a named toast raises the banner. The server keeps
        // the join replay on the batch packet, so anything arriving here is live.
        if (!isNew) return;
        if (!string.IsNullOrEmpty(packet.toast))
        {
            Banner?.Invoke(Lang.Get("almanactcm:toast-knowledge", Lang.Get(packet.toast)),
                BannerKind.Knowledge);
        }
        // Every live first earn is offered to the quest-step surface, bannered or not
        // (see the QuestKnowledge doc comment for the retune history).
        QuestKnowledge?.Invoke(packet.name);
    }

    private void OnKnowledgeBatchPacket(KnowledgeBatchPacket packet)
    {
        if (packet.entries == null) return;
        foreach (var (name, level) in packet.entries) Knowledge[name] = level;
    }

    private void OnRankUpPacket(RankUpPacket packet)
    {
        if (packet.rank == null || packet.domainName == null) return;
        Banner?.Invoke(Lang.Get("almanactcm:toast-rankup", packet.rank, packet.domainName),
            BannerKind.RankUp);
    }
}
