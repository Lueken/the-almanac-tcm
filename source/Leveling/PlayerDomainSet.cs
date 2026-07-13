// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace AlmanacTcm.Leveling;

/// <summary>
/// All domain state for one player — the vendored PlayerSkillSet, minus
/// abilities/unlearn. Attached as an entity behavior so other systems can reach
/// it via entity.GetBehavior. Sparring survives the port (duel deaths are exempt
/// from the ledger scatter); Knowledge is the synced discovery store.
/// </summary>
public class PlayerDomainSet : EntityBehavior
{
    public IPlayer Player { get; internal set; }

    public List<PlayerDomain> PlayerDomains { get; private set; }

    /// <summary>Synced discovery store (knowledge name → level), e.g. domain-revealed flags.</summary>
    public Dictionary<string, int> Knowledge { get; private set; }

    public PlayerDomain? this[int index]
        => PlayerDomains.Count > index && index >= 0 ? PlayerDomains[index] : null;

    /// <summary>Saved state for domains not currently registered (a feature mod left the
    /// pack). Preserved verbatim through save/load so the data survives the mod's return.</summary>
    internal Dictionary<string, SavedPlayerDomain> UnusedPlayerDomains { get; private set; }

    /// <summary>Mutual-consent duel flag: if victim and killer both have sparring on,
    /// the death penalty (ledger scatter) is skipped.</summary>
    public bool Sparring { get; set; }

    /// <summary>Last penalized death, total world hours — the chain-death cooldown anchor.</summary>
    public double LastDeath
    {
        get => entity.WatchedAttributes.GetDouble("almanactcm:lastdeath", 0.0);
        set => entity.WatchedAttributes.SetDouble("almanactcm:lastdeath", value);
    }

    public PlayerDomainSet(IPlayer player, DomainSetTemplate template) : base(player.Entity)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        Player = player ?? throw new ArgumentNullException(nameof(player));
        entity.AddBehavior(this);
        UnusedPlayerDomains = new Dictionary<string, SavedPlayerDomain>();

        PlayerDomains = new List<PlayerDomain>(template.Domains.Count);
        foreach (Domain domain in template.Domains)
        {
            PlayerDomains.Add(new PlayerDomain(domain, this));
        }
        Knowledge = new Dictionary<string, int>();
    }

    public PlayerDomain? FindDomain(string code)
    {
        foreach (PlayerDomain playerDomain in PlayerDomains)
        {
            if (playerDomain.Domain.Code == code) return playerDomain;
        }
        return null;
    }

    public virtual void FromSavedSet(SavedPlayerDomainSet? saved)
    {
        if (saved == null) return;
        Sparring = saved.Sparring;

        foreach (PlayerDomain playerDomain in PlayerDomains)
        {
            saved.Domains.TryGetValue(playerDomain.Domain.Code, out SavedPlayerDomain? savedDomain);
            if (savedDomain == null) continue;
            saved.Domains.Remove(playerDomain.Domain.Code);
            playerDomain.FromSaved(savedDomain);
        }
        foreach (string key in saved.Domains.Keys)
        {
            UnusedPlayerDomains[key] = saved.Domains[key];
        }
        Knowledge = saved.Knowledge ?? new Dictionary<string, int>();
    }

    public override string PropertyName() => "TcmDomainSet";
}

/// <summary>Save shape for a player's whole domain set (keyed by domain CODE, not id —
/// registry order may change between sessions; codes never do).</summary>
[JsonObject(MemberSerialization.OptIn)]
public class SavedPlayerDomainSet
{
    [JsonProperty] public bool Sparring;

    [JsonProperty] public Dictionary<string, SavedPlayerDomain> Domains = new();

    [JsonProperty] public Dictionary<string, int>? Knowledge;

    public SavedPlayerDomainSet() { }

    public SavedPlayerDomainSet(PlayerDomainSet domainSet)
    {
        Sparring = domainSet.Sparring;
        Knowledge = new Dictionary<string, int>(domainSet.Knowledge);

        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (playerDomain.Experience > 0.0f || playerDomain.Level > 0 || !playerDomain.Hidden)
            {
                Domains.Add(playerDomain.Domain.Code, new SavedPlayerDomain(playerDomain));
            }
        }
        foreach (string key in domainSet.UnusedPlayerDomains.Keys)
        {
            Domains.Add(key, domainSet.UnusedPlayerDomains[key]);
        }
    }
}
