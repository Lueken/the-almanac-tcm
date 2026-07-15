using System;
using System.Collections.Generic;
using AlmanacIlluminated;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacTcm.Gui;

/// <summary>
/// The Callings tab in The Almanac: Illuminated. Two views, one tab: an overview
/// of every trade (name, rank, bar, tier pips), and a per-domain detail page you
/// reach by clicking a calling. The detail carries the trade's identity, the
/// climb, and what its mastery gives. Reads synced client state and a client-safe
/// flavor asset only — no tuned constant crosses.
/// </summary>
public class CallingsTab : IAlmanacBookTab
{
    private readonly LevelingClient client;
    private IAlmanacBookTabHost? host;
    private string? detailCode;                     // null = overview, else the domain shown
    private Dictionary<string, DomainInfo>? info;   // lazy-loaded flavor asset

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };

    public CallingsTab(LevelingClient client)
    {
        this.client = client;
    }

    public string Label => "Callings";

    /// <summary>Between Trades (10) and Mastery (30) in the ribbon.</summary>
    public int Order => 20;

    /// <summary>Overview is a four-column grid; a detail page is page-wide (two).</summary>
    public int ColumnsPerSpread => detailCode == null ? 4 : 2;

    public void OnAttached(IAlmanacBookTabHost host) => this.host = host;

    /// <summary>Selecting the ribbon tab always returns to the overview.</summary>
    public void OnActivated() => detailCode = null;

    public List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight)
    {
        EnsureInfo(capi);
        return detailCode == null
            ? BuildOverview(capi, columnWidth, columnHeight)
            : BuildDetail(capi, detailCode, columnWidth, columnHeight);
    }

    private void EnsureInfo(ICoreClientAPI capi)
    {
        if (info != null) return;
        try
        {
            var asset = capi.Assets.TryGet(new AssetLocation("almanactcm", "almanac/domains.json"));
            info = asset?.ToObject<Dictionary<string, DomainInfo>>() ?? new();
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[almanactcm] could not read domains.json: {0}", e.Message);
            info = new();
        }
    }

    // --- Overview ---------------------------------------------------------

    private List<RichTextComponentBase[]> BuildOverview(ICoreClientAPI capi, double columnWidth, double columnHeight)
    {
        CairoFont name = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        CairoFont nameMuted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Muted);
        CairoFont rank = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);
        CairoFont muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted);

        var cards = new List<List<RichTextComponentBase>>();
        for (int id = 0; id < Domains.DomainRoster.All.Length; id++)
        {
            Domains.DomainRoster.Entry entry = Domains.DomainRoster.All[id];
            if (entry.RequiredMod != null && !capi.ModLoader.IsModEnabled(entry.RequiredMod)) continue;

            client.Domains.TryGetValue(id, out LevelingClient.DomainState? state);
            int level = state?.Level ?? 0;
            float experience = state?.Experience ?? 0f;
            float required = state?.RequiredExperience ?? 0f;
            bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
            bool awake = level > 0 || experience > 0;
            double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);

            // The name is a link into this trade's detail page, whether trained or not.
            // A positive-affinity trade wears a star: this calling is one of yours.
            string code = entry.Code;
            CairoFont nameFont = awake ? name : nameMuted;
            int score = Score(id);
            var comps = new List<RichTextComponentBase>
            {
                new BookLinkComponent(capi, entry.DisplayName, nameFont,
                    _ => { detailCode = code; host?.Recompose(); }),
            };
            if (score > 0) comps.Add(new AffinityMarkComponent(capi, filled: score >= 3));
            comps.Add(new RichTextComponent(capi, "\n", nameFont));

            int barred = Barred(id);
            if (!awake)
            {
                // A capped trade shows its walled ceiling even untrained; a neutral one
                // stays clean (no pip row), so the cue appears only where it means something.
                if (barred > 0)
                {
                    comps.Add(new RichTextComponent(capi, Domain.RankName(0) + "  ", muted));
                    comps.Add(new InkPipsComponent(capi, Domain.TierCount, 0, -1, barred));
                    comps.Add(new RichTextComponent(capi, "\n", muted));
                }
                else
                {
                    comps.Add(new RichTextComponent(capi, Domain.RankName(0) + "\n", muted));
                }
                comps.Add(new ProgressBarComponent(capi, 0, columnWidth - 2, 7, inkScale: 0.55));
                comps.Add(new ClearFloatTextComponent(capi, 12));
            }
            else
            {
                int filledPips = atCeiling ? Domain.TierCount : Domain.TierOf(level);
                int currentPip = atCeiling ? -1 : Domain.TierOf(level);

                comps.Add(new RichTextComponent(capi, Domain.RankName(level) + "  ", rank));
                comps.Add(new InkPipsComponent(capi, Domain.TierCount, filledPips, currentPip, barred));
                comps.Add(new RichTextComponent(capi, "\n", rank));
                comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 7));
                comps.Add(new ClearFloatTextComponent(capi, 3));
                if (required > 0)
                    comps.Add(new RichTextComponent(capi, $"{Math.Ceiling(required - experience):0} to {Domain.RankName(level + 1)}\n", muted));
                else if (atCeiling)
                    comps.Add(new RichTextComponent(capi, "The height of the art\n", muted));
            }
            cards.Add(comps);
        }

        return PackBalanced(capi, cards, columnWidth, columnHeight, BuildLegend(capi));
    }

    /// <summary>The key for the marks, pinned to the foot of the rightmost column so
    /// the stars and the barred pip read plainly. Muted, out of the way.</summary>
    private List<RichTextComponentBase> BuildLegend(ICoreClientAPI capi)
    {
        CairoFont head = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Muted);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted);

        var rows = new (LegendComponent.Glyph, string)[]
        {
            (LegendComponent.Glyph.GiftedStar, "a gifted calling"),
            (LegendComponent.Glyph.FavoredStar, "a favored trade"),
            (LegendComponent.Glyph.BarredPip, "a rank beyond reach"),
        };
        // LegendComponent.Build bundles the legend with its mandatory trailing clear
        // (the float+clear pairing its height measurement depends on) — the layout
        // detail lives in Illuminated now, not here.
        return new List<RichTextComponentBase>(
            LegendComponent.Build(capi, "The marks", rows, head, body));
    }

    // --- Detail -----------------------------------------------------------

    private List<RichTextComponentBase[]> BuildDetail(ICoreClientAPI capi, string code, double columnWidth, double columnHeight)
    {
        CairoFont heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        CairoFont title = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink);
        CairoFont italic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink);
        CairoFont muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted);
        CairoFont mutedItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Muted);
        CairoFont subhead = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Muted);

        string display = code;
        int id = -1;
        for (int i = 0; i < Domains.DomainRoster.All.Length; i++)
            if (Domains.DomainRoster.All[i].Code == code) { display = Domains.DomainRoster.All[i].DisplayName; id = i; break; }

        client.Domains.TryGetValue(id, out LevelingClient.DomainState? state);
        int level = state?.Level ?? 0;
        float experience = state?.Experience ?? 0f;
        float required = state?.RequiredExperience ?? 0f;
        bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
        double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);
        info!.TryGetValue(code, out DomainInfo? di);

        var comps = new List<RichTextComponentBase>
        {
            new BookLinkComponent(capi, "‹ All Callings", muted, _ => { detailCode = null; host?.Recompose(); }),
            new RichTextComponent(capi, "\n", muted),
            new RichTextComponent(capi, display, heading),
        };
        int score = Score(id);
        if (score > 0) comps.Add(new AffinityMarkComponent(capi, filled: score >= 3, 12));
        comps.Add(new RichTextComponent(capi, "\n", heading));

        if (di?.title != null)
            comps.Add(new RichTextComponent(capi, di.title + "\n", title));
        if (di?.tagline != null)
            comps.Add(new RichTextComponent(capi, di.tagline + "\n", italic));

        comps.Add(new ClearFloatTextComponent(capi, 6));
        int filledPips = atCeiling ? Domain.TierCount : (level > 0 ? Domain.TierOf(level) : 0);
        int currentPip = atCeiling ? -1 : (level > 0 ? Domain.TierOf(level) : 0);
        comps.Add(new RichTextComponent(capi, Domain.RankName(level) + "  ", body));
        comps.Add(new InkPipsComponent(capi, Domain.TierCount, filledPips, currentPip, Barred(id)));
        comps.Add(new RichTextComponent(capi, "\n", body));
        comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 8));
        comps.Add(new ClearFloatTextComponent(capi, 3));
        if (required > 0)
            comps.Add(new RichTextComponent(capi, $"{Math.Ceiling(required - experience):0} to {Domain.RankName(level + 1)}\n", muted));
        else if (atCeiling)
            comps.Add(new RichTextComponent(capi, "The height of the art\n", muted));
        else
            comps.Add(new RichTextComponent(capi, "Untrained, a fresh page\n", muted));

        string? affinity = AffinityLine(capi, id);
        if (affinity != null)
            comps.Add(new RichTextComponent(capi, affinity + "\n", mutedItalic));

        if (di?.identity != null)
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, di.identity + "\n", body));
        }

        if (di?.mastery != null)
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "What mastery gives\n", subhead));
            comps.Add(new RichTextComponent(capi, di.mastery + "\n", body));
        }

        if (di?.techniques != null && di.techniques.Length > 0)
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "The work\n", subhead));
            comps.Add(new RichTextComponent(capi, string.Join(" · ", di.techniques) + "\n", muted));
        }

        if (di?.tip != null)
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "A smith's tip\n", subhead));
            comps.Add(new RichTextComponent(capi, di.tip + "\n", body));
        }

        if (di?.identity == null && di?.mastery == null)
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "Its full account is still being set down.\n", italic));
        }

        return PackFlow(capi, comps, columnWidth, columnHeight);
    }

    // --- Packing ----------------------------------------------------------

    /// <summary>Overview: distribute cards evenly across the spread's four columns
    /// (21 pack 6/5/5/5), padding each column's entry gaps into the leftover height.
    /// Falls back to height-greedy packing if a balanced column would overflow.</summary>
    private List<RichTextComponentBase[]> PackBalanced(ICoreClientAPI capi,
        List<List<RichTextComponentBase>> cards, double columnWidth, double columnHeight,
        List<RichTextComponentBase>? legend = null)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = columnHeight * scale;
        int cols = 4;
        double legendH = legend != null ? ChapterRenderer.MeasureHeight(capi, legend.ToArray(), columnWidth) : 0;

        List<RichTextComponentBase> Join(List<List<RichTextComponentBase>> group, double gap)
        {
            var joined = new List<RichTextComponentBase>();
            for (int i = 0; i < group.Count; i++)
            {
                if (i > 0) joined.Add(new ClearFloatTextComponent(capi, (float)gap));
                joined.AddRange(group[i]);
            }
            return joined;
        }

        var columns = new List<RichTextComponentBase[]>();
        if (cards.Count > 0)
        {
            int index = 0;
            bool fits = true;
            for (int c = 0; c < cols && index < cards.Count; c++)
            {
                int take = cards.Count / cols + (c < cards.Count % cols ? 1 : 0);
                var group = new List<List<RichTextComponentBase>>();
                for (int i = 0; i < take && index < cards.Count; i++, index++) group.Add(cards[index]);

                var column = Join(group, EntryGap);
                double used = ChapterRenderer.MeasureHeight(capi, column.ToArray(), columnWidth);
                if (used > availH) { fits = false; break; }
                if (group.Count > 1 && used < availH)
                {
                    double extra = Math.Min(EntryGapStretchMax, (availH - used) / (group.Count - 1) / scale);
                    if (extra > 1)
                    {
                        column = Join(group, EntryGap + extra);
                        used = ChapterRenderer.MeasureHeight(capi, column.ToArray(), columnWidth);
                    }
                }
                // Pin the key to the true foot of the rightmost column: the capped
                // stretch leaves a gap at the bottom, so fill it, then drop the legend.
                if (c == cols - 1 && legend != null)
                {
                    double padUnscaled = (availH - used - legendH) / scale;
                    if (padUnscaled > 2) column.Add(new ClearFloatTextComponent(capi, (float)padUnscaled));
                    column.AddRange(legend);
                }
                columns.Add(column.ToArray());
            }
            if (fits && index >= cards.Count) return columns;
        }

        columns.Clear();
        var current = new List<RichTextComponentBase>();
        void Flush() { if (current.Count > 0) { columns.Add(current.ToArray()); current = new List<RichTextComponentBase>(); } }
        foreach (var card in cards)
        {
            var trial = new List<RichTextComponentBase>(current);
            if (current.Count > 0) trial.Add(new ClearFloatTextComponent(capi, (float)EntryGap));
            trial.AddRange(card);
            if (current.Count == 0 || ChapterRenderer.MeasureHeight(capi, trial.ToArray(), columnWidth) <= availH) current = trial;
            else { Flush(); current.AddRange(card); }
        }
        Flush();
        if (columns.Count == 0) columns.Add(Array.Empty<RichTextComponentBase>());
        return columns;
    }

    /// <summary>Detail: flow a single stream of components into page-height columns,
    /// so a long account page-turns across the spread instead of running off.</summary>
    private List<RichTextComponentBase[]> PackFlow(ICoreClientAPI capi,
        List<RichTextComponentBase> comps, double columnWidth, double columnHeight)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = columnHeight * scale;

        var columns = new List<RichTextComponentBase[]>();
        var current = new List<RichTextComponentBase>();
        foreach (var c in comps)
        {
            var trial = new List<RichTextComponentBase>(current) { c };
            if (current.Count > 0 && ChapterRenderer.MeasureHeight(capi, trial.ToArray(), columnWidth) > availH)
            {
                columns.Add(current.ToArray());
                current = new List<RichTextComponentBase> { c };
            }
            else current = trial;
        }
        if (current.Count > 0) columns.Add(current.ToArray());
        if (columns.Count == 0) columns.Add(Array.Empty<RichTextComponentBase>());
        return columns;
    }

    private const double EntryGap = 14;
    private const double EntryGapStretchMax = 26;

    /// <summary>The "why you started here" line, from the synced affinity band and the
    /// player's class. Gifted and favored trades read as native; resisted ones as
    /// uphill; a neutral trade (or an unlisted class like commoner) shows nothing.</summary>
    private string? AffinityLine(ICoreClientAPI capi, int domainId)
    {
        if (domainId < 0 || !client.Affinity.TryGetValue(domainId, out int score)) return null;
        string cls = PrettyClass(capi.World.Player?.Entity?.WatchedAttributes?.GetString("characterClass"));
        if (score >= 3) return $"Your {cls} hands took to this early.";
        if (score >= 1) return $"This comes readily to a {cls}.";
        if (score <= -1) return $"This runs against a {cls}'s nature. The climb is steeper, and the summit sits lower.";
        return null;
    }

    private static string PrettyClass(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "newcomer";
        return char.ToUpperInvariant(code[0]) + code.Substring(1);
    }

    /// <summary>Top tiers walled off by NEGATIVE affinity, for the barred pips. Neutral
    /// and positive reach Grandmaster (gated by the Masterpiece deed, not by class), so
    /// only a resisted trade caps: −1 loses Grandmaster, −2 loses Master too.</summary>
    private int Barred(int domainId)
    {
        if (domainId < 0 || !client.Affinity.TryGetValue(domainId, out int score)) return 0;
        if (score >= 0) return 0;
        return score <= -2 ? 2 : 1;
    }

    /// <summary>Synced affinity score for a domain (−2 … +3), 0 if unknown.</summary>
    private int Score(int domainId)
        => domainId >= 0 && client.Affinity.TryGetValue(domainId, out int s) ? s : 0;
}

/// <summary>Client-safe per-domain flavor, read from assets/almanactcm/almanac/domains.json.
/// Never carries tuned numbers — identity, tagline, and what mastery gives, in words.</summary>
public class DomainInfo
{
    public string? title;
    public string? tagline;
    public string? identity;
    public string? mastery;
    public string? tip;
    public string[]? techniques;
}
