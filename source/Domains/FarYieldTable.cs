using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// The per-crop, per-rank yield table (RULED 2026-08-22): replaces the generic
/// 85-percent-Untrained dock with a config surface walked crop by crop, composed with
/// Specialized Classes' own crop-yield multipliers (we multiply the same by-ref multiplier,
/// never fight theirs) and with a master switch to disable TCM yield touch entirely.
///
/// Server config: ModConfig/almanactcm/FAR-yields.json, generated on first run with one row
/// per crop id from crop-families.json, every row the legacy shape (untrained 0.85, every
/// named rank 1.0) so behaviour is unchanged out of the box; the reality comb edits the data
/// per crop against the baseline doc. enabled=false means TCM applies NO yield multiplier at
/// all, dock included. A crop missing from the table falls back to the legacy dock.
/// </summary>
public static class FarYieldTable
{
    public class TableFile
    {
        public bool enabled { get; set; } = true;
        public Dictionary<string, Dictionary<string, double>> byCrop { get; set; } = new();
    }

    private static TableFile? table;

    public static void LoadServer(ICoreServerAPI sapi)
    {
        try
        {
            table = sapi.LoadModConfig<TableFile>("almanactcm/FAR-yields.json");
        }
        catch (System.Exception e)
        {
            TcmLog.Error(sapi, $"FAR-yields.json unreadable ({e.Message}) — legacy dock only, NOT overwriting the broken file");
            table = null;
            return;
        }

        if (table == null)
        {
            table = new TableFile();
            FarFamiliarity.EnsureLoaded(sapi);
            foreach (var (_, cropId) in FarFamiliarity.AllCropIds())
            {
                table.byCrop[cropId] = new Dictionary<string, double>
                {
                    ["untrained"] = 0.85,
                    ["novice"] = 1.0,
                    ["apprentice"] = 1.0,
                    ["journeyman"] = 1.0,
                    ["master"] = 1.0,
                    ["grandmaster"] = 1.0,
                };
            }
            sapi.StoreModConfig(table, "almanactcm/FAR-yields.json");
            TcmLog.Cat(sapi, TcmLog.Config, $"FAR yield table generated: {table.byCrop.Count} crops at the legacy shape");
        }
    }

    private static string BandOf(int level) =>
        level <= 0 ? "untrained"
        : level < Rank.Apprentice ? "novice"
        : level < Rank.Journeyman ? "apprentice"
        : level < Rank.Master ? "journeyman"
        : level < Rank.Grandmaster ? "master"
        : "grandmaster";

    /// <summary>The multiplier for this crop and level; null when the crop has no row (legacy
    /// dock applies) — but never null when the master switch is off, where 1.0 means "TCM
    /// touches nothing".</summary>
    public static double? MultiplierFor(ICoreAPI api, Block cropBlock, int level)
    {
        if (table == null) return null;
        if (!table.enabled) return 1.0;
        string? cropId = FarFamiliarity.CropIdOf(api, cropBlock);
        if (cropId == null || !table.byCrop.TryGetValue(cropId, out var row)) return null;
        return row.TryGetValue(BandOf(level), out double mul) && mul > 0 ? mul : 1.0;
    }
}
