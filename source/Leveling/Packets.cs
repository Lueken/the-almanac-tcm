// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System.Collections.Generic;
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

    // Grower's Eye (FAR, ruled 2026-08-22): the readout ladder renders client-side from the
    // synced familiarity counters, so the toggle and thresholds ride the same join packet.
    [ProtoMember(2)]
    [DefaultValue(true)]
    public bool growerEyeFar = true;

    [ProtoMember(3)]
    [DefaultValue(5)]
    public int famAcquainted = 2;

    [ProtoMember(4)]
    [DefaultValue(25)]
    public int famVersed = 8;

    [ProtoMember(5)]
    [DefaultValue(50)]
    public int famFamilyVersed = 16;

    [ProtoMember(6)]
    [DefaultValue(0.5)]
    public double famSpread = 0.5;

    /// <summary>Ceiling on what kin alone can carry, so the tab and the tooltips agree with the
    /// server about where "never everything" sits.</summary>
    public int famKinCeiling = 7;

    public ClientConfigPacket() { }

    public ClientConfigPacket(bool alloyLedgerGated, bool growerEyeFar,
        int famAcquainted, int famVersed, int famFamilyVersed, double famSpread, int famKinCeiling)
    {
        this.alloyLedgerGated = alloyLedgerGated;
        this.growerEyeFar = growerEyeFar;
        this.famAcquainted = famAcquainted;
        this.famVersed = famVersed;
        this.famFamilyVersed = famFamilyVersed;
        this.famSpread = famSpread;
        this.famKinCeiling = famKinCeiling;
    }
}

/// <summary>Server→client practice-gain ping for the HUD toast (one per surviving
/// Log call; dedup-zeroed repeats never emit). Carries the same values the Info-tab
/// chat line already shows — display sensation only, no engine constants, no state
/// the Callings tab doesn't already get through PlayerDomainPacket.</summary>
[ProtoContract]
public class PracticeGainPacket
{
    [ProtoMember(1)]
    [DefaultValue(null)]
    public string? domainCode;

    [ProtoMember(2)]
    [DefaultValue(null)]
    public string? technique;

    [ProtoMember(3)]
    public float raw;

    public PracticeGainPacket() { }

    public PracticeGainPacket(string domainCode, string technique, float raw)
    {
        this.domainCode = domainCode;
        this.technique = technique;
        this.raw = raw;
    }
}

/// <summary>Server→client knowledge/discovery sync. A LIVE packet (sent the moment a key
/// is earned) may carry a toast lang key; the client resolves it in the player's own
/// locale and raises the discovery banner. The join-time replay travels as
/// <see cref="KnowledgeBatchPacket"/> instead, which never toasts — a returning player's
/// whole store replayed as banners would be noise, not ceremony.</summary>
[ProtoContract]
public class KnowledgePacket
{
    [ProtoMember(1)]
    [DefaultValue(null)]
    public string? name;

    [ProtoMember(2)]
    [DefaultValue(0)]
    public int level;

    /// <summary>Lang key naming this discovery for the banner ("The Stack Kiln").
    /// Null = silent earn (the auto-minted first-practice stream stays quiet).</summary>
    [ProtoMember(3)]
    [DefaultValue(null)]
    public string? toast;

    public KnowledgePacket() { }

    public KnowledgePacket(string name, int level, string? toast = null)
    {
        this.name = name;
        this.level = level;
        this.toast = toast;
    }
}

/// <summary>The join-time knowledge replay, whole store in one packet (was one
/// KnowledgePacket per key — chatty as the vocabulary grows, and every entry risked
/// reading as a live earn). Applied silently on the client, never toasted.</summary>
[ProtoContract]
public class KnowledgeBatchPacket
{
    [ProtoMember(1)]
    public Dictionary<string, int>? entries;

    public KnowledgeBatchPacket() { }

    public KnowledgeBatchPacket(Dictionary<string, int> entries)
    {
        this.entries = new Dictionary<string, int>(entries);
    }
}

/// <summary>Server→client display figures for one domain's book copy: the RESOLVED,
/// pre-formatted strings the Callings rung prose quotes ("85", "21.6 degrees toward you"),
/// computed server-side through the live DomainConfig by <c>DomainFigures</c>. Extends the
/// AffinityPacket precedent — raw knobs and curves never cross, only the resolved display
/// values the book actually prints, so a tuned server shows tuned numbers (ruled 2026-08-22).
/// Sent once per provider-backed domain at join.</summary>
[ProtoContract]
public class FiguresPacket
{
    [ProtoMember(1)]
    [DefaultValue(null)]
    public string? domainCode;

    [ProtoMember(2)]
    public Dictionary<string, string>? figures;

    /// <summary>The domain's live spillover adjacency (DomainConfig.Adjacency, roster
    /// codes) — the identity page's "trade web" margin block names its partners from
    /// this, so a server that rewires adjacency shows its own wiring. Resolved display
    /// data like the figures, never the tuned K/raw values around it.</summary>
    [ProtoMember(3)]
    public List<string>? adjacency;

    public FiguresPacket() { }

    public FiguresPacket(string domainCode, Dictionary<string, string> figures, List<string>? adjacency = null)
    {
        this.domainCode = domainCode;
        this.figures = figures;
        this.adjacency = adjacency;
    }
}

/// <summary>The rank-up ceremony: server→client, display sensation only (the STATE
/// travelled in PlayerDomainPacket at grant time; delaying this packet delays nothing
/// but the banner). Strings are pre-composed server-side exactly like the morning chat
/// line, so the client needs no tier math. Sent immediately for a 3am rank-up caught
/// in play; held for a login-consolidation rank-up until login protection clears
/// (RULED 2026-08-08: the banner fires when the player is fully in-game and aware).</summary>
[ProtoContract]
public class RankUpPacket
{
    /// <summary>The attained rank, e.g. "Journeyman II".</summary>
    [ProtoMember(1)]
    [DefaultValue(null)]
    public string? rank;

    /// <summary>The domain's display name, e.g. "Farming & Husbandry".</summary>
    [ProtoMember(2)]
    [DefaultValue(null)]
    public string? domainName;

    public RankUpPacket() { }

    public RankUpPacket(string rank, string domainName)
    {
        this.rank = rank;
        this.domainName = domainName;
    }
}
