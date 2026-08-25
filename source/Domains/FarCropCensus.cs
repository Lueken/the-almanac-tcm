using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// Walks every crop in the loaded game and writes down what it actually does, stage by stage.
///
/// WHY THIS EXISTS, AND WHY IT READS THE REGISTRY. Twice now a census of this roster has been
/// built by parsing mod zips, and twice it has been wrong in the same way: a zip is what a mod
/// SHIPS, not what the game LOADS. Art of Growing's breeding addon overwrites vanilla's carrot
/// from 7 stages to 11 and hangs a bolt stage on the end; the pack's own far-comb patches rewrite
/// drop tables again on top. A file-derived census misses every one of those and reads as
/// authoritative while doing it, which is the worst combination available. The lifecycle
/// generator nearly shipped two bugs on exactly this in August 2026, and a hand stocktake of the
/// same roster recorded vanilla's stage counts for five crops the addon had already replaced.
///
/// The registry has no such problem. By the time this runs, every asset patch has applied in load
/// order and every block is the block a player will meet. <see cref="FarYieldCurve"/> already
/// reads it that way for the Grower's Eye, so this reuses the same curve rather than a second
/// implementation that could disagree with the one players see.
///
/// One row per LADDER, not per block. A crop is a stage chain, and the breeding addon multiplies
/// each chain by seven varietal sizes, so keying on the ladder is what keeps the census the size
/// of the roster instead of the size of the block registry.
/// </summary>
public static class FarCropCensus
{
    private sealed class Row
    {
        public string Ladder = "";
        public string CropId = "";
        public string Family = "";
        public int Stages;
        public int Peak;
        public double PeakFood;
        public double FinalFood;
        public string PeakHeads = "";
        public string FinalHeads = "";
        public string Shape = "";
        public string Pick = "";
        public int Variants = 1;
    }

    /// <summary>The stage chain a block belongs to: its code with the stage part replaced. Matches
    /// how FarYieldCurve keys its cache, so the two cannot drift apart.</summary>
    private static string LadderKey(Block b)
    {
        string[] parts = b.Code.Path.Split('-');
        if (parts.Length < 2) return b.Code.Domain + ":" + b.Code.Path;
        parts[parts.Length - 1] = "*";
        return b.Code.Domain + ":" + string.Join("-", parts);
    }

    /// <summary>What a cut-and-come-again pick costs, if this ladder has one. Reported because the
    /// pick's sickness accrual is scaled by exactly this fraction, and a census that did not show
    /// it would leave the one number nobody can see in play unverifiable.</summary>
    private static string PickOf(ICoreServerAPI sapi, Block block)
    {
        foreach (var beh in block.BlockBehaviors)
        {
            if (beh is not BlockBehaviorHarvestable) continue;
            var code = HarmonyLib.Traverse.Create(beh).Field("harvestedBlockCode").GetValue<AssetLocation>();
            if (code == null) return "harvestable, regrow target unreadable";
            if (!int.TryParse(block.LastCodePart(), out int from)) return "harvestable";
            string[] p = code.Path.Split('-');
            if (!int.TryParse(p[p.Length - 1], out int to)) return "harvestable";
            int stages = block.CropProps?.GrowthStages ?? 0;
            int taken = from - to;
            return stages > 0 && taken > 0
                ? $"pick {from}->{to}, {taken}/{stages} of life ({taken / (double)stages:0.00} accrual)"
                : "harvestable";
        }
        return "";
    }

