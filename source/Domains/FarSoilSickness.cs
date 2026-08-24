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
/// THE MODEL. A level per FAMILY per tile. Harvesting a crop raises its own family's level;
/// time lowers every family's, faster when the ground is bare than when something is growing in
/// it. With accrual set between one and a half and three cycles' worth of decay, a two-course
/// A-B-A-B rotation creeps upward on BOTH crops while a three- or four-course rotation stays
/// clean. That is exactly why four-course rotations beat two-course ones historically, and here
/// it falls out of the arithmetic rather than being special-cased.
///
/// It shipped on 2026-08-24 holding a single (family, level) pair, and that was wrong in a way
/// only a long run showed: once one family had claimed the tile's one slot and its level never
/// decayed to nothing, its rotation partner could never begin accruing there at all. In a tight
/// A-B-A-B, B was permanently immune. Real ground gets sick from everything repeated on it, and
/// a two-course rotation should bleed from both ends.
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
    private static double SpeedBite => Cfg?.SickMaxSpeedPenalty ?? 0.60;
    private static double YieldBite => Cfg?.SickMaxYieldPenalty ?? 0.40;

    private static Config.TcmGlobalConfig? Cfg => AlmanacTcmModSystem.ServerInstance?.GlobalConfig;

    public static bool Enabled => Cfg?.SoilSicknessFAR ?? true;

    // ------------------------------------------------------------------ the record

    /// <summary>One family's standing in one tile.</summary>
    public sealed class Fam
    {
        public double Level;
        /// <summary>In-game day the level was last brought up to date. Decay is owed from here.</summary>
        public double Day;
        /// <summary>In-game day this family last took an accrual here, so a cut-and-come-again
        /// crop picked four times in an afternoon counts as the one crop it is.</summary>
        public double LastCreditDay = -1;
    }

    public sealed class Tile
    {
        /// <summary>Family id to its standing. Entries are pruned at zero, so a clean tile holds
        /// nothing and the whole record disappears with it. Bounded by the taxonomy at eight.</summary>
        public Dictionary<string, Fam> Fams = new();

        // --- Legacy single-pair fields. Read on load, migrated into Fams, then written as null
        // and dropped by the serializer. Kept so a world tilled under the 2026-08-24 build does
        // not silently forget ground it had already poisoned.
        public string? Family { get; set; }
        public double? Level { get; set; }
        public double? Day { get; set; }
        public double? LastCreditDay { get; set; }

        /// <summary>Folds a one-pair record into the per-family map. No-op once migrated.</summary>
        public bool Migrate()
        {
            if (string.IsNullOrEmpty(Family) || Level == null) return false;
            if (!Fams.ContainsKey(Family!))
                Fams[Family!] = new Fam { Level = Level.Value, Day = Day ?? 0, LastCreditDay = LastCreditDay ?? -1 };
            Family = null; Level = null; Day = null; LastCreditDay = null;
            return true;
        }

        /// <summary>The family this ground is worst with, or null when it is clean.</summary>
        public KeyValuePair<string, Fam>? Worst()
        {
            KeyValuePair<string, Fam>? worst = null;
            foreach (var kv in Fams)
                if (worst == null || kv.Value.Level > worst.Value.Value.Level) worst = kv;
            return worst != null && worst.Value.Value.Level > 0.01 ? worst : null;
        }
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
                if (read != null)
                {
                    map = read;
                    foreach (var t in map.Values) t.Migrate();
                }
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

    /// <summary>Migrated legacy fields serialize as null; dropping them keeps the store small
    /// and stops a converted record from being converted again.</summary>
    private static readonly Newtonsoft.Json.JsonSerializerSettings NoNulls = new()
    { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore };

    private static void Flush(ICoreServerAPI sapi, BlockPos pos, Dictionary<int, Tile> map)
    {
        var chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk == null) return;
        try
        {
            chunk.SetModdata(ModdataKey, Encoding.UTF8.GetBytes(
                Newtonsoft.Json.JsonConvert.SerializeObject(map, NoNulls)));
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
    private static void Settle(Fam f, double today, bool occupied)
    {
        double days = today - f.Day;
        if (days <= 0) { f.Day = today; return; }
        f.Level = Math.Max(0, f.Level - days * DecayDay * (occupied ? Occupied : 1.0));
        f.Day = today;
    }

    /// <summary>Brings every family on a tile up to the present and drops the ones that have
    /// burned out. A standing crop occupies the ground for all of them alike, whichever family
    /// it belongs to, so occupancy is a property of the tile and not of the record.</summary>
    private static void SettleAll(Tile t, double today, bool occupied)
    {
        List<string>? dead = null;
        foreach (var kv in t.Fams)
        {
            Settle(kv.Value, today, occupied);
            if (kv.Value.Level <= 0.01) (dead ??= new List<string>()).Add(kv.Key);
        }
        if (dead != null) foreach (string k in dead) t.Fams.Remove(k);
    }

    /// <summary>The tile's current state, decayed to now. Null when the ground is clean.</summary>
    public static Tile? Read(ICoreServerAPI sapi, BlockPos pos)
    {
        if (!Enabled) return null;
        var map = Store(sapi, pos, false);
        if (map == null) return null;
        if (!map.TryGetValue(LocalIndex(pos, GlobalConstants.ChunkSize), out var t)) return null;

        bool occupied = sapi.World.BlockAccessor.GetBlock(pos.UpCopy())?.CropProps != null;
        SettleAll(t, sapi.World.Calendar.TotalDays, occupied);
        return t.Fams.Count > 0 ? t : null;
    }

    /// <summary>Level for one family specifically: a tile sick with brassicas tells a legume
    /// nothing, which is the point of keying it by family at all.</summary>
    public static double LevelFor(ICoreServerAPI sapi, BlockPos pos, string family)
    {
        var t = Read(sapi, pos);
        return t != null && t.Fams.TryGetValue(family, out var f) ? f.Level : 0;
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
    /// One harvest day of a family raises THAT FAMILY'S level in this tile, and leaves every
    /// other family here to go on decaying. Capped to once per family per tile per in-game day
    /// for the same reason familiarity is: a cut-and-come-again crop picked four times in an
    /// afternoon is one crop standing in that ground, not four.
    /// </summary>
    public static void NoteHarvest(ICoreServerAPI sapi, BlockPos farmlandPos, string family)
    {
        if (!Enabled || string.IsNullOrEmpty(family)) return;
        var map = Store(sapi, farmlandPos, true);
        if (map == null) return;

        int idx = LocalIndex(farmlandPos, GlobalConstants.ChunkSize);
        double today = sapi.World.Calendar.TotalDays;

        if (!map.TryGetValue(idx, out var t)) map[idx] = t = new Tile();

        bool occupied = sapi.World.BlockAccessor.GetBlock(farmlandPos.UpCopy())?.CropProps != null;
        SettleAll(t, today, occupied);

        if (!t.Fams.TryGetValue(family, out var f))
        {
            // A taxonomy-bounded guard, not a design limit: eight families exist, so this can
            // only trip if a pack adds more. Drop the least sick rather than grow without bound.
            if (t.Fams.Count >= 8)
            {
                string? weakest = null;
                foreach (var kv in t.Fams)
                    if (weakest == null || kv.Value.Level < t.Fams[weakest].Level) weakest = kv.Key;
                if (weakest != null) t.Fams.Remove(weakest);
            }
            t.Fams[family] = f = new Fam { Level = 0, Day = today };
        }
        else if (f.LastCreditDay == today)
        {
            Flush(sapi, farmlandPos, map);
            return;   // already learned what this day had to teach
        }

        double before = f.Level;
        f.Level = Math.Min(Max, f.Level + Accrual);
        f.LastCreditDay = today;
        f.Day = today;
        Flush(sapi, farmlandPos, map);

        TcmLog.Cat(sapi, TcmLog.Soil,
            $"{farmlandPos} {family}: {before:0.#} -> {f.Level:0.#} "
          + $"(speed x{SpeedMul(f.Level):0.00}, yield x{YieldMul(f.Level):0.00}; "
          + $"{t.Fams.Count} family/families tracked here)");
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
            "cycle  crop  its level   speed   yield",
        };

        // Every crop letter keeps its own level, because every family repeated on a tile makes
        // that tile sick with THAT family. The row shows the level of the crop planted that
        // cycle and the penalty it actually pays, which is exactly what LevelFor decides in play.
        var levels = new Dictionary<char, double>();

        for (int i = 0; i < cycles; i++)
        {
            char c = pattern.Length == 0 ? '.' : pattern[i % pattern.Length];
            bool fallow = c == '.';

            // A standing crop occupies the ground for every family alike, so they all decay at
            // the same rate whichever one is planted.
            foreach (char k in new List<char>(levels.Keys))
                levels[k] = Math.Max(0, levels[k] - cycleDays * DecayDay * (fallow ? 1.0 : Occupied));

            if (fallow) { rows.Add($"{i + 1,5}  {c,4}  {"",9}  x1.00  x1.00"); continue; }

            levels.TryGetValue(c, out double lvl);
            lvl = Math.Min(Max, lvl + Accrual);
            levels[c] = lvl;

            rows.Add($"{i + 1,5}  {c,4}  {lvl,9:0.#}  x{SpeedMul(lvl):0.00}  x{YieldMul(lvl):0.00}"
                     // No angle brackets: the game's chat renders VTML, so a bare '<' opens a
                     // tag and the parser swallows the rest of the line. The marker went missing
                     // in play before anyone noticed the rows it belonged on.
                     + (Bites(lvl) ? "   (felt)" : ""));
        }

        var final = new List<string>();
        foreach (var kv in levels) if (kv.Value > 0.01) final.Add($"{kv.Key} {kv.Value:0.#}");
        rows.Add(final.Count > 0
            ? "ground left sick with: " + string.Join(", ", final) + " (each crop pays only its own)"
            : "ground left clean.");
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
                var sb = new StringBuilder();
                sb.Append($"{pos}\n");
                sb.Append($"  decaying    {DecayDay * (occupied ? Occupied : 1.0):0.###}/day ({(occupied ? "occupied" : "bare")})\n");
                sb.Append($"  now         day {sapi.World.Calendar.TotalDays:0.##}, clean below {CleanBelow:0} of {Max:0}\n");
                foreach (var kv in t.Fams)
                {
                    sb.Append($"  {kv.Key,-12} {kv.Value.Level,6:0.##}  growth x{SpeedMul(kv.Value.Level):0.000}"
                            + $"  yield x{YieldMul(kv.Value.Level):0.000}"
                            + (Bites(kv.Value.Level) ? "  (felt)" : "  (not felt)") + "\n");
                }
                return TextCommandResult.Success(sb.ToString().TrimEnd());
            });

        TcmLog.Info(sapi, "soil sickness: /tcmsoil registered (inspect + sim)");
    }
}
