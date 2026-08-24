using System;
using System.Collections.Generic;
using System.Text;

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// Soil sickness — the reason to rotate (RULED 2026-08-24).
///
/// THE PROBLEM IT SOLVES. Nothing in the pack made rotation worth doing. A crop's clock is kept
/// in months and the soil's in hours, so 30-day months hand every grow more than three times
/// vanilla's recovery budget without changing what the crop takes out: a tile under a 40-draw
/// garlic sits at its nutrient ceiling. Fertilizer then makes a non-problem smaller still. So
/// the pressure is deliberately put somewhere fertilizer cannot reach, which is also where the
/// real pressure lives. Farmers rotate because of soil-borne disease far more than because of
/// NPK; allium white rot closes ground to alliums for decades, and no amount of feeding helps.
///
/// THE MODEL. One (family, level) pair per tile. Harvesting a crop of the stored family raises
/// the level; time lowers it, faster when the ground is bare than when something is growing in
/// it. That single pair does more work than it looks: with accrual set between one and a half
/// and three cycles' worth of decay, a two-course A-B-A-B rotation still creeps upward slowly
/// while a three- or four-course rotation stays clean. That is exactly why four-course rotations
/// beat two-course ones historically, and here it falls out of the arithmetic rather than being
/// special-cased.
///
/// WHY IT IS NOT ON THE BLOCK ENTITY. Sickness lives in CHUNK MODDATA keyed by position, not in
/// the farmland's CropAttributes, because a player who can break and replace the tilled block to
/// wipe it has made the whole system decorative in one action. Keying the ground rather than the
/// block also closes the hauling-in-clean-dirt variant, and it is the more truthful model:
/// soil-borne pathogens live in the profile, not in the few centimetres you can pick up.
///
/// ONE LOOP IS LEFT OPEN ON PURPOSE. Abandoning a sick plot and breaking new ground elsewhere
/// works, and should. That is shifting cultivation, it is historically real, and it costs the
/// player the improved soil, fencing and water they invested in. Land and labour are the right
/// constraint. Closing it would mean tracking the player rather than the ground.
/// </summary>
public static class FarSoilSickness
{
    private const string ModdataKey = "almanactcm:soilsick";

    /// <summary>Level runs 0..100 for legibility; nothing below <see cref="CleanBelow"/> is felt,
    /// so one careless repeat never bites.</summary>
    public const double Max = 100;

    // Defaults chosen so the rotation arithmetic lands where it was ruled. See Simulate.
    private static double Accrual   => Cfg?.SickAccrualPerHarvest ?? 34;
    private static double DecayDay  => Cfg?.SickFallowDecayPerDay ?? 0.35;
    private static double Occupied  => Cfg?.SickOccupiedDecayFactor ?? 0.75;
    private static double CleanBelow=> Cfg?.SickCleanBelow ?? 40;
    private static double SpeedBite => Cfg?.SickMaxSpeedPenalty ?? 0.40;
    private static double YieldBite => Cfg?.SickMaxYieldPenalty ?? 0.25;

    private static Config.TcmGlobalConfig? Cfg => AlmanacTcmModSystem.ServerInstance?.GlobalConfig;

    public static bool Enabled => Cfg?.SoilSicknessFAR ?? true;

    // ------------------------------------------------------------------ the record

    public sealed class Tile
    {
        public string Family = "";
        public double Level;
        /// <summary>In-game day the level was last brought up to date. Decay is owed from here.</summary>
        public double Day;
        /// <summary>In-game day this tile last took an accrual, so a cut-and-come-again crop
        /// picked four times in an afternoon counts as the one crop it is.</summary>
        public double LastCreditDay = -1;
    }

    /// <summary>Parsed chunk stores, written through on every mutation so cache and disk never
    /// disagree. Server-side only; there is exactly one authority.</summary>
    private static readonly Dictionary<long, Dictionary<int, Tile>> cache = new();

    private static int LocalIndex(BlockPos pos, int csize) =>
        ((pos.Y % csize + csize) % csize) * csize * csize
        + ((pos.Z % csize + csize) % csize) * csize
        + ((pos.X % csize + csize) % csize);

    private static long ChunkKey(BlockPos pos, int csize) =>
        ((long)(pos.X / csize) << 40) ^ ((long)(pos.Y / csize) << 20) ^ (pos.Z / csize);

    private static Dictionary<int, Tile>? Store(ICoreServerAPI sapi, BlockPos pos, bool create)
    {
        var chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk == null) return null;
        int csize = GlobalConstants.ChunkSize;
        long key = ChunkKey(pos, csize);

        if (cache.TryGetValue(key, out var got)) return got;

