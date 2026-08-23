using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AlmanacTcm.Gui;

/// <summary>
/// The climb copy for the Callings book, read from assets/almanactcm/almanac/rungs.json.
/// Prose only — every quoted figure is a {token} resolved here against the per-domain
/// figure dictionary the server synced (LevelingClient.Figures, from the live DomainConfig
/// via DomainFigures). A token with no figure renders as a visible [?token] marker and logs,
/// never a silently stale number: the whole point of the pipeline is that the book cannot
/// lie about a tuned value (ruled 2026-08-22).
/// </summary>
public class RungLibrary
{
    public class Bullet
    {
        public string? text;
        /// <summary>Companion mod this bullet leans on ("Falling Tree"); renders as an
        /// italic aside under the bullet rather than a parenthetical inside the sentence.</summary>
        public string? with;
    }

    public class Grant
    {
        public string? name;
        public string? text;
    }

    public class Rung
    {
        public string? rank;
        /// <summary>The one-line reading the Ladder shows for this rung.</summary>
        public string? summary;
        public List<Bullet>? bullets;
        /// <summary>Named grants — proper-name perks that take the lore frame.</summary>
        public List<Grant>? grants;
        /// <summary>Authoring note for the voice pass; never rendered.</summary>
        public string? voiceNote;
    }

    public class DomainRungs
    {
        /// <summary>The identity page's trade-web line: what changes hands. Partner
        /// names come from the synced adjacency, this carries the words.</summary>
        public string? tradeWeb;
        public List<Rung>? rungs;
    }

    /// <summary>The whole domain block (tradeWeb + rungs), or null when unauthored.</summary>
    public DomainRungs? DomainFor(ICoreClientAPI capi, string code)
    {
        var a = Load(capi);
        if (a?.domains == null) return null;
        return a.domains.TryGetValue(code, out DomainRungs? d) ? d : null;
    }

    public class RungsAsset
    {
        public string[]? characterLabels;
        public Dictionary<string, DomainRungs>? domains;
    }

    private static readonly Regex Token = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    private RungsAsset? asset;
    private bool loadTried;

    /// <summary>The six character labels, Untrained→Grandmaster; empty until loaded.</summary>
    public string[] CharacterLabels(ICoreClientAPI capi)
        => Load(capi)?.characterLabels ?? System.Array.Empty<string>();

    /// <summary>The rung copy for one domain, or null when none is authored yet — the
    /// caller renders the detail page without a climb rather than inventing one.</summary>
    public List<Rung>? RungsFor(ICoreClientAPI capi, string code)
    {
        var a = Load(capi);
        if (a?.domains == null) return null;
        return a.domains.TryGetValue(code, out DomainRungs? d) ? d?.rungs : null;
    }

    /// <summary>Fill every {token} in <paramref name="text"/> from the domain's synced
    /// figures. Missing figure (or no figure packet for the domain at all) → a visible
    /// [?token] marker plus one debug line naming what is absent.</summary>
    public static string Resolve(ICoreClientAPI capi, string code, string text,
        IReadOnlyDictionary<string, string>? figures)
    {
        return Token.Replace(text, m =>
        {
            string key = m.Groups[1].Value;
            if (figures != null && figures.TryGetValue(key, out string? v)) return v;
            capi.Logger.Debug("[almanactcm] rung copy for {0} wants figure '{1}' but the server sent none", code, key);
            return "[?" + key + "]";
        });
    }

    private RungsAsset? Load(ICoreClientAPI capi)
    {
        if (loadTried) return asset;
        loadTried = true;
        try
        {
            var loc = new AssetLocation("almanactcm", "almanac/rungs.json");
            asset = capi.Assets.TryGet(loc)?.ToObject<RungsAsset>();
            if (asset == null) capi.Logger.Warning("[almanactcm] almanac/rungs.json missing or unreadable");
        }
        catch (System.Exception e)
        {
            capi.Logger.Warning("[almanactcm] could not read rungs.json: {0}", e.Message);
            asset = null;
        }
        return asset;
    }
}
