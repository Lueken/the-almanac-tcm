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
    private readonly RungLibrary rungLib;
    private IAlmanacBookTabHost? host;
    private string? detailCode;                     // null = overview, else the domain shown
    private string? webHot;                         // non-null = trade-web view, holding this hot seat
    private Dictionary<string, DomainInfo>? info;   // lazy-loaded flavor asset
    private Dictionary<string, string>? webProse;   // lazy-loaded per-edge ledger lines
    private Dictionary<string, TradeWebAsset.Road>? webRoads;   // code-fact co-grant edges

    /// <summary>Which spread each rung's head landed on in the LAST build, so the
    /// ladder's click-to-jump turns straight to it. Content is identical between the
    /// click and the rebuild it triggers, so the stale-by-one-frame map is exact.</summary>
    private Dictionary<int, int> rungSpreadMap = new();

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };
    private static readonly double[] Rubric = { 0.541, 0.353, 0.133, 1 };
    private static readonly double[] CalloutInterior = { 0.953, 0.918, 0.824, 1 };

    public CallingsTab(LevelingClient client, RungLibrary rungLib)
    {
        this.client = client;
        this.rungLib = rungLib;
    }

    public string Label => "Callings";

    /// <summary>Between Trades (10) and Mastery (30) in the ribbon.</summary>
    public int Order => 20;

    /// <summary>Overview is a four-column grid; detail and trade-web pages are page-wide (two).</summary>
    public int ColumnsPerSpread => detailCode != null || webHot != null ? 2 : 4;

    public void OnAttached(IAlmanacBookTabHost host) => this.host = host;

    /// <summary>Selecting the ribbon tab always returns to the overview.</summary>
    public void OnActivated() { detailCode = null; webHot = null; }

    public List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight)
    {
        EnsureInfo(capi);
        if (webHot != null) return BuildWeb(capi, webHot, columnWidth, columnHeight);
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
        // Index type stepped up with the R3 pass (2026-08-22): production gives this
        // page more paper than the mock had, and the ruling stands — spend it.
        CairoFont name = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(19.5f);
        CairoFont nameMuted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Muted).WithFontSize(19.5f);
        CairoFont rank = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink).WithFontSize(17f);
        CairoFont muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted).WithFontSize(17f);
        CairoFont mutedItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Muted).WithFontSize(16f);
        CairoFont strandCaps = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithColor(Rubric).WithFontSize(17f);
        CairoFont tailItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Ink).WithFontSize(17f);

        // Phase C (2026-08-22): the flat A–Z register is replaced by the six calling
        // strands in site order, with the handoff's fixed column plan —
        // Field & Fold | Forge & Kiln || Hearth & Cask + Stone & Stream | Arms + The Unquiet + tail.
        int ceiling = 0, begun = 0, untouched = 0, total = 0;

        List<RichTextComponentBase>? CardFor(int id)
        {
            Domains.DomainRoster.Entry entry = Domains.DomainRoster.All[id];
            if (!entry.IsEnabled(capi)) return null;

            client.Domains.TryGetValue(id, out LevelingClient.DomainState? state);
            int level = state?.Level ?? 0;
            float experience = state?.Experience ?? 0f;
            float required = state?.RequiredExperience ?? 0f;
            float pending = state?.PendingBanked ?? 0f;
            bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
            bool awake = level > 0 || experience > 0 || pending > 0;
            double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);
            double pendingFraction = required > 0 ? pending / required : 0;
            total++;
            if (atCeiling) ceiling++;
            else if (awake) begun++;
            else untouched++;

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
                // An unworked calling keeps its rank line and pip row (the ladder
                // ahead is legible before the first swing) but DROPS the bar — the
                // handoff's rule: "not yet begun" entries carry no meter, and the
                // faint name says untouched before any number does.
                comps.Add(new RichTextComponent(capi, Domain.RankName(0) + "  ", muted));
                comps.Add(new InkPipsComponent(capi, Domain.TierCount, 0, -1, barred));
                comps.Add(new RichTextComponent(capi, "\n", muted));
                comps.Add(new ClearFloatTextComponent(capi, 2));
                comps.Add(new RichTextComponent(capi, "not yet begun\n", mutedItalic));
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
                comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 8, pendingFraction: pendingFraction));
                comps.Add(new ClearFloatTextComponent(capi, 3));
                if (required > 0)
                    comps.Add(new RichTextComponent(capi, ProgressCaption(level, experience, required, pending), muted));
                else if (atCeiling)
                    comps.Add(new RichTextComponent(capi, "The height of the art\n", muted));
            }
            return comps;
        }

        // Cards per strand, in strand print order; every claimed code is remembered
        // so a roster entry no strand names still prints instead of vanishing.
        var strands = Domains.DomainRoster.Strands;
        var strandCards = new List<List<RichTextComponentBase>>[strands.Length];
        var claimed = new HashSet<string>();
        for (int s = 0; s < strands.Length; s++)
        {
            strandCards[s] = new List<List<RichTextComponentBase>>();
            foreach (string code in strands[s].Codes)
            {
                claimed.Add(code);
                for (int id = 0; id < Domains.DomainRoster.All.Length; id++)
                {
                    if (Domains.DomainRoster.All[id].Code != code) continue;
                    var card = CardFor(id);
                    if (card != null) strandCards[s].Add(card);
                    break;
                }
            }
        }
        var strays = new List<List<RichTextComponentBase>>();
        for (int id = 0; id < Domains.DomainRoster.All.Length; id++)
        {
            if (claimed.Contains(Domains.DomainRoster.All[id].Code)) continue;
            var card = CardFor(id);
            if (card == null) continue;
            strays.Add(card);
            capi.Logger.Warning("[almanactcm] roster code {0} is in no strand; printing unsorted",
                Domains.DomainRoster.All[id].Code);
        }

        // The handoff's fixed plan: FF | FK || HC+SS | Arms+Unquiet+tail. Spacing is
        // decided ONCE for the whole spread (playtest ruling 2026-08-22): the densest
        // column sets the entry pitch and every column deals that same stretch into
        // its gaps — uniform rhythm reads as intent, four self-justified columns read
        // as four different pages. Sparse columns simply end higher. Strand gaps take
        // DOUBLE the stretch so groups sharing a column keep reading as separate
        // groups no matter how generous the pitch gets.
        int[][] plan = { new[] { 0 }, new[] { 1 }, new[] { 2, 3 }, new[] { 4, 5 } };
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        int lastCol = plan.Length - 1;

        // Pass 1: blocks and the base gap each wants above it, per column. A header
        // block carries its own trailing clear (the divider floats and MUST clear
        // before the first card — ChapterRenderer rule).
        var colBlocks = new List<List<RichTextComponentBase>>[plan.Length];
        var colGapBase = new List<float>[plan.Length];
        var colGapIsStrand = new List<bool>[plan.Length];
        for (int c = 0; c < plan.Length; c++)
        {
            var groups = new List<(string Name, List<List<RichTextComponentBase>> Cards)>();
            foreach (int s in plan[c])
                if (strandCards[s].Count > 0) groups.Add((strands[s].Name, strandCards[s]));
            if (c == lastCol && strays.Count > 0) groups.Add(("Unsorted", strays));

            var blocks = colBlocks[c] = new List<List<RichTextComponentBase>>();
            var gapBase = colGapBase[c] = new List<float>();
            var gapIsStrand = colGapIsStrand[c] = new List<bool>();
            foreach (var (strandName, cardsIn) in groups)
            {
                blocks.Add(new List<RichTextComponentBase>
                {
                    new RichTextComponent(capi, strandName.ToUpperInvariant() + "\n", strandCaps),
                    new DividerComponent(capi, 6),
                    new ClearFloatTextComponent(capi, 8),
                });
                gapBase.Add(blocks.Count == 1 ? 0 : StrandGap);
                gapIsStrand.Add(blocks.Count > 1);
                for (int i = 0; i < cardsIn.Count; i++)
                {
                    blocks.Add(cardsIn[i]);
                    gapBase.Add(i == 0 ? 0 : (float)EntryGap);
                    gapIsStrand.Add(false);
                }
            }
        }

        List<RichTextComponentBase> Joined(int c, double stretch)
        {
            var col = new List<RichTextComponentBase>();
            for (int b = 0; b < colBlocks[c].Count; b++)
            {
                if (colGapBase[c][b] > 0)
                {
                    double gap = colGapBase[c][b] + stretch * (colGapIsStrand[c][b] ? 2 : 1);
                    col.Add(new ClearFloatTextComponent(capi, (float)gap));
                }
                col.AddRange(colBlocks[c][b]);
            }
            return col;
        }

        var webLink = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Rubric).WithFontSize(16f);
        var tail = new List<RichTextComponentBase>
        {
            // Into the drawn web; BuildWeb falls back to the first ring node when
            // this code happens to be disabled.
            new BookLinkComponent(capi, "The trade web, drawn ›", webLink,
                _ => { webHot = "WOO"; detailCode = null; host?.Recompose(); }),
            new RichTextComponent(capi, "\n", webLink),
            new ClearFloatTextComponent(capi, 8),
            new RichTextComponent(capi, CountedStanding(total, ceiling, begun, untouched) + "\n", tailItalic),
            new ClearFloatTextComponent(capi, 10),
        };
        tail.AddRange(BuildLegend(capi));
        double tailH = ChapterRenderer.MeasureHeight(capi, tail.ToArray(), columnWidth);

        // Pass 2: each column's own justified stretch, then the spread takes the
        // MINIMUM — the fullest column governs, so no column overflows and all share
        // one pitch. Strand gaps weigh double in the budget, matching the join.
        double shared = double.MaxValue;
        for (int c = 0; c < plan.Length; c++)
        {
            double weight = 0;
            for (int g = 0; g < colGapBase[c].Count; g++)
                if (colGapBase[c][g] > 0) weight += colGapIsStrand[c][g] ? 2 : 1;
            if (weight <= 0) continue;
            double used = ChapterRenderer.MeasureHeight(capi, Joined(c, 0).ToArray(), columnWidth);
            double leftoverUn = (columnHeight * scale - (c == lastCol ? tailH : 0) - used) / scale;
            double candidate = leftoverUn <= 0 ? 0 : Math.Min(EntryStretchMax, leftoverUn / weight);
            if (candidate < shared) shared = candidate;
        }
        if (shared == double.MaxValue) shared = 0;

        var columns = new List<RichTextComponentBase[]>();
        for (int c = 0; c < plan.Length; c++)
        {
            var colOut = Joined(c, shared);
            if (c == lastCol) AppendPinnedFoot(capi, colOut, columnWidth, columnHeight, tail);
            columns.Add(colOut.ToArray());
        }
        return columns;
    }

    private const float StrandGap = 30;      // fresh-block gap above a second strand header
    private const double EntryStretchMax = 60;   // per-gap cap when dealing leftover height

    /// <summary>The index tail's counted standing line, composed from live state —
    /// "Twenty-two callings. One stands at the height of its art, 9 are begun, and
    /// 12 have not yet been touched." Zero-count clauses drop out rather than print.</summary>
    private static string CountedStanding(int total, int ceiling, int begun, int untouched)
    {
        var parts = new List<string>();
        if (ceiling > 0) parts.Add(ceiling == 1
            ? "One stands at the height of its art"
            : $"{ceiling} stand at the height of their art");
        if (begun > 0) parts.Add(begun == 1 ? "1 is begun" : $"{begun} are begun");
        if (untouched > 0) parts.Add(untouched == 1
            ? "1 has not yet been touched"
            : $"{untouched} have not yet been touched");
        string head = $"{CountWord(total)} callings.";
        if (parts.Count == 0) return head;
        string joined = parts.Count == 1 ? parts[0]
            : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + ", and " + parts[^1];
        return head + " " + joined + ".";
    }

    /// <summary>Spell a small count the way the book would set it ("Twenty-two").
    /// Past what a roster will ever hold, digits are honest enough.</summary>
    private static string CountWord(int n)
    {
        string[] small = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight",
            "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
            "Seventeen", "Eighteen", "Nineteen" };
        string[] tens = { "", "", "Twenty", "Thirty", "Forty" };
        if (n < 0 || n >= 50) return n.ToString();
        if (n < 20) return small[n];
        return n % 10 == 0 ? tens[n / 10] : tens[n / 10] + "-" + small[n % 10].ToLowerInvariant();
    }

    /// <summary>The key for the marks, pinned to the foot of the rightmost column so
    /// the stars and the barred pip read plainly. Muted, out of the way.</summary>
    private List<RichTextComponentBase> BuildLegend(ICoreClientAPI capi)
    {
        CairoFont head = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithColor(Muted).WithFontSize(15.5f);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithColor(Muted).WithFontSize(16.5f);

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
            LegendComponent.Build(capi, "THE MARKS", rows, head, body));
    }

    // --- Detail -----------------------------------------------------------

    private List<RichTextComponentBase[]> BuildDetail(ICoreClientAPI capi, string code, double columnWidth, double columnHeight)
    {
        // Sizes stepped up again 2026-08-22 R2 (playtest: mock-parity math still left
        // the bottom third of the page white; the ruling is to SPEND that space).
        // Explicit points, not multipliers of the stock GUI fonts.
        CairoFont heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(24f);
        CairoFont title = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(19f);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink).WithFontSize(18f);
        CairoFont italic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Ink).WithFontSize(18f);
        CairoFont muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted).WithFontSize(18f);
        CairoFont mutedItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithSlant(Cairo.FontSlant.Italic).WithColor(Muted).WithFontSize(18f);
        CairoFont subhead = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Muted).WithFontSize(17f);

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

        // --- The redesigned spread set (2026-08-22 handoff): identity verso, the
        // Ladder recto, then the climb rank by rank on the pages after. ---
        client.Figures.TryGetValue(code, out Dictionary<string, string>? synced);
        Dictionary<string, string> figures = Domains.DomainFigures.Merged(code, synced);
        RungLibrary.DomainRungs? domainRungs = rungLib.DomainFor(capi, code);

        // The trade web anchors to the page FOOT, the way the mock sets it — the
        // identity prose ends where it ends and the partners wait at the bottom
        // margin, so the page never trails off into blank paper mid-column.
        var tradeWebFoot = new List<RichTextComponentBase>();
        AppendTradeWeb(capi, tradeWebFoot, code, domainRungs?.tradeWeb, subhead, body, muted);
        if (tradeWebFoot.Count > 0)
            AppendPinnedFoot(capi, comps, columnWidth, columnHeight, tradeWebFoot);

        int curRank = atCeiling ? 5 : (level <= 0 ? 0 : Domain.TierOf(level) + 1);
        int barredRanks = Barred(id);
        string standing = Domain.RankName(level);
        string distance = atCeiling ? "The height of the art"
            : ProgressCaption(level, experience, required, pending).TrimEnd('\n');

        var columns = new List<RichTextComponentBase[]> { comps.ToArray() };

        List<RichTextComponentBase[]> rungCols = new();
        if (domainRungs?.rungs is { Count: > 0 })
        {
            rungCols = BuildRungColumns(capi, code, display, domainRungs, figures, curRank,
                barredRanks, standing, distance, columnWidth, columnHeight, out rungSpreadMap);
        }
        else rungSpreadMap = new();

        // The spread position prints in the book's own folio ("2 of 6" — Illuminated's
        // footerPageTotal seam), never in the flow.
        var rungModels = BuildRungModels(capi, code, domainRungs, figures, curRank, barredRanks);
        columns.Add(BuildLadderColumn(capi, rungModels, standing, distance, heading, body, italic,
            mutedItalic, rungCols.Count > 0).ToArray());
        columns.AddRange(rungCols);

        return columns;
    }

    // --- Detail: the trade-web margin block --------------------------------

    /// <summary>The identity page's foot block: partner callings from the LIVE synced
    /// adjacency (each an internal link into that calling's own detail) plus the
    /// what-changes-hands line from the rung asset. Renders nothing when the server
    /// sent no adjacency — never an invented partnership.</summary>
    private void AppendTradeWeb(ICoreClientAPI capi, List<RichTextComponentBase> comps,
        string code, string? tradeWebLine, CairoFont subhead, CairoFont body, CairoFont muted)
    {
        client.Adjacency.TryGetValue(code, out List<string>? partners);
        if ((partners == null || partners.Count == 0) && tradeWebLine == null) return;

        comps.Add(new ClearFloatTextComponent(capi, 14));
        comps.Add(new RichTextComponent(capi, "The trade web\n", subhead));
        if (partners != null && partners.Count > 0)
        {
            for (int i = 0; i < partners.Count; i++)
            {
                string pCode = partners[i];
                string pName = pCode;
                foreach (var e in Domains.DomainRoster.All)
                    if (e.Code == pCode) { pName = e.DisplayName; break; }
                if (i > 0) comps.Add(new RichTextComponent(capi, " · ", muted));
                comps.Add(new BookLinkComponent(capi, pName, body,
                    _ => { detailCode = pCode; host?.Recompose(); }));
            }
            comps.Add(new RichTextComponent(capi, "\n", body));
        }
        if (tradeWebLine != null)
            comps.Add(new RichTextComponent(capi, tradeWebLine + "\n", muted));

        var webLink = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Rubric).WithFontSize(16f);
        comps.Add(new BookLinkComponent(capi, "The whole web, drawn ›", webLink,
            _ => { webHot = code; detailCode = null; host?.Recompose(); }));
        comps.Add(new RichTextComponent(capi, "\n", webLink));
    }

    // --- The trade web (Phase D) --------------------------------------------

    /// <summary>Ring order: enabled roster entries grouped by strand in site order —
    /// the same order the index teaches, so the six families read as adjacent arcs.
    /// Unclaimed enabled entries still ride the ring, in an unlabeled trailing span.</summary>
    private static List<(string Code, string Display)> WebNodes(ICoreClientAPI capi,
        out List<(string Name, int Start, int Count)> spans)
    {
        var nodesOut = new List<(string, string)>();
        spans = new List<(string, int, int)>();
        var claimed = new HashSet<string>();
        foreach (var (name, codes) in Domains.DomainRoster.Strands)
        {
            int start = nodesOut.Count;
            foreach (string code in codes)
            {
                claimed.Add(code);
                foreach (var e in Domains.DomainRoster.All)
                {
                    if (e.Code != code) continue;
                    if (e.IsEnabled(capi)) nodesOut.Add((e.Code, e.DisplayName));
                    break;
                }
            }
            if (nodesOut.Count > start) spans.Add((name, start, nodesOut.Count - start));
        }
        int strayStart = nodesOut.Count;
        foreach (var e in Domains.DomainRoster.All)
            if (!claimed.Contains(e.Code) && e.IsEnabled(capi)) nodesOut.Add((e.Code, e.DisplayName));
        if (nodesOut.Count > strayStart) spans.Add(("", strayStart, nodesOut.Count - strayStart));
        return nodesOut;
    }

    /// <summary>Long names shortened on the ring only — the mock's SHORT map. The
    /// ledger and everywhere else keep the full display name.</summary>
    private static string RingShort(string code, string display) => code switch
    {
        "WOO" => "Woodcutting",
        "FAR" => "Farming",
        "ANI" => "Handling",
        "PAN" => "Panning",
        "BRE" => "Brewing",
        _ => display,
    };

    /// <summary>The trade web spread: verso the drawn ring, recto the hot seat's
    /// ledger. The GRAPH is the live synced adjacency — Adjacency[Y] is what Y draws
    /// FROM, so each neighbour N of Y is an N→Y edge (N gives, Y takes) — and a
    /// server that rewires adjacency draws its own web. Only the margin prose comes
    /// from the tradeweb asset, and only for edges it knows.</summary>
    private List<RichTextComponentBase[]> BuildWeb(ICoreClientAPI capi, string hotCode,
        double columnWidth, double columnHeight)
    {
        EnsureWebProse(capi);

        CairoFont heading = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(24f);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink).WithFontSize(17f);
        CairoFont bodyBold = body.Clone().WithWeight(Cairo.FontWeight.Bold);
        CairoFont muted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted).WithFontSize(17f);
        CairoFont mutedItalic = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Muted).WithFontSize(16f);
        CairoFont linkMuted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted).WithFontSize(17f);
        CairoFont subhead = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithWeight(Cairo.FontWeight.Bold).WithSlant(Cairo.FontSlant.Italic)
            .WithColor(Rubric).WithFontSize(18f);
        CairoFont openLink = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Rubric).WithFontSize(17f);

        var nodes = WebNodes(capi, out var spans);
        var idxByCode = new Dictionary<string, int>();
        for (int i = 0; i < nodes.Count; i++) idxByCode[nodes[i].Code] = i;
        if (!idxByCode.TryGetValue(hotCode, out int hotIdx) && nodes.Count > 0)
        {
            hotIdx = 0;
            hotCode = nodes[0].Code;
            webHot = hotCode;
        }
        if (nodes.Count == 0) return new List<RichTextComponentBase[]> { Array.Empty<RichTextComponentBase>() };

        var directed = new List<(int From, int To)>();
        foreach (var (code, _) in nodes)
        {
            if (!client.Adjacency.TryGetValue(code, out List<string>? neigh) || neigh == null) continue;
            foreach (string nCode in neigh)
                if (idxByCode.TryGetValue(nCode, out int ni)) directed.Add((ni, idxByCode[code]));
        }

        // Practice roads: cross-calling co-grants the domain code itself keeps
        // (rust kills feeding Temporal beside the weapon's calling, butchery paying
        // farm and hunt alike). Adjacency is config-synced; roads are code facts,
        // declared in the tradeweb asset against the site's working callings copy.
        if (webRoads != null)
        {
            var have = new HashSet<(int, int)>(directed);
            foreach (var (key, road) in webRoads)
            {
                if (road?.requires != null && !capi.ModLoader.IsModEnabled(road.requires)) continue;
                int gt = key.IndexOf('>');
                if (gt <= 0 || gt >= key.Length - 1) continue;
                if (!idxByCode.TryGetValue(key[..gt], out int fi)
                    || !idxByCode.TryGetValue(key[(gt + 1)..], out int ti)) continue;
                if (have.Add((fi, ti))) directed.Add((fi, ti));
            }
        }

        var seenPair = new HashSet<(int, int)>();
        var ringEdges = new List<(int, int)>();
        foreach (var (f, t) in directed)
        {
            var key = f < t ? (f, t) : (t, f);
            if (seenPair.Add(key)) ringEdges.Add(key);
        }

        // Verso: the ring.
        var ringNodes = new List<TradeWebRingComponent.Node>();
        foreach (var (code, display) in nodes)
            ringNodes.Add(new TradeWebRingComponent.Node { Key = code, Label = RingShort(code, display) });
        var ringGroups = new List<TradeWebRingComponent.Group>();
        foreach (var (name, start, count) in spans)
            ringGroups.Add(new TradeWebRingComponent.Group { Label = name, Start = start, Count = count });

        // The whole verso is the component: it draws the centred head itself and
        // fits the portrait ellipse to the page below it.
        var verso = new List<RichTextComponentBase>();
        verso.AddRange(TradeWebRingComponent.Build(capi, ringNodes, ringGroups, ringEdges, hotIdx,
            i => { webHot = ringNodes[i].Key; host?.Recompose(); },
            "The Trade Web", "No calling stands alone. Practice runs along the lines.",
            columnWidth, columnHeight));

        // Recto: the kinship ledger (reframed 2026-08-22). The graph is SPILLOVER
        // kinship, not goods — a mutual pair under the goods frame printed as both
        // giving and taking each other, which read as nonsense. Mutual edges now
        // collapse into ONE kindred list; the goods voice survives only in the
        // margin notes, where it reads as flavor rather than as the data model.
        string hotName = nodes[hotIdx].Display;

        (string rank, string dist) StandingFor(string code)
        {
            int rid = -1;
            for (int i = 0; i < Domains.DomainRoster.All.Length; i++)
                if (Domains.DomainRoster.All[i].Code == code) { rid = i; break; }
            client.Domains.TryGetValue(rid, out LevelingClient.DomainState? st);
            int level = st?.Level ?? 0;
            float experience = st?.Experience ?? 0f;
            float required = st?.RequiredExperience ?? 0f;
            float pending = st?.PendingBanked ?? 0f;
            bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
            string dist = atCeiling ? "The height of the art"
                : required > 0 ? ProgressCaption(level, experience, required, pending).TrimEnd('\n')
                : "a fresh page";
            return (Domain.RankName(level), dist);
        }

        (string hotRank, string hotDist) = StandingFor(hotCode);
        var recto = new List<RichTextComponentBase>
        {
            new BookLinkComponent(capi, "‹ All Callings", linkMuted,
                _ => { webHot = null; detailCode = null; host?.Recompose(); }),
            new RichTextComponent(capi, "\n", linkMuted),
            new RichTextComponent(capi, hotName + "\n", heading),
            new RichTextComponent(capi, hotRank + " · " + hotDist + "\n", mutedItalic),
            new ClearFloatTextComponent(capi, 4),
            new RichTextComponent(capi,
                "Pick any name on the facing page and the web redraws around it. The lines are not goods but habit: what one hand practises, its neighbours bank a share of at rest.\n",
                muted),
        };

        var incoming = new HashSet<int>();   // hot draws from these
        var outgoing = new HashSet<int>();   // these draw from hot
        foreach (var (f, t) in directed)
        {
            if (t == hotIdx) incoming.Add(f);
            if (f == hotIdx) outgoing.Add(t);
        }
        var kindred = new List<int>();
        var drawsFrom = new List<int>();
        var lendsTo = new List<int>();
        foreach (int p in incoming) (outgoing.Contains(p) ? kindred : drawsFrom).Add(p);
        foreach (int p in outgoing) if (!incoming.Contains(p)) lendsTo.Add(p);
        kindred.Sort(); drawsFrom.Sort(); lendsTo.Sort();   // ring order, stable

        void Ledger(string label, List<int> partners)
        {
            if (partners.Count == 0) return;   // no clause prints for nothing
            recto.Add(new ClearFloatTextComponent(capi, 10));
            recto.Add(new RichTextComponent(capi, label + "\n", subhead));
            foreach (int p in partners)
            {
                string pCode = nodes[p].Code;
                recto.Add(new RichTextComponent(capi, "— ", muted));
                recto.Add(new BookLinkComponent(capi, nodes[p].Display, bodyBold,
                    _ => { webHot = pCode; host?.Recompose(); }));
                recto.Add(new RichTextComponent(capi, ", " + StandingFor(pCode).rank + "\n", muted));
                // The margin note, where the mapmaker voiced this line (either
                // direction serves — the note is flavor, not the data model).
                string? prose = null;
                if (webProse != null && !webProse.TryGetValue($"{pCode}>{hotCode}", out prose))
                    webProse.TryGetValue($"{hotCode}>{pCode}", out prose);
                if (prose != null)
                    recto.Add(new RichTextComponent(capi, prose + "\n", mutedItalic));
                recto.Add(new ClearFloatTextComponent(capi, 2));
            }
        }
        Ledger("Kindred callings", kindred);
        Ledger("It draws from", drawsFrom);
        Ledger("It lends to", lendsTo);

        recto.Add(new ClearFloatTextComponent(capi, 12));
        recto.Add(new RichTextComponent(capi,
            "The share runs full while this hand is green, thins across Journeyman, and is gone by Master — fundamentals transfer; mastery is its own.\n",
            mutedItalic));

        recto.Add(new ClearFloatTextComponent(capi, 12));
        string openHot = hotCode;
        recto.Add(new BookLinkComponent(capi, $"Open {hotName} ›", openLink,
            _ => { detailCode = openHot; webHot = null; host?.Recompose(); }));
        recto.Add(new RichTextComponent(capi, "\n", openLink));

        return new List<RichTextComponentBase[]> { verso.ToArray(), recto.ToArray() };
    }

    private void EnsureWebProse(ICoreClientAPI capi)
    {
        if (webProse != null) return;
        try
        {
            var asset = capi.Assets.TryGet(new AssetLocation("almanactcm", "almanac/tradeweb.json"));
            TradeWebAsset? tw = asset?.ToObject<TradeWebAsset>();
            webProse = tw?.edges ?? new Dictionary<string, string>();
            webRoads = tw?.roads;
            // Road notes read through the same margin-note lookup as edge prose.
            if (webRoads != null)
                foreach (var (key, road) in webRoads)
                    if (road?.note != null && !webProse.ContainsKey(key)) webProse[key] = road.note;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[almanactcm] could not read tradeweb.json: {0}", e.Message);
            webProse = new Dictionary<string, string>();
        }
    }

    // --- Detail: the Ladder ------------------------------------------------

    /// <summary>The six rung models the Ladder draws: state from the reader's own
    /// standing, pip counts per the settled mapping (a pip fills when its rank
    /// COMPLETES, Grandmaster all-filled), summaries from the rung asset.</summary>
    private List<LadderComponent.Rung> BuildRungModels(ICoreClientAPI capi, string code,
        RungLibrary.DomainRungs? domainRungs, Dictionary<string, string>? figures,
        int curRank, int barredRanks)
    {
        var models = new List<LadderComponent.Rung>();
        for (int i = 0; i < 6; i++)
        {
            string? summary = null;
            if (domainRungs?.rungs != null && i < domainRungs.rungs.Count)
                summary = domainRungs.rungs[i].summary;

            models.Add(new LadderComponent.Rung
            {
                Label = BandName(i),
                Body = summary == null ? "" : RungLibrary.Resolve(capi, code, summary, figures),
                State = i >= 6 - barredRanks ? LadderComponent.RungState.Barred
                    : i < curRank ? LadderComponent.RungState.Done
                    : i == curRank ? LadderComponent.RungState.Current
                    : LadderComponent.RungState.Ahead,
                PipsTotal = Domain.TierCount,
                PipsFilled = i == 5 ? Domain.TierCount : Math.Max(0, i - 1),
                PipsCurrent = i == 5 ? -1 : Math.Max(0, i - 1),
                PipsBarred = barredRanks,
            });
        }
        return models;
    }

    private List<RichTextComponentBase> BuildLadderColumn(ICoreClientAPI capi,
        List<LadderComponent.Rung> models, string standing, string distance,
        CairoFont heading, CairoFont body, CairoFont italic, CairoFont mutedItalic, bool clickable)
    {
        var col = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, "The Ladder\n", heading),
            new ClearFloatTextComponent(capi, 2),
            // The mock's standing intro — rung-independent prose, so literal words
            // are correct here (no tuned figure hides in it).
            new RichTextComponent(capi,
                "Every calling climbs the same six rungs. Untrained costs you. Novice stands you level with any other hand. Everything above answers better to the work.\n",
                italic),
            new ClearFloatTextComponent(capi, 8),
        };
        Action<int>? click = clickable
            ? i => { if (rungSpreadMap.TryGetValue(i, out int s)) host?.Recompose(s); }
            : null;
        col.AddRange(LadderComponent.Build(capi, models, click));
        col.Add(new ClearFloatTextComponent(capi, 10));
        col.Add(new RichTextComponent(capi, $"You stand at {standing}. {distance}.\n", body));
        if (clickable)
            col.Add(new RichTextComponent(capi, "Any rung above turns straight to its own page.\n", mutedItalic));
        return col;
    }

    /// <summary>Pin a foot block to the bottom of a column by padding the measured
    /// remainder. Skips the pad (foot follows content directly) when the column is
    /// already too full to pin cleanly.</summary>
    private static void AppendPinnedFoot(ICoreClientAPI capi, List<RichTextComponentBase> col,
        double columnWidth, double columnHeight, List<RichTextComponentBase> foot)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double used = ChapterRenderer.MeasureHeight(capi, col.ToArray(), columnWidth);
        double footH = ChapterRenderer.MeasureHeight(capi, foot.ToArray(), columnWidth);
        double padUn = (columnHeight * scale - used - footH) / scale - 1;
        if (padUn > 2) col.Add(new ClearFloatTextComponent(capi, (float)padUn));
        col.AddRange(foot);
    }

    // --- Detail: the rung pages -------------------------------------------

    /// <summary>Band display name for rank index 0..5, from the code's own RankName so
    /// a renamed ladder renames the book.</summary>
    private static string BandName(int rankIndex)
    {
        if (rankIndex <= 0) return Domain.RankName(0);
        int entry = 1 + (rankIndex - 1) * Domain.SubLevelsPerTier;
        string name = Domain.RankName(entry);
        return name.EndsWith(" I") ? name[..^2] : name;
    }

    /// <summary>Pack the rungs onto page-height columns: whole rungs, never split across
    /// a boundary — except a rung too tall for ANY single page, which continues onto the
    /// next with a repeated head reading "continued" (the handoff's one packing
    /// exception). Every column carries an in-flow running head (verso: ‹ All Callings +
    /// the calling; recto: the rank span) and verso columns a foot line carrying the
    /// reader's standing past the identity page.</summary>
    private List<RichTextComponentBase[]> BuildRungColumns(ICoreClientAPI capi, string code,
        string display, RungLibrary.DomainRungs domainRungs, Dictionary<string, string>? figures,
        int curRank, int barredRanks, string standing, string distance,
        double columnWidth, double columnHeight, out Dictionary<int, int> rungSpread)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        const float HeadReserveUn = 40, FootReserveUn = 48, ChunkGapUn = 16;
        double availH = (columnHeight - HeadReserveUn - FootReserveUn) * scale;

        string[] charLabels = rungLib.CharacterLabels(capi);

        // 1. Each rung to one chunk, splitting only the ones no single page can hold.
        var chunks = new List<(int rung, bool cont, List<RichTextComponentBase> comps)>();
        for (int i = 0; i < domainRungs.rungs!.Count && i < 6; i++)
        {
            var full = RungComps(capi, code, domainRungs.rungs[i], i, curRank, barredRanks,
                charLabels, figures, continued: false);
            if (Measure(capi, full, columnWidth) <= availH)
            {
                chunks.Add((i, false, full));
                continue;
            }
            chunks.AddRange(SplitRung(capi, code, domainRungs.rungs[i], i, curRank, barredRanks,
                charLabels, figures, columnWidth, availH));
        }

        // 2. Greedy whole-chunk packing, trial-measured so flow quirks can't overflow.
        var cols = new List<List<RichTextComponentBase>>();
        var colRungs = new List<List<int>>();
        var current = new List<RichTextComponentBase>();
        var currentRungs = new List<int>();
        var headSpread = new Dictionary<int, int>();

        void Flush()
        {
            if (current.Count == 0) return;
            cols.Add(current); colRungs.Add(currentRungs);
            current = new(); currentRungs = new();
        }

        foreach (var chunk in chunks)
        {
            var trial = new List<RichTextComponentBase>(current);
            if (current.Count > 0) trial.Add(new ClearFloatTextComponent(capi, ChunkGapUn));
            trial.AddRange(chunk.comps);
            if (current.Count == 0 || Measure(capi, trial, columnWidth) <= availH)
                current = trial;
            else
            {
                Flush();
                current.AddRange(chunk.comps);
            }
            if (!chunk.cont) headSpread[chunk.rung] = cols.Count;   // column it will land in
            if (!currentRungs.Contains(chunk.rung)) currentRungs.Add(chunk.rung);
        }
        Flush();

        // 3. Chrome per column: running head, and the verso foot. Absolute column
        // index = 2 + local (identity 0, ladder 1); verso = even.
        var caps = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithColor(Muted).WithFontSize(14.5f);
        var linkMuted = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Muted).WithFontSize(17f);

        var final = new List<RichTextComponentBase[]>();
        for (int c = 0; c < cols.Count; c++)
        {
            int absolute = 2 + c;
            bool verso = absolute % 2 == 0;
            var col = new List<RichTextComponentBase>();
            if (verso)
            {
                col.Add(new BookLinkComponent(capi, "‹ All Callings", linkMuted,
                    _ => { detailCode = null; host?.Recompose(); }));
                col.Add(new RichTextComponent(capi, "   " + display.ToUpperInvariant() + "\n", caps));
            }
            else
            {
                var names = colRungs[c];
                string span = names.Count == 0 ? "" : names.Count == 1 || names[0] == names[^1]
                    ? BandName(names[0])
                    : BandName(names[0]) + " — " + BandName(names[^1]);
                col.Add(new RichTextComponent(capi, span.ToUpperInvariant() + "\n", caps));
            }
            col.Add(new ClearFloatTextComponent(capi, 6));
            col.AddRange(cols[c]);

            if (verso)
            {
                // Foot order per the 2026-08-22 playtest: the standing line first, the
                // diamond ornament BELOW it, sitting right above the printed folio.
                AppendPinnedFoot(capi, col, columnWidth, columnHeight, new List<RichTextComponentBase>
                {
                    new RichTextComponent(capi, (standing + " · " + distance).ToUpperInvariant() + "\n", caps.Clone().WithFontSize(16f)),
                    new ClearFloatTextComponent(capi, 1),
                    new DividerComponent(capi, 8),
                    new ClearFloatTextComponent(capi, 0),   // divider float MUST clear
                });
            }
            final.Add(col.ToArray());
        }

        // Spread index: two columns per spread across the whole detail set.
        rungSpread = new Dictionary<int, int>();
        foreach (var (rung, colIdx) in headSpread) rungSpread[rung] = (2 + colIdx) / 2;
        return final;
    }

    /// <summary>One rung as flow components: head (rank + pips + hairline + character
    /// label, the current rank's head hung with a rubric ❯), the em-dash bullet list
    /// with figures bold, companion-mod asides as italic lines, and the named grants in
    /// the lore frame under their small-caps heading.</summary>
    private List<RichTextComponentBase> RungComps(ICoreClientAPI capi, string code,
        RungLibrary.Rung rung, int rankIndex, int curRank, int barredRanks,
        string[] charLabels, Dictionary<string, string>? figures, bool continued)
    {
        // Explicit sizes per the 2026-08-22 playtest: the rung pages must read at the
        // mock's density, and the Odibee labels must be plainly legible.
        CairoFont name = CairoFont.WhiteSmallishText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink).WithFontSize(21.5f);
        CairoFont rubricName = name.Clone().WithColor(Rubric);
        CairoFont body = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody).WithColor(Ink).WithFontSize(18f);
        CairoFont bold = body.Clone().WithWeight(Cairo.FontWeight.Bold);
        CairoFont aside = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifBody)
            .WithSlant(Cairo.FontSlant.Italic).WithColor(Muted).WithFontSize(15.5f);
        CairoFont caps = CairoFont.WhiteSmallText().WithFont(FontRegistry.DisplaySans)
            .WithColor(Muted).WithFontSize(14.5f);
        CairoFont grantCaps = caps.Clone().WithColor(Rubric).WithFontSize(15f);

        var comps = new List<RichTextComponentBase>();
        bool isCurrent = rankIndex == curRank;

        if (isCurrent) comps.Add(new RichTextComponent(capi, "› ", rubricName));
        comps.Add(new RichTextComponent(capi, BandName(rankIndex) + "  ", name));
        comps.Add(new InkPipsComponent(capi, Domain.TierCount,
            rankIndex == 5 ? Domain.TierCount : Math.Max(0, rankIndex - 1),
            rankIndex == 5 ? -1 : Math.Max(0, rankIndex - 1), barredRanks));
        comps.Add(new RichTextComponent(capi, "\n", name));
        comps.Add(new DividerComponent(capi, 8));
        // A divider floats left and MUST be cleared before the next line, or the
        // flow engine clips the first glyph beside it (ChapterRenderer pairs every
        // divider with a clear; the playtest's "ELOW THE BASELINE" was this).
        comps.Add(new ClearFloatTextComponent(capi, 2));
        string charLabel = continued ? "continued"
            : rankIndex < charLabels.Length ? charLabels[rankIndex] : "";
        if (charLabel.Length > 0)
            comps.Add(new RichTextComponent(capi, charLabel.ToUpperInvariant() + "\n", caps));
        comps.Add(new ClearFloatTextComponent(capi, 4));

        foreach (var b in rung.bullets ?? new())
        {
            string resolved = RungLibrary.Resolve(capi, code, b.text ?? "", figures);
            comps.Add(new RichTextComponent(capi, "— ", body));
            comps.AddRange(RichBold(capi, resolved + "\n", body, bold));
            if (b.with != null)
                comps.Add(new RichTextComponent(capi, "with " + b.with + "\n", aside));
            comps.Add(new ClearFloatTextComponent(capi, 3));
        }

        foreach (var g in rung.grants ?? new())
        {
            comps.Add(new ClearFloatTextComponent(capi, 6));
            if (g.name != null)
                comps.Add(new RichTextComponent(capi, g.name.ToUpperInvariant() + "\n", grantCaps));
            string resolved = RungLibrary.Resolve(capi, code, g.text ?? "", figures).Replace("**", "");
            comps.Add(new ClearFloatTextComponent(capi, (float)CalloutComponent.OuterMargin));
            comps.Add(new CalloutComponent(capi, resolved + "\n", body, CalloutInterior, Rubric));
            comps.Add(new ClearFloatTextComponent(capi, (float)CalloutComponent.OuterMargin));
        }
        return comps;
    }

    /// <summary>The oversized-rung exception: bullets flow on, each continuation column
    /// repeating the head with "continued" in the character-label slot.</summary>
    private List<(int rung, bool cont, List<RichTextComponentBase> comps)> SplitRung(
        ICoreClientAPI capi, string code, RungLibrary.Rung rung, int rankIndex, int curRank,
        int barredRanks, string[] charLabels, Dictionary<string, string>? figures,
        double columnWidth, double availH)
    {
        var result = new List<(int, bool, List<RichTextComponentBase>)>();

        // Bullet-granular pieces, each prefixed by a fresh head when a page fills.
        var pending = new List<RungLibrary.Bullet>(rung.bullets ?? new());
        var grants = new List<RungLibrary.Grant>(rung.grants ?? new());
        bool first = true;

        while (pending.Count > 0 || grants.Count > 0)
        {
            var probeRung = new RungLibrary.Rung { rank = rung.rank, bullets = new(), grants = new() };
            List<RichTextComponentBase> built;
            while (true)
            {
                var trialRung = new RungLibrary.Rung
                {
                    rank = rung.rank,
                    bullets = new(probeRung.bullets!),
                    grants = new(probeRung.grants!),
                };
                if (pending.Count > 0) trialRung.bullets!.Add(pending[0]);
                else if (grants.Count > 0) trialRung.grants!.Add(grants[0]);
                else break;

                var trial = RungComps(capi, code, trialRung, rankIndex, curRank, barredRanks,
                    charLabels, figures, continued: !first);
                if (Measure(capi, trial, columnWidth) > availH && (probeRung.bullets!.Count > 0 || probeRung.grants!.Count > 0))
                    break;

                probeRung = trialRung;
                if (pending.Count > 0) pending.RemoveAt(0);
                else grants.RemoveAt(0);
                // A single bullet taller than a page still ships alone rather than looping.
                if (Measure(capi, trial, columnWidth) > availH) break;
            }
            built = RungComps(capi, code, probeRung, rankIndex, curRank, barredRanks,
                charLabels, figures, continued: !first);
            result.Add((rankIndex, !first, built));
            first = false;
        }
        return result;
    }

    private static IEnumerable<RichTextComponentBase> RichBold(ICoreClientAPI capi,
        string text, CairoFont normal, CairoFont bold)
    {
        string[] parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            yield return new RichTextComponent(capi, parts[i], i % 2 == 1 ? bold : normal);
        }
    }

    private static double Measure(ICoreClientAPI capi, List<RichTextComponentBase> comps, double columnWidth)
        => ChapterRenderer.MeasureHeight(capi, comps.ToArray(), columnWidth);

    private const double EntryGap = 14;   // gap between calling cards within a strand

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

/// <summary>The tradeweb asset: 'edges' is per-edge ledger prose (flavor only —
/// the adjacency graph always comes from the live sync); 'roads' are cross-calling
/// co-grants written in the domain code itself, which DO contribute edges.</summary>
public class TradeWebAsset
{
    public Dictionary<string, string>? edges;
    public Dictionary<string, Road>? roads;

    public class Road
    {
        public string? note;
        public string? requires;   // modid gate; null = unconditional
    }
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