        // Bounded, and safe to drop at any moment: an in-memory tile is always derivable from
        // the on-disk one, because decay is computed from the stored day rather than accumulated
        // into it. Losing the cache costs a re-read, never a level.
        if (cache.Count > 4096) cache.Clear();

        var map = new Dictionary<int, Tile>();
        try
        {
            byte[]? raw = chunk.GetModdata(ModdataKey);
            if (raw != null && raw.Length > 0)
            {
                var read = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<int, Tile>>(
                    Encoding.UTF8.GetString(raw));
                if (read != null) map = read;
            }
        }
        catch (Exception e)
        {
            // A store we cannot read is treated as clean rather than fatal: the worst case is a
            // farmer gets a pardon, and the alternative is a chunk that will not load.
            TcmLog.Warn(sapi, $"soil sickness store unreadable at {pos} ({e.Message}); treated as clean");
        }

        if (map.Count > 0 || create) cache[key] = map;
        return map;
    }

    private static void Flush(ICoreServerAPI sapi, BlockPos pos, Dictionary<int, Tile> map)
    {
        var chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk == null) return;
        try
        {
            chunk.SetModdata(ModdataKey, Encoding.UTF8.GetBytes(
                Newtonsoft.Json.JsonConvert.SerializeObject(map)));
            chunk.MarkModified();
        }
        catch (Exception e)
        {
            TcmLog.Warn(sapi, $"soil sickness store unwritable at {pos} ({e.Message})");
        }
    }

    // ------------------------------------------------------------------ the curve

    /// <summary>
    /// Brings a tile's level up to the present. Decay is owed per elapsed in-game day, at the
    /// bare-ground rate when nothing is growing and a fraction of it when something is, so
    /// fallow is always the faster cure and rotation the one that also feeds you.
    ///
    /// Approximation, recorded honestly: occupancy is sampled NOW and applied across the whole
    /// elapsed span, because nothing records what stood here in between. It errs by at most the
    /// difference between the two rates, and only on tiles nobody has looked at in a while.
    /// </summary>
    private static void Settle(Tile t, double today, bool occupied)
    {
        double days = today - t.Day;
        if (days <= 0) { t.Day = today; return; }
        t.Level = Math.Max(0, t.Level - days * DecayDay * (occupied ? Occupied : 1.0));
        t.Day = today;
    }

    /// <summary>The tile's current state, decayed to now. Null when the ground is clean.</summary>
    public static Tile? Read(ICoreServerAPI sapi, BlockPos pos)
    {
        if (!Enabled) return null;
        var map = Store(sapi, pos, false);
        if (map == null) return null;
        int csize = GlobalConstants.ChunkSize;
        if (!map.TryGetValue(LocalIndex(pos, csize), out var t)) return null;

        bool occupied = sapi.World.BlockAccessor.GetBlock(pos.UpCopy())?.CropProps != null;
        Settle(t, sapi.World.Calendar.TotalDays, occupied);
        if (t.Level <= 0.01) return null;
        return t;
    }

    /// <summary>Level for one family specifically: a tile sick with brassicas tells a legume
    /// nothing, which is the point of keying it by family at all.</summary>
    public static double LevelFor(ICoreServerAPI sapi, BlockPos pos, string family)
    {
        var t = Read(sapi, pos);
        return t != null && t.Family == family ? t.Level : 0;
    }

    /// <summary>Growth-speed multiplier for a level: 1.0 clean, falling to 1 - SickMaxSpeedPenalty
    /// at Max. Multiplies with vanilla's nutrient speed bands rather than replacing them.</summary>
    public static double SpeedMul(double level) => 1.0 - SpeedBite * Ramp(level);

    /// <summary>Yield multiplier. Deliberately the gentler of the two, so a sick tile is a slow
    /// disappointment rather than two punishments for one mistake.</summary>
    public static double YieldMul(double level) => 1.0 - YieldBite * Ramp(level);

    private static double Ramp(double level)
    {
        double span = Max - CleanBelow;
        if (span <= 0) return level >= Max ? 1 : 0;
        return GameMath.Clamp((level - CleanBelow) / span, 0, 1);
    }

    /// <summary>Nothing at all is felt below the clean line.</summary>
    public static bool Bites(double level) => level > CleanBelow;

    // ------------------------------------------------------------------ the harvest hook

    /// <summary>
    /// One harvest day of a family on a tile raises that tile's level. Capped to once per tile
    /// per in-game day for the same reason familiarity is: a cut-and-come-again crop picked four
    /// times in an afternoon is one crop standing in that ground, not four.
    ///
    /// A tile carrying a DIFFERENT family's sickness is left to decay on its own rather than
    /// being re-keyed on the spot, so the record of what actually made this ground sick survives
    /// a season of something else.
    /// </summary>
    public static void NoteHarvest(ICoreServerAPI sapi, BlockPos farmlandPos, string family)
    {
        if (!Enabled || string.IsNullOrEmpty(family)) return;
        var map = Store(sapi, farmlandPos, true);
        if (map == null) return;

        int csize = GlobalConstants.ChunkSize;
        int idx = LocalIndex(farmlandPos, csize);
        double today = sapi.World.Calendar.TotalDays;

        if (!map.TryGetValue(idx, out var t))
        {
            t = new Tile { Family = family, Level = 0, Day = today };
            map[idx] = t;
        }

        bool occupied = sapi.World.BlockAccessor.GetBlock(farmlandPos.UpCopy())?.CropProps != null;
        double before = t.Level;
        Settle(t, today, occupied);

        if (t.Family != family)
        {
            // Somebody else's sickness still standing here. If it has burned out, this family
            // takes the slot; if not, leave it be and let this harvest pass unrecorded.
            if (t.Level > 0.01) { PruneAndFlush(sapi, farmlandPos, map, idx, t); return; }
            t.Family = family;
            t.Level = 0;
        }
        else if (Math.Abs(t.Day - today) < 1e-9 && before > 0 && t.LastCreditDay == today)
        {
            PruneAndFlush(sapi, farmlandPos, map, idx, t);
            return;   // already learned what this day had to teach
        }

        t.Level = Math.Min(Max, t.Level + Accrual);
        t.LastCreditDay = today;
        PruneAndFlush(sapi, farmlandPos, map, idx, t);

        TcmLog.Cat(sapi, TcmLog.Soil,
            $"{farmlandPos} {family}: {before:0.#} -> {t.Level:0.#} (speed x{SpeedMul(t.Level):0.00}, yield x{YieldMul(t.Level):0.00})");
    }

    private static void PruneAndFlush(ICoreServerAPI sapi, BlockPos pos, Dictionary<int, Tile> map, int idx, Tile t)
    {
        if (t.Level <= 0.01) map.Remove(idx);   // clean ground carries no record
        Flush(sapi, pos, map);
    }

    // ------------------------------------------------------------------ the growth penalty

    /// <summary>
    /// Binds the growth-speed penalty. The seam returns hours-to-next-stage, so sickness
    /// multiplies hours UP rather than a rate down, and it composes with vanilla's nutrient
    /// speed bands instead of replacing them.
    ///
    /// The return type is read at bind time and the matching postfix chosen, because a float
    /// postfix on a double method is a hard Harmony throw at startup and this is not worth
    /// taking the mod down for. An unrecognised shape warns and leaves the yield haircut doing
    /// the work alone.
    /// </summary>
    public static void PatchGrowth(ICoreAPI api, HarmonyLib.Harmony harmony)
    {
        const string typeName = "Vintagestory.GameContent.BlockEntityFarmland";
        var t = HarmonyLib.AccessTools.TypeByName(typeName);
        var m = t == null ? null : HarmonyLib.AccessTools.DeclaredMethod(t, "GetHoursForNextStage");
        if (m == null)
        {
            TcmLog.Warn(api, $"soil sickness: {typeName}.GetHoursForNextStage not found; the growth penalty is inactive this build (the yield haircut still applies)");
            return;
        }

        string postfix = m.ReturnType == typeof(double) ? nameof(HoursPostfixDouble)
                       : m.ReturnType == typeof(float) ? nameof(HoursPostfixFloat)
                       : "";
        if (postfix.Length == 0)
        {
            TcmLog.Warn(api, $"soil sickness: GetHoursForNextStage returns {m.ReturnType.Name}, which this build cannot ride; the growth penalty is inactive");
            return;
        }

        harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(
            HarmonyLib.AccessTools.Method(typeof(FarSoilSickness), postfix)));
        TcmLog.Info(api, $"soil sickness: growth penalty hooked ({typeName}.GetHoursForNextStage -> {m.ReturnType.Name})");
    }

    public static void HoursPostfixDouble(Vintagestory.GameContent.BlockEntityFarmland __instance, ref double __result)
        => __result *= HoursMul(__instance);

    public static void HoursPostfixFloat(Vintagestory.GameContent.BlockEntityFarmland __instance, ref float __result)
        => __result *= (float)HoursMul(__instance);

    private static double HoursMul(Vintagestory.GameContent.BlockEntityFarmland be)
    {
        if (!Enabled || be?.Api is not ICoreServerAPI sapi) return 1;
        var crop = sapi.World.BlockAccessor.GetBlock(be.Pos.UpCopy());
        if (crop?.CropProps == null) return 1;

        string? id = FarFamiliarity.CropIdOf(sapi, crop);
        string? family = id == null ? null : FarFamiliarity.FamilyOf(id);
        if (family == null) return 1;

        double level = LevelFor(sapi, be.Pos, family);
        if (level <= 0) return 1;
        double mul = SpeedMul(level);
        return mul <= 0.01 ? 1 : 1.0 / mul;
    }

    // ------------------------------------------------------------------ the simulator

    /// <summary>
    /// Runs the curve forward on paper. This exists because the system's whole behaviour takes
    /// in-game seasons to show, and tuning it by playing it is not tuning it. Pure arithmetic:
    /// no world, no tiles, same constants the live path uses.
    /// </summary>
    /// <param name="pattern">Family letters per cycle, e.g. "AAAA" monoculture, "AB" two-course,
    /// "ABCD" four-course. "." is a fallow cycle.</param>
    public static List<string> Simulate(string pattern, int cycles, double cycleDays)
    {
        var rows = new List<string> {
            $"pattern {pattern} | {cycleDays:0.#}-day cycles | accrual {Accrual:0.#}, decay {DecayDay:0.##}/day, occupied x{Occupied:0.##}, clean below {CleanBelow:0.#}",
            "cycle  crop  level   speed   yield",
        };
        double level = 0;
        string sickFamily = "A";   // the family we are tracking, which is the one repeated

        for (int i = 0; i < cycles; i++)
        {
            char c = pattern.Length == 0 ? '.' : pattern[i % pattern.Length];
            bool fallow = c == '.';
            level = Math.Max(0, level - cycleDays * DecayDay * (fallow ? 1.0 : Occupied));
            if (!fallow && c.ToString() == sickFamily) level = Math.Min(Max, level + Accrual);
            rows.Add($"{i + 1,5}  {c,4}  {level,6:0.#}  x{SpeedMul(level):0.00}  x{YieldMul(level):0.00}"
                     // No angle brackets: the game's chat renders VTML, so a bare '<' opens a
                     // tag and the parser swallows the rest of the line. The marker went missing
                     // in play before anyone noticed the rows it belonged on.
                     + (Bites(level) ? "   (felt)" : ""));
        }
        return rows;
    }

    // ------------------------------------------------------------------ commands

    public static void RegisterCommands(ICoreServerAPI sapi)
    {
        sapi.ChatCommands.Create("tcmsoil")
            .WithDescription("Soil sickness: inspect the tile you are looking at, or simulate a rotation. "
                           + "Usage: /tcmsoil | /tcmsoil sim <pattern> [cycles] [cycleDays]")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("mode"),
                      sapi.ChatCommands.Parsers.OptionalWord("pattern"),
                      sapi.ChatCommands.Parsers.OptionalInt("cycles"),
                      sapi.ChatCommands.Parsers.OptionalInt("cycleDays"))
            .HandleWith(args =>
            {
                string mode = (args[0] as string ?? "").ToLowerInvariant();

                if (mode == "sim")
                {
                    string pattern = (args[1] as string ?? "AAAA").ToUpperInvariant();
                    int cycles = args[2] as int? ?? 16;
                    int days = args[3] as int? ?? 54;
                    return TextCommandResult.Success(string.Join("\n",
                        Simulate(pattern, GameMath.Clamp(cycles, 1, 60), Math.Max(1, days))));
                }

                if (!Enabled) return TextCommandResult.Success("Soil sickness is switched off (SoilSicknessFAR).");

                var player = args.Caller.Player as IServerPlayer;
                var sel = player?.CurrentBlockSelection;
                if (sel == null) return TextCommandResult.Error("Look at a farmland block first.");

                // Looking at the crop is looking at the ground under it, which is what a farmer means.
                BlockPos pos = sel.Position.Copy();
                if (sapi.World.BlockAccessor.GetBlock(pos)?.CropProps != null) pos = pos.DownCopy();

                var t = Read(sapi, pos);
                if (t == null)
                    return TextCommandResult.Success($"{pos}: clean ground, nothing recorded.");

                bool occupied = sapi.World.BlockAccessor.GetBlock(pos.UpCopy())?.CropProps != null;
                return TextCommandResult.Success(
                    $"{pos}\n"
                  + $"  family      {t.Family}\n"
                  + $"  level       {t.Level:0.##} of {Max:0} (clean below {CleanBelow:0})\n"
                  + $"  settled to  day {t.Day:0.##} (now {sapi.World.Calendar.TotalDays:0.##})\n"
                  + $"  decaying    {DecayDay * (occupied ? Occupied : 1.0):0.###}/day ({(occupied ? "occupied" : "bare")})\n"
                  + $"  growth      x{SpeedMul(t.Level):0.000}\n"
                  + $"  yield       x{YieldMul(t.Level):0.000}\n"
                  + $"  felt        {(Bites(t.Level) ? "yes" : "no")}");
            });

        TcmLog.Info(sapi, "soil sickness: /tcmsoil registered (inspect + sim)");
    }
}
