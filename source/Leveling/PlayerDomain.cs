// Derived from XLib/XLeveling by Xandu (MIT), see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Leveling;

/// <summary>
/// Per-player state for one domain, the vendored PlayerSkill, minus abilities.
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
    /// MaxLevel excess is discarded (xLib behavior: the ladder is finite, guard #4).</summary>
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

    /// <summary>Restores one saved domain. The ENABLED path is unchanged and still travels the
    /// property setters, so a save whose banked XP now exceeds a retuned requirement still
    /// cascades its level-ups on login. The DISABLED path writes the backing fields directly,
    /// because the setters would destroy the save.
    ///
    /// WHY THE BRANCH EXISTS (2026-08-20). Both setters refuse when the domain is disabled:
    /// Level coerces to 0 (:31), Experience zeroes and returns (:47). Those guards are right for
    /// gameplay. They are what stops a dormant domain banking practice or cascading a level-up,
    /// and the audit that cleared this change turns on them staying exactly as they are. They
    /// were wrong HERE, because a restore is not a gain.
    ///
    /// What the old code did: a domain goes disabled when its third-party mod is absent, which
    /// today means GLA, ARC or BEE, the only DomainRoster entries carrying a required mod.
    /// PlayerDomainSet.FromSavedSet removes the entry from the saved set before calling this, so
    /// it never reached the UnusedPlayerDomains preservation path either. Net effect: remove
    /// Rustbound Magic, log in once, and a player's Arcana rank AND banked practice were gone
    /// permanently, with reinstalling the mod bringing neither back. That is a rank regressing,
    /// which the governing rule (G11) says can never happen.
    ///
    /// This cannot heal a save the old behaviour already zeroed; nothing is left on disk to
    /// restore. That case needs an operator /tcm setlevel.</summary>
    public virtual void FromSaved(SavedPlayerDomain saved)
    {
        if (Domain.Enabled)
        {
            Level = saved.Level;
            Experience = saved.Experience;
        }
        else
        {
            level = GameMath.Clamp(saved.Level, 0, Domain.MaxLevel);
            experience = Math.Max(saved.Experience, 0.0f);
            // Only the Level setter recomputes this, and the line above bypassed it.
            RequiredExperience = Domain.GetRequiredExperience(level + 1);
        }
        Hidden = saved.Hidden;
    }
}

/// <summary>Save shape for one domain (JSON opt-in, own file format, never xLib's).</summary>
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
