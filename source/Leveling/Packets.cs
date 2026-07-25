// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System.ComponentModel;
using ProtoBuf;

namespace AlmanacTcm.Leveling;

/// <summary>
/// Server→client domain state sync (join-time full sync + per-consolidation delta).
/// Carries STATE only — level and banked XP — never engine constants
/// (build-track-1 T1.0 hidden-values rule). There is no client→server XP path.
/// </summary>
[ProtoContract]
public class PlayerDomainPacket
{
    [ProtoMember(1)]
    [DefaultValue(-1)]
    public int domainId = -1;

    [ProtoMember(2)]
    public int level;

    [ProtoMember(3)]
    public float experience;

    /// <summary>Required XP for the next level — sent so the client HUD needs no curve math.</summary>
    [ProtoMember(4)]
    public float requiredExperience;

    [ProtoMember(5)]
    public bool hidden = true;

    /// <summary>Projection of what today's unconsolidated practice would bank at the
    /// next rest — display-only state for the two-tone bar. Zero on every packet the
    /// consolidation flush sends, which is what collapses the wash into ink.</summary>
    [ProtoMember(6)]
    public float pendingBanked;

    public PlayerDomainPacket() { }

    public PlayerDomainPacket(PlayerDomain playerDomain)
    {
        domainId = playerDomain.Domain.Id;
        level = playerDomain.Level;
        experience = playerDomain.Experience;
        requiredExperience = playerDomain.RequiredExperience;
        hidden = playerDomain.Hidden;
    }
}

/// <summary>Server→client affinity band for one domain, given the player's class.
/// The grid (design law, server-configurable) never leaves the server; only this
/// resolved band does, so the client shows the "why you started here" line without
/// a copy of the grid that could drift from a customized affinity.json.</summary>
[ProtoContract]
public class AffinityPacket
{
    [ProtoMember(1)]
    [DefaultValue(-1)]
    public int domainId = -1;

    /// <summary>The affinity score for this domain (−2 … +3). The client buckets it
    /// into prose; it is identity, not a tuned curve value.</summary>
    [ProtoMember(2)]
    public int band;

    public AffinityPacket() { }

    public AffinityPacket(int domainId, int band)
    {
        this.domainId = domainId;
        this.band = band;
    }
}

/// <summary>Server→client sync of the config flags the CLIENT must honour (gates that open
/// client-side). Sent once on join, so a server-owned setting in global.json actually reaches
/// the client instead of the client falling back to the shipped default.</summary>
[ProtoContract]
public class ClientConfigPacket
{
    [ProtoMember(1)]
    [DefaultValue(true)]
    public bool alloyLedgerGated = true;

    public ClientConfigPacket() { }

    public ClientConfigPacket(bool alloyLedgerGated)
    {
        this.alloyLedgerGated = alloyLedgerGated;
    }
}

/// <summary>Server→client knowledge/discovery sync.</summary>
[ProtoContract]
public class KnowledgePacket
{
    [ProtoMember(1)]
    [DefaultValue(null)]
    public string? name;

    [ProtoMember(2)]
    [DefaultValue(0)]
    public int level;

    public KnowledgePacket() { }

    public KnowledgePacket(string name, int level)
    {
        this.name = name;
        this.level = level;
    }
}
