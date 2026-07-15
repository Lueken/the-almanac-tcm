// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace AlmanacTcm.Leveling;

/// <summary>
/// Client mirror of the local player's domain state — a plain read model for
/// HUD/status surfaces. Deliberately thinner than xLib's client: the client
/// receives state packets and holds them; no curve math, no constants, no
/// client→server XP messages exist.
/// </summary>
public class LevelingClient
{
    public class DomainState
    {
        public int Level;
        public float Experience;
        public float RequiredExperience;
        public bool Hidden = true;
    }

    /// <summary>Local player's synced state, keyed by domain id.</summary>
    public Dictionary<int, DomainState> Domains { get; } = new();

    /// <summary>Synced knowledge/discovery store.</summary>
    public Dictionary<string, int> Knowledge { get; } = new();

    /// <summary>Synced affinity band per domain id (−2 … +3): the player's aptitude for
    /// each trade given their class. Drives the detail page's "why you started" line.</summary>
    public Dictionary<int, int> Affinity { get; } = new();

    public LevelingClient(ICoreClientAPI capi)
    {
        IClientNetworkChannel channel = capi.Network.RegisterChannel("almanactcm");
        channel.RegisterMessageType(typeof(PlayerDomainPacket));
        channel.RegisterMessageType(typeof(KnowledgePacket));
        channel.RegisterMessageType(typeof(AffinityPacket));
        channel.RegisterMessageType(typeof(ClientConfigPacket));
        channel.SetMessageHandler<PlayerDomainPacket>(OnDomainPacket);
        channel.SetMessageHandler<KnowledgePacket>(OnKnowledgePacket);
        channel.SetMessageHandler<AffinityPacket>(OnAffinityPacket);
        channel.SetMessageHandler<ClientConfigPacket>(OnClientConfigPacket);
    }

    private void OnClientConfigPacket(ClientConfigPacket packet)
    {
        var core = AlmanacTcmModSystem.Instance;
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
            Hidden = packet.hidden
        };
    }

    private void OnKnowledgePacket(KnowledgePacket packet)
    {
        if (packet.name == null) return;
        Knowledge[packet.name] = packet.level;
    }
}
