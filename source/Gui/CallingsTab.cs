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
/// flavor asset only: no tuned constant crosses.
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

        // Read in alphabetical order. The roster itself is registration order and is
        // append-only (ids ride the wire), so the sort lives here, on the display
        // side only, and the page becomes a register the eye can land on.
        var order = new List<int>();
        for (int i = 0; i < Domains.DomainRoster.All.Length; i++) order.Add(i);
        order.Sort((a, b) => string.Compare(
            Domains.DomainRoster.All[a].DisplayName,
            Domains.DomainRoster.All[b].DisplayName,
            StringComparison.OrdinalIgnoreCase));

        var cards = new List<List<RichTextComponentBase>>();
        foreach (int id in order)
        {
            Domains.DomainRoster.Entry entry = Domains.DomainRoster.All[id];
            if (!entry.IsEnabled(capi)) continue;

            client.Domains.TryGetValue(id, out LevelingClient.DomainState? state);
            int level = state?.Level ?? 0;
            float experience = state?.Experience ?? 0f;
            float required = state?.RequiredExperience ?? 0f;
            float pending = state?.PendingBanked ?? 0f;
            bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
            bool awake = level > 0 || experience > 0 || pending > 0;
            double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);
            double pendingFraction = required > 0 ? pending / required : 0;

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
                // Every calling wears the same four lines, trained or not: name, rank
                // and pips, bar, caption. Equal cards are what let the four columns
                // share one baseline grid. The pip row draws even untrained, so the
                // ladder ahead is legible before the first swing.
                comps.Add(new RichTextComponent(capi, Domain.RankName(0) + "  ", muted));
                comps.Add(new InkPipsComponent(capi, Domain.TierCount, 0, -1, barred));
                comps.Add(new RichTextComponent(capi, "\n", muted));
                comps.Add(new ProgressBarComponent(capi, 0, columnWidth - 2, 7, inkScale: 0.55));
                comps.Add(new ClearFloatTextComponent(capi, 3));
                comps.Add(new RichTextComponent(capi, "not yet begun\n", muted));
            }
            else
            {
                // level 0 guard, matching the detail page at :221-222. TierOf(0) is -1 by design
                // (Untrained sits outside the named tiers), so an unguarded read gave the
                // overview card -1 pips for an untrained domain. Added 2026-08-12.
                int filledPips = atCeiling ? Domain.TierCount : (level > 0 ? Domain.TierOf(level) : 0);
                int currentPip = atCeiling ? -1 : (level > 0 ? Domain.TierOf(level) : 0);

                comps.Add(new RichTextComponent(capi, Domain.RankName(level) + "  ", rank));
                comps.Add(new InkPipsComponent(capi, Domain.TierCount, filledPips, currentPip, barred));
                comps.Add(new RichTextComponent(capi, "\n", rank));
                comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 7, pendingFraction: pendingFraction));
                comps.Add(new ClearFloatTextComponent(capi, 3));
                if (required > 0)
                    comps.Add(new RichTextComponent(capi, ProgressCaption(level, experience, required, pending), muted));
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
        // (the float+clear pairing its height measurement depends on). The layout
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
        float pending = state?.PendingBanked ?? 0f;
        bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
        double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);
        double pendingFraction = required > 0 ? pending / required : 0;
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
        comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 8, pendingFraction: pendingFraction));
        comps.Add(new ClearFloatTextComponent(capi, 3));
        if (required > 0)
            comps.Add(new RichTextComponent(capi, ProgressCaption(level, experience, required, pending), muted));
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

        // ALC: the Grandmaster's Potent/Lasting emphasis toggle, the player's own choice, stamped
        // onto every remedy they make. Only a live lever at Grandmaster; below it, a note of what waits.
        if (code == "ALC")
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "Your emphasis\n", subhead));
            if (level >= Rank.Grandmaster)
            {
                bool potent = Domains.AlcEmphasis.IsPotent(capi.World.Player);
                comps.Add(new EmphasisStampComponent(capi, "Potent", potent, body,
                    potent ? null : () => { Domains.AlcEmphasis.Set(capi, true); host?.Recompose(); }));
                comps.Add(new EmphasisStampComponent(capi, "Lasting", !potent, body,
                    !potent ? null : () => { Domains.AlcEmphasis.Set(capi, false); host?.Recompose(); }));
                comps.Add(new RichTextComponent(capi, "\n", body));
                comps.Add(new ClearFloatTextComponent(capi, 6));
                comps.Add(new RichTextComponent(capi, potent
                    ? "Your remedies run stronger, trading a little of how long they hold.\n"
                    : "Your remedies run longer, trading a little of their strength.\n", mutedItalic));
            }
            else
            {
                comps.Add(new RichTextComponent(capi,
                    "At Grandmaster you will choose to brew Potent or Lasting. Until then, your work simply climbs.\n", mutedItalic));
            }
        }

        // TAI: the Grandmaster's Warm / Lasting / Cool emphasis, stamped onto every garment they make.
        // A live three-way lever only at Grandmaster; below it, a note of what waits.
        if (code == "TAI")
        {
            comps.Add(new ClearFloatTextComponent(capi, 14));
            comps.Add(new RichTextComponent(capi, "Your emphasis\n", subhead));
            if (level >= Rank.Grandmaster)
            {
                int emph = Domains.TaiEmphasis.EmphasisOf(capi.World.Player);
                void Stamp(string label, int val)
                {
                    bool on = emph == val;
                    comps.Add(new EmphasisStampComponent(capi, label, on, body,
                        on ? null : () => { Domains.TaiEmphasis.Set(capi, val); host?.Recompose(); }));
                }
                Stamp("Warm", Domains.TaiDomain.EmphWarm);
                Stamp("Lasting", Domains.TaiDomain.EmphLasting);
                Stamp("Cool", Domains.TaiDomain.EmphCool);
                comps.Add(new RichTextComponent(capi, "\n", body));
                comps.Add(new ClearFloatTextComponent(capi, 6));
                string note = emph == Domains.TaiDomain.EmphWarm
                    ? "Your garments hold more warmth, trading a little of their wear and cool.\n"
                    : emph == Domains.TaiDomain.EmphCool
                        ? "Your garments breathe cooler in the heat, trading a little warmth and wear.\n"
                        : "Your garments outlast the rest, trading a little warmth and cool.\n";
                comps.Add(new RichTextComponent(capi, note, mutedItalic));
            }
            else
            {
                comps.Add(new RichTextComponent(capi,
                    "At Grandmaster you will set your hand to Warm, Lasting, or Cool. Until then, your work simply climbs.\n", mutedItalic));
            }
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
            // A ruled page needs one row pitch for the whole spread, not one per
            // column. Two things have to agree for that: every card the same height,
            // and every column the same gap. Measure once, pad the short cards up to
            // the tallest, then derive a single gap all four columns are handed.
            double tallest = 0;
            var cardH = new double[cards.Count];
            for (int i = 0; i < cards.Count; i++)
            {
                cardH[i] = ChapterRenderer.MeasureHeight(capi, cards[i].ToArray(), columnWidth);
                if (cardH[i] > tallest) tallest = cardH[i];
            }

            // Deepest column, with the shortfall pooled in the last one (22 callings
            // reads 6/6/6/4). Spreading the remainder instead would leave three
            // columns a row longer than the fourth for no reason a reader can see.
            int depth = (int)Math.Ceiling(cards.Count / (double)cols);
            double gap = EntryGap;
            if (depth > 1)
            {
                double slack = availH - depth * tallest - (depth - 1) * EntryGap * scale;
                if (slack > 0) gap += Math.Min(EntryGapStretchMax, slack / (depth - 1) / scale);
            }

            // One row pitch only holds if the deepest column actually fits it.
            if (depth * tallest + (depth - 1) * EntryGap * scale <= availH)
            {
                var padded = new List<List<RichTextComponentBase>>(cards.Count);
                foreach (var card in cards)
                {
                    var copy = new List<RichTextComponentBase>(card);
                    double padUnscaled = (tallest - cardH[padded.Count]) / scale;
                    if (padUnscaled > 1) copy.Add(new ClearFloatTextComponent(capi, (float)padUnscaled));
                    padded.Add(copy);
                }

                // Always four columns, even if the last takes nothing: the key lives
                // at its foot and must not vanish with a short roster.
                int index = 0;
                for (int c = 0; c < cols; c++)
                {
                    var group = new List<List<RichTextComponentBase>>();
                    for (int i = 0; i < depth && index < padded.Count; i++, index++) group.Add(padded[index]);

                    var column = Join(group, gap);
                    double used = ChapterRenderer.MeasureHeight(capi, column.ToArray(), columnWidth);

                    // Pin the key to the true foot of the last column. The pooled
                    // shortfall lives here, above the key, where it reads as a margin.
                    if (c == cols - 1 && legend != null)
                    {
                        double padUnscaled = (availH - used - legendH) / scale;
                        if (padUnscaled > 2) column.Add(new ClearFloatTextComponent(capi, (float)padUnscaled));
                        column.AddRange(legend);
                    }
                    columns.Add(column.ToArray());
                }
                return columns;
            }
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

    /// <summary>The line under a bar: banked distance to the next rank, plus today's
    /// unsettled practice when there is any: a promise the rest will keep.</summary>
    private static string ProgressCaption(int level, float experience, float required, float pending)
    {
        string next = Domain.RankName(level + 1);
        if (pending <= 0.5f) return $"{Math.Ceiling(required - experience):0} to {next}\n";
        if (pending >= required - experience) return $"{next} at rest\n";
        return $"{Math.Ceiling(required - experience):0} to {next}  (+{Math.Ceiling(pending):0} at rest)\n";
    }

    /// <summary>Top tiers walled off by NEGATIVE affinity, for the barred pips. Practice
    /// stops at Master IV for neutral and positive alike (ruled 2026-08-19), but the
    /// Grandmaster pip is not BARRED for them: the declared ascension is still open, and
    /// a barred pip means a rank the player can never hold. Only a resisted trade loses a
    /// tier outright: −1 loses Grandmaster, −2 loses Master too.</summary>
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
/// Never carries tuned numbers: identity, tagline, and what mastery gives, in words.</summary>
public class DomainInfo
{
    public string? title;
    public string? tagline;
    public string? identity;
    public string? mastery;
    public string? tip;
    public string[]? techniques;
}
