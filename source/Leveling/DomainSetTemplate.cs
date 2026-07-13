// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System.Collections.Generic;

namespace AlmanacTcm.Leveling;

/// <summary>
/// The registry every player's domain set is instantiated from — the vendored
/// SkillSetTemplate. Populate fully (including conditional domains, marked
/// Enabled=false when their mod is absent) before any player joins; ids are
/// assigned in registration order.
/// </summary>
public class DomainSetTemplate
{
    public List<Domain> Domains { get; private set; } = new();

    public int Count => Domains.Count;

    public Domain? this[int index]
        => Domains.Count > index && index >= 0 ? Domains[index] : null;

    /// <summary>Registers a domain and assigns its id. Returns -1 on duplicate code.</summary>
    public int AddDomain(Domain domain)
    {
        if (domain == null) return -1;
        foreach (Domain existing in Domains)
        {
            if (existing.Code == domain.Code) return -1;
        }
        domain.Id = Domains.Count;
        Domains.Add(domain);
        return domain.Id;
    }

    public Domain? FindDomain(string code)
    {
        foreach (Domain domain in Domains)
        {
            if (domain.Code == code) return domain;
        }
        return null;
    }
}
