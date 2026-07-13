// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Leveling;

/// <summary>
/// Per-player state for one domain — the vendored PlayerSkill, minus abilities.
/// Experience only ever arrives through the consolidation flush (there is no
/// instant-XP path anywhere in TCM); the level-up while-loop is xLib's proven one.
/// </summary>
public class PlayerDomain
{
    public Domain Domain { get; private set; }

    public PlayerDomainSet DomainSet { get; private set; }

    private int level;

    /// <summary>Hidden until the player's first practice event discovers the domain.</summary>
    public bool Hidden { get; set; }

    public int Level
    {
        get => level;
        internal set
        {
            if (!Domain.Enabled) value = 0;
            value = GameMath.Clamp(value, 0, Domain.MaxLevel);
            level = value;
            RequiredExperience = Domain.GetRequiredExperience(level + 1);
        }
    }

    private float experience;

    /// <summary>Banked XP inside the current level. Setting cascades level-ups; at
    /// MaxLevel excess is discarded (xLib behavior — the ladder is finite, guard #4).</summary>
    public float Experience
    {
        get => experience;
        internal set
        {
            if (Level >= Domain.MaxLevel || !Domain.Enabled)
            {
                experience = 0.0f;
                return;
            }
            experience = Math.Max(value, 0.0f);
            while (RequiredExperience <= experience && Level < Domain.MaxLevel)
            {
                experience -= RequiredExperience;
                Level++;
            }
        }
    }

    public float RequiredExperience { get; private set; }

    public PlayerDomain(Domain domain, PlayerDomainSet domainSet, int level = 0)
    {
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        DomainSet = domainSet ?? throw new ArgumentNullException(nameof(domainSet));
        Level = level;
        Experience = 0.0f;
        Hidden = true;
    }

    public virtual void FromSaved(SavedPlayerDomain saved)
    {
        Level = saved.Level;
        Experience = saved.Experience;
        Hidden = saved.Hidden;
    }
}

/// <summary>Save shape for one domain (JSON opt-in, own file format — never xLib's).</summary>
[JsonObject(MemberSerialization.OptIn)]
public class SavedPlayerDomain
{
    [JsonProperty] public int Level;

    [JsonProperty] public float Experience;

    [JsonProperty] public bool Hidden = true;

    public SavedPlayerDomain() { }

    public SavedPlayerDomain(PlayerDomain playerDomain)
    {
        Level = playerDomain.Level;
        Experience = playerDomain.Experience;
        Hidden = playerDomain.Hidden;
    }
}
