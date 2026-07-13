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