    public static List<string> Build(ICoreServerAPI sapi, out string path)
    {
        var byLadder = new Dictionary<string, Row>();

        foreach (Block? b in sapi.World.Blocks)
        {
            if (b?.Code == null || b.CropProps == null) continue;
            string key = LadderKey(b);
            if (byLadder.TryGetValue(key, out var seen)) { seen.Variants++; continue; }

            string? id = FarFamiliarity.CropIdOf(sapi, b);
            var curve = FarYieldCurve.Of(sapi, b);

            // A NULL CURVE IS A FINDING, NOT A REASON TO GO QUIET. The first build skipped these,
            // and the skip hid all seven cucurbits: a melon or squash motherplant carries
            // CropProps and drops nothing at all, because the FRUIT is the harvest and it grows on
            // a separate block. The plant still occupies farmland, still sickens it and still has
            // a life the reader can see, so a census that omits it is answering a different
            // question than the one asked. Seed nurseries land here too: their ladders drop only
            // chaff, so no peak resolves.
            if (curve == null)
            {
                byLadder[key] = new Row
                {
                    Ladder = key,
                    CropId = id ?? "(not in taxonomy)",
                    Family = id == null ? "-" : (FarFamiliarity.FamilyOf(id) ?? "-"),
                    Stages = b.CropProps.GrowthStages,
                    Peak = b.CropProps.GrowthStages,
                    PeakHeads = "", FinalHeads = "",
                    Shape = id == null ? "UNREADABLE" : "FRUITS",
                };
                continue;
            }

            var row = new Row
            {
                Ladder = key,
                CropId = id ?? "(not in taxonomy)",
                Family = id == null ? "-" : (FarFamiliarity.FamilyOf(id) ?? "-"),
                Stages = curve.FinalStage,
                Peak = curve.PeakStage,
                PeakFood = curve.PeakFood,
                FinalFood = curve.Food.Length > curve.FinalStage ? curve.Food[curve.FinalStage] : 0,
                PeakHeads = string.Join(",", curve.PeakHeads),
                FinalHeads = string.Join(",", curve.FinalHeads),
                Shape = curve.Bolts ? "BOLTS"
                      : curve.Transforms ? "TRANSFORMS"
                      : curve.PeakStage == curve.FinalStage ? "RIPENS"
                      : "DECLINES",
            };

            // The pick lives on the RIPE stage's block, which is not necessarily this one.
            for (int n = curve.FinalStage; n >= 1 && row.Pick.Length == 0; n--)
            {
                var stage = sapi.World.GetBlock(b.CodeWithPart(n.ToString(), b.Code.Path.Split('-').Length - 1));
                if (stage != null) row.Pick = PickOf(sapi, stage);
            }
            byLadder[key] = row;
        }

        var rows = new List<Row>(byLadder.Values);
        int Rank(string s) => s == "UNREADABLE" ? 0 : s == "FRUITS" ? 1 : s == "BOLTS" ? 2
                            : s == "TRANSFORMS" ? 3 : s == "DECLINES" ? 4 : 5;
        rows.Sort((x, y) => Rank(x.Shape) != Rank(y.Shape)
            ? Rank(x.Shape).CompareTo(Rank(y.Shape))
            : string.CompareOrdinal(x.Ladder, y.Ladder));

        var sb = new StringBuilder();
        sb.AppendLine("# Crop census, read from the live registry");
        sb.AppendLine();
        sb.AppendLine($"Generated on day {sapi.World.Calendar.TotalDays:0} of the running world, after every");
        sb.AppendLine("asset patch has applied. This is what the game HAS, not what any mod ships.");
        sb.AppendLine();
        sb.AppendLine("Peak is the stage carrying the most food, first maximum winning ties. Shape:");
        sb.AppendLine("BOLTS = the last stage gives no food. TRANSFORMS = the last stage gives a different");
        sb.AppendLine("food than the peak. DECLINES = same food, less of it. RIPENS = the peak IS the last stage.");
        sb.AppendLine("FRUITS = the plant itself drops nothing; the harvest hangs on a separate fruit block");
        sb.AppendLine("(every cucurbit, and seed nurseries whose ladder drops only chaff). UNREADABLE = the same,");
        sb.AppendLine("but the taxonomy does not know this crop either, which is a gap worth closing.");
        sb.AppendLine();
        sb.AppendLine("| Ladder | Crop | Family | Stages | Peak | Peak gives | Last gives | Shape | Pick |");
        sb.AppendLine("|---|---|---|---:|---:|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| `{r.Ladder}`{(r.Variants > 1 ? $" x{r.Variants}" : "")} | {r.CropId} | {r.Family} "
                        + $"| {r.Stages} | {r.Peak} | {(r.PeakHeads.Length > 0 ? r.PeakHeads : "-")} {r.PeakFood:0.##} "
                        + $"| {(r.FinalFood > 0 ? $"{r.FinalHeads} {r.FinalFood:0.##}" : "no food")} "
                        + $"| **{r.Shape}** | {(r.Pick.Length > 0 ? r.Pick : "-")} |");

        var counts = new Dictionary<string, int>();
        foreach (var r in rows) counts[r.Shape] = counts.GetValueOrDefault(r.Shape) + 1;
        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        foreach (var kv in counts) sb.AppendLine($"- **{kv.Key}**: {kv.Value}");

        // THE LOG IS THE REAL OUTPUT SURFACE. Chat truncates a roster this size and cannot be
        // copied out of, so every row goes to server-main.log under one grep tag as well as to
        // the file. Aligned columns rather than a table, because a log is read in a text editor.
        sapi.Logger.Notification("[tcmcrops] census of {0} crop ladders, live registry, day {1:0}",
            rows.Count, sapi.World.Calendar.TotalDays);
        sapi.Logger.Notification("[tcmcrops] {0,-44} {1,-16} {2,-11} {3,>6} {4,>5}  {5,-22} {6,-22} {7,-11} {8}",
            "LADDER", "CROP", "FAMILY", "STAGES", "PEAK", "PEAK GIVES", "LAST GIVES", "SHAPE", "PICK");
        foreach (var r in rows)
            sapi.Logger.Notification("[tcmcrops] {0,-44} {1,-16} {2,-11} {3,6} {4,5}  {5,-22} {6,-22} {7,-11} {8}",
                r.Ladder + (r.Variants > 1 ? $" x{r.Variants}" : ""), r.CropId, r.Family,
                r.Stages, r.Peak,
                $"{(r.PeakHeads.Length > 0 ? r.PeakHeads : "-")} {r.PeakFood:0.##}",
                r.FinalFood > 0 ? $"{r.FinalHeads} {r.FinalFood:0.##}" : "no food",
                r.Shape, r.Pick.Length > 0 ? r.Pick : "-");
        foreach (var kv in counts)
            sapi.Logger.Notification("[tcmcrops] total {0,-11} {1}", kv.Key, kv.Value);

        path = Path.Combine(GamePaths.ModConfig, "almanactcm", "crop-census.md");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi, $"crop census unwritable ({e.Message})");
            path = "(could not write)";
        }

        // Chat gets the tally and where to look, nothing more: a roster this size does not survive
        // the chat window and cannot be copied out of it.
        var summary = new List<string> { $"{rows.Count} crop ladders read from the live registry." };
        foreach (var kv in counts) summary.Add($"  {kv.Key,-11} {kv.Value}");
        summary.Add("Full census in server-main.log under [tcmcrops], and at:");
        summary.Add(path);
        return summary;
    }

    public static void RegisterCommands(ICoreServerAPI sapi)
    {
        sapi.ChatCommands.Create("tcmcrops")
            .WithDescription("Write a census of every crop's stage ladder as the game actually loaded it, "
                           + "post-patch. Answers what bolts, what ripens at the end, and what changes crop.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ => TextCommandResult.Success(string.Join("\n", Build(sapi, out string _))));

        TcmLog.Info(sapi, "crop census: /tcmcrops registered");
    }
}
