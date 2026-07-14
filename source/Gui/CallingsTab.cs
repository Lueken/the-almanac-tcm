using System;
using System.Collections.Generic;
using AlmanacIlluminated;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace AlmanacTcm.Gui;

/// <summary>
/// The Callings tab in The Almanac: Illuminated — every trade of the world on
/// one spread. Awake callings (any rank or banked practice) carry full ink:
/// name, rank with the tier-pip stamp row, progress bar, and the distance to
/// the next rank. Untrained ones recede to muted ink with a faint empty bar,
/// so a squint at the page shows who this character is. Reads the synced
/// client state exclusively — no engine constants exist on this side.
/// </summary>
public class CallingsTab : IAlmanacBookTab
{
    private readonly LevelingClient client;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };
    private static readonly double[] Muted = { 0.42, 0.36, 0.28, 1 };

    public CallingsTab(LevelingClient client)
    {
        this.client = client;
    }

    public string Label => "Callings";

    /// <summary>Between the Trades (10) and Mastery (30) ribbon chapters, so TCM's
    /// three tabs read left-to-right as Trades, Callings, Mastery.</summary>
    public int Order => 20;

    public int ColumnsPerSpread => 4;

    public List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight)
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
            // Conditional domains vanish from the page when their mod is absent
            // (same check the server registration gates Enabled on).
            if (entry.RequiredMod != null && !capi.ModLoader.IsModEnabled(entry.RequiredMod)) continue;

            client.Domains.TryGetValue(id, out LevelingClient.DomainState? state);
            int level = state?.Level ?? 0;
            float experience = state?.Experience ?? 0f;
            float required = state?.RequiredExperience ?? 0f;
            bool atCeiling = required <= 0 && level >= Domain.MaxLevelDefault;
            bool awake = level > 0 || experience > 0;
            double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);

            // Cards carry no trailing gap of their own — the packer joins them
            // with spacers sized to let each column breathe down the page.
            var comps = new List<RichTextComponentBase>();
            if (!awake)
            {
                // Untrained and untouched: recede into the page. The identical
                // "5 to Novice I" caption carries no information 18 times over.
                comps.Add(new RichTextComponent(capi, entry.DisplayName + "\n", nameMuted));
                comps.Add(new RichTextComponent(capi, Domain.RankName(0) + "\n", muted));
                comps.Add(new ProgressBarComponent(capi, 0, columnWidth - 2, 7, inkScale: 0.55));
                comps.Add(new ClearFloatTextComponent(capi, 2));
            }
            else
            {
                // The wander-book stamp row: filled pips are completed tiers,
                // the outlined one is where the climb currently stands.
                int filledPips = atCeiling ? Domain.TierCount : Domain.TierOf(level);
                int currentPip = atCeiling ? -1 : Domain.TierOf(level);

                comps.Add(new RichTextComponent(capi, entry.DisplayName + "\n", name));
                comps.Add(new RichTextComponent(capi, Domain.RankName(level) + "  ", rank));
                comps.Add(new InkPipsComponent(capi, Domain.TierCount, filledPips, currentPip));
                comps.Add(new RichTextComponent(capi, "\n", rank));
                comps.Add(new ProgressBarComponent(capi, fraction, columnWidth - 2, 7));
                comps.Add(new ClearFloatTextComponent(capi, 3));
                if (required > 0)
                {
                    comps.Add(new RichTextComponent(capi,
                        $"{Math.Ceiling(required - experience):0} to {Domain.RankName(level + 1)}\n", muted));
                }
                else if (atCeiling)
                {
                    comps.Add(new RichTextComponent(capi, "The height of the art\n", muted));
                }
            }
            cards.Add(comps);
        }

        return PackBalanced(capi, cards, columnWidth, columnHeight);
    }

    /// <summary>Base breathing room between entries, unscaled GUI units.</summary>
    private const double EntryGap = 14;

    /// <summary>Extra gap cap when a column runs short — the leftover page height
    /// is spread across the gaps up to this much, so a sparse ledger column
    /// settles down the page instead of huddling at the top, while a full
    /// column stays compact and never looks stretched.</summary>
    private const double EntryGapStretchMax = 26;

    /// <summary>
    /// Distributes cards evenly across one spread's four columns (21 entries
    /// pack 6/5/5/5) so the page fills instead of leaving the last column
    /// blank, then pads each column's entry gaps into the measured leftover
    /// height. If the balanced split would overflow the column height (future
    /// taller entries), falls back to the Crops-style height-greedy packing,
    /// which page-turns onto further spreads.
    /// </summary>
    private List<RichTextComponentBase[]> PackBalanced(ICoreClientAPI capi,
        List<List<RichTextComponentBase>> cards, double columnWidth, double columnHeight)
    {
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = columnHeight * scale;
        int cols = ColumnsPerSpread;

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

                // Let the column settle into the page: share the leftover height
                // across the gaps, capped so it reads as rhythm, not stretching.
                if (group.Count > 1 && used < availH)
                {
                    double extra = Math.Min(EntryGapStretchMax, (availH - used) / (group.Count - 1) / scale);
                    if (extra > 1) column = Join(group, EntryGap + extra);
                }
                columns.Add(column.ToArray());
            }
            if (fits && index >= cards.Count) return columns;
        }

        // Fallback: pack whole cards by measured height, overflow page-turns.
        columns.Clear();
        var current = new List<RichTextComponentBase>();
        void Flush()
        {
            if (current.Count > 0) { columns.Add(current.ToArray()); current = new List<RichTextComponentBase>(); }
        }
        foreach (var card in cards)
        {
            var trial = new List<RichTextComponentBase>(current);
            if (current.Count > 0) trial.Add(new ClearFloatTextComponent(capi, (float)EntryGap));
            trial.AddRange(card);
            if (current.Count == 0 || ChapterRenderer.MeasureHeight(capi, trial.ToArray(), columnWidth) <= availH)
            {
                current = trial;
            }
            else
            {
                Flush();
                current.AddRange(card);
            }
        }
        Flush();
        if (columns.Count == 0) columns.Add(Array.Empty<RichTextComponentBase>());
        return columns;
    }
}
