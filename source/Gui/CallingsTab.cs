using System;
using System.Collections.Generic;
using AlmanacIlluminated;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace AlmanacTcm.Gui;

/// <summary>
/// The Callings tab in The Almanac: Illuminated — every trade of the world on
/// one spread: name, rank above a progress bar, and the distance to the next
/// rank. All 21 are always listed (an almanac names the callings whether you
/// have practiced them or not); Hidden only governs /tcm status brevity.
/// Reads the synced client state exclusively — level, banked XP, and required
/// XP arrive in packets, so no engine constants exist on this side.
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

    public int ColumnsPerSpread => 4;

    public List<RichTextComponentBase[]> BuildColumns(ICoreClientAPI capi, double columnWidth, double columnHeight)
    {
        CairoFont name = CairoFont.WhiteSmallText().WithFont(FontRegistry.SerifDecorative)
            .WithWeight(Cairo.FontWeight.Bold).WithColor(Ink);
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
            double fraction = required > 0 ? experience / required : (atCeiling ? 1 : 0);

            var comps = new List<RichTextComponentBase>
            {
                new RichTextComponent(capi, entry.DisplayName + "\n", name),
                new RichTextComponent(capi, Domain.RankName(level) + "\n", rank),
                new ProgressBarComponent(capi, fraction, columnWidth - 2, 8),
                new ClearFloatTextComponent(capi, 3),
            };
            if (required > 0)
            {
                comps.Add(new RichTextComponent(capi,
                    $"{Math.Ceiling(required - experience):0} to {Domain.RankName(level + 1)}\n", muted));
            }
            else if (atCeiling)
            {
                comps.Add(new RichTextComponent(capi, "The height of the art\n", muted));
            }
            comps.Add(new ClearFloatTextComponent(capi, 10));
            cards.Add(comps);
        }

        // Pack whole entries into page-height columns, the Crops tab's approach.
        double scale = RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;
        double availH = columnHeight * scale;
        var columns = new List<RichTextComponentBase[]>();
        var current = new List<RichTextComponentBase>();
        void Flush()
        {
            if (current.Count > 0) { columns.Add(current.ToArray()); current = new List<RichTextComponentBase>(); }
        }
        foreach (var card in cards)
        {
            var trial = new List<RichTextComponentBase>(current);
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
