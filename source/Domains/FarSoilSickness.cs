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
    private static double FertBonus => Cfg?.SickFertilityDecayBonus ?? 0.15;
    private static double FertFloor => Cfg?.SickFertilityFloor ?? 5;
    private static double FertCeil  => Cfg?.SickFertilityCeiling ?? 80;
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

        /// <summary>Whether something was GROWING here across the span now being charged, as
        /// opposed to whatever happens to stand here at the instant somebody looks.
        ///
        /// This is the whole of the fallow fix. Decay is lazy, so a span is only ever charged
        /// when something asks, and the first build sampled occupancy at that moment and applied
        /// it backwards across the lot. Nothing asks about a BARE tile (the growth hook returns
        /// before it reads anything when no crop stands), so a fallow year was invariably billed
        /// at the occupied rate by the next crop that grew on it. Fallow bought almost nothing,
        /// which quietly falsified the two-field result the whole design leans on.
        ///
        /// Defaults false, which is the safe way round for records written before this field
        /// existed: a first settle after upgrade over-heals slightly rather than over-sickening.</summary>
        public bool Occ { get; set; }

        /// <summary>The tile's remembered farmland fertility, on farmland's own 5-to-80 scale.
        /// Zero means UNKNOWN, which resolves to no bonus at all rather than to poor ground, and
        /// is refreshed by the first touch that has a loaded block entity to read.
        ///
        /// Remembered rather than sampled for the same reason <see cref="Occ"/> is: the sweep
        /// settles tiles nobody asked about and cannot reach the world for each one. It is also
        /// the truthful model, since fertility can be raised permanently under a standing crop and
        /// the span already elapsed was earned at the old rate.</summary>
        public double Fert { get; set; }

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

    /// <summary>
    /// Brings EVERY tile in a chunk store up to the present and drops the ones with nothing left
    /// to say. The two structural halves of the storage compaction (scope 2026-08-24), and they
    /// are structural rather than an encoding change because they reduce how many records exist
    /// rather than how big each one is. At village scale that is the larger number.
    ///
    /// The husk. Families are pruned at zero, and <see cref="Read"/> returns null on an empty map,
    /// but the map ENTRY survived and serialised as <c>"12345":{"Fams":{}}</c>, about 22 bytes of
    /// nothing. Every tile ever farmed and since recovered kept one forever.
    ///
    /// The sweep. Pruning only ever reached the tile being read or written, so a RETIRED field was
    /// never touched again and its records never cleaned up even though the levels decayed to zero
    /// seasons ago. Settling the whole store on flush costs a pass over what is already in memory,
    /// and decay is lazy arithmetic rather than a tick.
    ///
    /// Occupancy comes from each tile's own remembered <see cref="Tile.Occ"/> and is written back
    /// unchanged. A sweep is not a transition: nothing here has just been planted or harvested,
    /// and sampling the world for tiles nobody asked about would re-introduce exactly the fallow
    /// bug this field was added to close.
    /// </summary>
    private static void Sweep(Dictionary<int, Tile> map, double today)
    {
        List<int>? husks = null;
        foreach (var kv in map)
        {
            SettleAll(kv.Value, today, kv.Value.Occ, kv.Value.Fert);
            if (kv.Value.Fams.Count == 0) (husks ??= new List<int>()).Add(kv.Key);
        }
        if (husks != null) foreach (int k in husks) map.Remove(k);
    }

    private static void Flush(ICoreServerAPI sapi, BlockPos pos, Dictionary<int, Tile> map)
    {
        var chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk == null) return;
        Sweep(map, sapi.World.Calendar.TotalDays);
        try
        {
            if (map.Count == 0)
            {
                // A chunk whose last sick tile has recovered goes back to costing nothing at all,
                // rather than carrying an empty JSON object for the life of the save. Dropping the
                // cache entry with it keeps the in-memory side honest: the next read finds no key
                // and treats the ground as clean, which it is.
                chunk.RemoveModdata(ModdataKey);
                cache.Remove(ChunkKey(pos, GlobalConstants.ChunkSize));
            }
            else
            {
                chunk.SetModdata(ModdataKey, Encoding.UTF8.GetBytes(
                    Newtonsoft.Json.JsonConvert.SerializeObject(map, NoNulls)));
            }
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
    /// Occupancy comes from the TILE's remembered state (Tile.Occ), never from what happens to
    /// stand here at the instant of the call. The first build sampled it live and called that a
    /// small approximation "erring by at most the difference between the two rates". That was
    /// wrong, and the error was not small: nothing ever asks about a bare tile, so every fallow
    /// span was billed at the occupied rate by the next crop to grow on it. Two-field bit at
    /// cycle 5 and hit the ceiling by cycle 25 instead of biting at 15 and levelling near 70,
    /// which is to say fallowing bought nothing and the design's headline result was false in
    /// play. Spans are closed at each transition instead: NotePlanted when a crop goes in, the
    /// harvest prefix while it still stands.
    /// </summary>
    private static void Settle(Fam f, double today, bool occupied, double fertMul)
    {
        double days = today - f.Day;
        if (days <= 0) { f.Day = today; return; }
        f.Level = Math.Max(0, f.Level - days * DecayDay * (occupied ? Occupied : 1.0) * fertMul);
        f.Day = today;
    }

    // ------------------------------------------------------------------ suppressive soil

    /// <summary>
    /// Tier 1 of the soil stabilisers (scope 2026-08-24, built 2026-08-24): rich ground sheds
    /// sickness faster, permanently, with no interaction to learn and no click to spend.
    ///
    /// Take-all decline is exactly this, and it is why the tier was ruled first: organic matter
    /// feeds the microbes that antagonise the pathogen, so continuously cropped ground eventually
    /// part-suppresses its own disease. It also costs a player nothing to discover, because the
    /// soil investment it rewards is one they were already making for growth speed.
    ///
    /// IT MUST NEVER RESCUE A BAD ROTATION, and the band that holds that line is narrow. A
    /// two-course A-B-A-B faces 108 occupied days between one family's harvests, which is 28.35
    /// of decay against 34 of accrual: a margin of 5.65. Scaling decay past `34/28.35 = 1.199`
    /// makes two-course clean forever and deletes the lesson the whole domain exists to teach.
    /// The shipped 0.15 tops out at 1.15, which delays the first bite from the third course to
    /// the sixth on the best ground. See TcmGlobalConfig.SickFertilityDecayBonus for the full
    /// derivation, and re-derive it if any of the four constants move.
    ///
    /// UNKNOWN GROUND (zero) TAKES NO BONUS rather than being treated as poor. Records written
    /// before this field existed, and tiles whose farmland is not loaded, then heal at exactly
    /// the rate they always did until the first touch that can read the block entity.
    /// </summary>
    public static double FertMul(double fert)
    {
        if (fert <= 0) return 1.0;
        double lo = FertFloor, hi = FertCeil;
        if (hi <= lo) return 1.0;
        return 1.0 + FertBonus * GameMath.Clamp((fert - lo) / (hi - lo), 0, 1);
    }

    /// <summary>Farmland stores fertility per nutrient; the block variant it wears is chosen from
    /// the average of the three (BEFarmland.cs:464), so that is the honest single number.</summary>
    public static double AvgFertility(int[]? original) =>
        original == null || original.Length < 3 ? 0 : (original[0] + original[1] + original[2]) / 3.0;

    /// <summary>
    /// The tile's fertility, live. Zero when there is no farmland block entity to ask, which is
    /// the unknown case rather than the poor one.
    ///
    /// NOT `Block.Fertility`. The soil block's own fertility runs 100 to 300 and governs what
    /// grows on UNTILLED ground; the number farmland keeps comes from a separate table (verylow
    /// 5, low 25, medium 50, compost 65, high 80). Reading the wrong one puts every tile in the
    /// game below the floor, and the whole tier becomes a silent no-op that tests clean.
    /// </summary>
    private static double FertOf(ICoreServerAPI sapi, BlockPos farmlandPos) =>
        sapi.World.BlockAccessor.GetBlockEntity(farmlandPos) is IFarmlandBlockEntity be
            ? AvgFertility(be.OriginalFertility) : 0;

    /// <summary>The rate a tile is actually shedding at, for the readouts that quote it.</summary>
    public static double DecayPerDay(bool occupied, double fert) =>
        DecayDay * (occupied ? Occupied : 1.0) * FertMul(fert);

    /// <summary>Brings every family on a tile up to the present and drops the ones that have
    /// burned out. A standing crop occupies the ground for all of them alike, whichever family
    /// it belongs to, so occupancy is a property of the tile and not of the record.</summary>
    private static void SettleAll(Tile t, double today, bool occupiedNow, double fertNow)
    {
        // Charge the span at the rate that was true ACROSS it, then remember the state going
        // forward. Passing occupiedNow straight down was the bug: it billed a fallow year at the
        // occupied rate as soon as the next crop grew tall enough for anything to ask. Fertility
        // rides the same rule, so composting a plot speeds its healing from that moment on rather
        // than retroactively.
        double fertMul = FertMul(t.Fert);
        List<string>? dead = null;
        foreach (var kv in t.Fams)
        {
            Settle(kv.Value, today, t.Occ, fertMul);
            if (kv.Value.Level <= 0.01) (dead ??= new List<string>()).Add(kv.Key);
        }
        if (dead != null) foreach (string k in dead) t.Fams.Remove(k);
        t.Occ = occupiedNow;
        // Zero is "could not read it", never "poor ground", so it must not overwrite a value the
        // tile already knows. The sweep passes t.Fert straight back through for the same reason.
        if (fertNow > 0) t.Fert = fertNow;
    }

    /// <summary>Closes the bare span at the moment a crop goes in, so the ground is credited the
    /// fallow rate for the time it actually lay fallow. Without this the span stays open until
    /// something reads the tile with a crop standing on it, and the whole rest is billed as
    /// occupied. This is the single call that makes the two-field pattern behave as modelled.</summary>
    public static void NotePlanted(ICoreServerAPI sapi, BlockPos farmlandPos)
    {
        if (!Enabled) return;
        var map = Store(sapi, farmlandPos, false);
        if (map == null) return;
        if (!map.TryGetValue(LocalIndex(farmlandPos, GlobalConstants.ChunkSize), out var t)) return;
        if (t.Fams.Count == 0) return;

        SettleAll(t, sapi.World.Calendar.TotalDays, true, FertOf(sapi, farmlandPos));   // bare span ends here, occupied begins
        Flush(sapi, farmlandPos, map);
    }

    /// <summary>The tile's current state, decayed to now. Null when the ground is clean.</summary>
    public static Tile? Read(ICoreServerAPI sapi, BlockPos pos)
    {
        if (!Enabled) return null;
        var map = Store(sapi, pos, false);
        if (map == null) return null;
        if (!map.TryGetValue(LocalIndex(pos, GlobalConstants.ChunkSize), out var t)) return null;

        bool occupied = sapi.World.BlockAccessor.GetBlock(pos.UpCopy())?.CropProps != null;
        SettleAll(t, sapi.World.Calendar.TotalDays, occupied, FertOf(sapi, pos));
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

    /// <summary>Worth SAYING, which begins before anything is worth charging. Below this the
    /// ground is genuinely fine and the readout stays quiet; between here and the clean line it
    /// warns without costing, so a repeat is still free but never blind.</summary>
    public static bool Notable(double level) => level > (Cfg?.SickTiringAbove ?? 30);

    /// <summary>The line the tiring readout quotes, so a reader can judge their own margin.</summary>
    public static int CleanLine => (int)CleanBelow;

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
        SettleAll(t, today, occupied, FertOf(sapi, farmlandPos));

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

        // Push it to whoever is looking. The tree-attribute sync only carries on a resend, and a
        // harvest is the one moment the number moves far enough to matter to a reader standing
        // right there.
        sapi.World.BlockAccessor.GetBlockEntity(farmlandPos)?.MarkDirty(true);

        TcmLog.Cat(sapi, TcmLog.Soil,
            $"{farmlandPos} {family}: {before:0.#} -> {f.Level:0.#} "
          + $"(speed x{SpeedMul(f.Level):0.00}, yield x{YieldMul(f.Level):0.00}; "
          + $"{t.Fams.Count} family/families tracked here)");
    }

    // ------------------------------------------------------------------ biofumigation

    /// <summary>What a turn-in bought, per family, for the message that reports it. Empty means
    /// the ground had nothing on it worth clearing, which is a real outcome and not a failure.</summary>
    public readonly record struct Cleared(string Family, double Before, double After);

    /// <summary>
    /// Takes a share off EVERY family's level on one tile, the arithmetic half of biofumigation
    /// (scope 2026-08-24). Broad-spectrum on purpose: isothiocyanates do not care which pathogen
    /// they meet, and clearing only brassicas would make a mustard slot worth almost nothing.
    ///
    /// Strictly this tile. No radius, ruled: turning in nine plants costs exactly what harvesting
    /// nine plants costs, and the player was going to work that bed anyway, so there is no chore to
    /// design around. One mustard plant does not treat nine squares in any real sense.
    ///
    /// The tile is settled to the present first, so the share comes off a CURRENT level rather
    /// than a remembered one, and it is settled as BARE because the crop is already out of the
    /// ground by the time this is called.
    /// </summary>
    public static List<Cleared>? Biofumigate(ICoreServerAPI sapi, BlockPos farmlandPos, double clearShare)
    {
        if (!Enabled) return null;
        var map = Store(sapi, farmlandPos, false);
        if (map == null) return null;
        if (!map.TryGetValue(LocalIndex(farmlandPos, GlobalConstants.ChunkSize), out var t)) return new List<Cleared>();

        double today = sapi.World.Calendar.TotalDays;
        SettleAll(t, today, false, FertOf(sapi, farmlandPos));

        double keep = 1.0 - GameMath.Clamp(clearShare, 0, 1);
        var cleared = new List<Cleared>();
        foreach (var kv in t.Fams)
        {
            double before = kv.Value.Level;
            if (before <= 0.01) continue;
            kv.Value.Level = before * keep;
            kv.Value.Day = today;
            cleared.Add(new Cleared(kv.Key, before, kv.Value.Level));
        }

        // Flush sweeps and prunes, so a tile cleared to nothing leaves no husk behind. That
        // matters more here than anywhere else: a mass-zeroing operation is precisely the thing
        // that manufactures empty records, and a farmer using the cure as intended would have
        // generated the most of them.
        Flush(sapi, farmlandPos, map);
        sapi.World.BlockAccessor.GetBlockEntity(farmlandPos)?.MarkDirty(true);

        foreach (var c in cleared)
            TcmLog.Cat(sapi, TcmLog.Soil, $"{farmlandPos} biofumigated {c.Family}: {c.Before:0.#} -> {c.After:0.#}");
        return cleared;
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
    // ------------------------------------------------------------------ the client mirror

    /// <summary>
    /// Sickness state lives in SERVER chunk moddata and the farmland tooltip is composed on the
    /// CLIENT. 0.5.0 shipped that gap as a dead branch: the hover asked `api is ICoreServerAPI`
    /// inside a postfix that had already returned unless the side was Client, so no player at
    /// any rank ever saw a sickness line while the penalties were charged in full. Silent
    /// punishment is the worst failure this domain can have, which is why the fix is a real sync
    /// and not a relaxed type test: relaxing it would only have swapped a dead branch for a null
    /// read, because the client has no store to consult.
    ///
    /// The carrier is the farmland block entity's own attribute tree, which the engine already
    /// serialises to disk AND ships to clients. Levels are settled server-side at write time, so
    /// a packet always carries a current level rather than a remembered one.
    /// </summary>
    private const string SyncKey = "almanactcm:soilsick";

    private static readonly Dictionary<BlockPos, Dictionary<string, double>> mirror = new();

    /// <summary>Server side: stamp the settled per-family levels onto the tree being written.</summary>
    public static void SyncOut(Vintagestory.API.Common.BlockEntity __instance,
                               Vintagestory.API.Datastructures.ITreeAttribute tree)
    {
        if (tree == null || __instance?.Api is not ICoreServerAPI sapi) return;
        try
        {
            var t = Enabled ? Read(sapi, __instance.Pos) : null;
            if (t == null || t.Fams.Count == 0) { tree.RemoveAttribute(SyncKey); return; }

            var sub = new Vintagestory.API.Datastructures.TreeAttribute();
            foreach (var kv in t.Fams)
                if (kv.Value.Level > 0.01) sub.SetDouble(kv.Key, kv.Value.Level);
            if (sub.Count == 0) { tree.RemoveAttribute(SyncKey); return; }
            tree[SyncKey] = sub;
        }
        catch (Exception e) { TcmLog.Warn(sapi, $"soil sickness sync-out failed at {__instance.Pos} ({e.Message})"); }
    }

    /// <summary>Client side: keep the last synced snapshot for the tooltip to read.</summary>
    // PARAMETER NAMES ARE THE CONTRACT. Harmony injects by NAME, not by position, and a name that
    // does not exist on the target throws at patch time rather than being ignored. This shipped
    // once as "worldAccessForResolve" and took the whole sync seam down with it; the real
    // signature is FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving).
    public static void SyncIn(Vintagestory.API.Common.BlockEntity __instance,
                              Vintagestory.API.Datastructures.ITreeAttribute tree,
                              IWorldAccessor worldForResolving)
    {
        // Api is still null while a BE is being deserialised, so side comes from the world.
        if (tree == null || __instance == null || worldForResolving?.Side != EnumAppSide.Client) return;
        var pos = __instance.Pos;
        if (pos == null) return;

        lock (mirror)
        {
            if (tree[SyncKey] is not Vintagestory.API.Datastructures.ITreeAttribute sub)
            { mirror.Remove(pos); return; }

            // Bounded and safe to drop at any moment: the authority is the server store, and a
            // missing mirror entry costs a blank line rather than a wrong one.
            if (mirror.Count > 4096) mirror.Clear();

            var map = new Dictionary<string, double>();
            foreach (var kv in sub) map[kv.Key] = sub.GetDouble(kv.Key);
            mirror[pos.Copy()] = map;
        }
    }

    /// <summary>The tooltip's only view of sickness. Null means nothing is known here, which a
    /// caller must render as silence rather than as clean ground.</summary>
    public static IReadOnlyDictionary<string, double>? ClientRead(BlockPos pos)
    {
        if (pos == null) return null;
        lock (mirror) return mirror.TryGetValue(pos, out var m) ? m : null;
    }

    public static void PatchSync(ICoreAPI api, HarmonyLib.Harmony harmony)
    {
        var t = HarmonyLib.AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityFarmland");
        // DeclaredMethod on purpose: resolving up the hierarchy would patch BlockEntity itself
        // and stamp every block entity in the game.
        var to = t == null ? null : HarmonyLib.AccessTools.DeclaredMethod(t, "ToTreeAttributes");
        var from = t == null ? null : HarmonyLib.AccessTools.DeclaredMethod(t, "FromTreeAttributes");
        if (to == null || from == null)
        {
            TcmLog.Warn(api, "soil sickness: farmland tree attributes not found; the readout stays server-only this build (penalties still apply)");
            return;
        }

        // Harmony injects by parameter NAME and throws when one does not exist, so check the
        // names ourselves and degrade instead. Guessing "worldAccessForResolve" for what is
        // really "worldForResolving" took the whole seam down once already, and a thrown patch
        // is indistinguishable in the log from the domain being off.
        string wanted = "";
        foreach (var p in from.GetParameters())
            if (p.ParameterType == typeof(IWorldAccessor)) wanted = p.Name ?? "";
        if (wanted != "worldForResolving")
        {
            TcmLog.Warn(api, $"soil sickness: FromTreeAttributes names its world parameter '{wanted}', not 'worldForResolving'; the readout stays server-only this build (penalties still apply)");
            return;
        }

        // Read side first: if it throws we have applied nothing, rather than leaving the server
        // stamping trees no client ever reads.
        harmony.Patch(from, postfix: new HarmonyLib.HarmonyMethod(
            HarmonyLib.AccessTools.Method(typeof(FarSoilSickness), nameof(SyncIn))));
        harmony.Patch(to, postfix: new HarmonyLib.HarmonyMethod(
            HarmonyLib.AccessTools.Method(typeof(FarSoilSickness), nameof(SyncOut))));
        TcmLog.Info(api, "soil sickness: readout synced to clients (farmland tree attributes)");
    }

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
    /// <param name="fertility">Farmland's own fertility scale (verylow 5, low 25, medium 50,
    /// compost 65, high 80). Zero models unknown ground, which takes no suppressive bonus. This
    /// parameter exists because without it the simulator would silently model only poor soil, and
    /// the person most likely to run it is the one tuning the suppressive tier.</param>
    public static List<string> Simulate(string pattern, int cycles, double cycleDays, double fertility = 0)
    {
        double fertMul = FertMul(fertility);
        var rows = new List<string> {
            $"pattern {pattern} | {cycleDays:0.#}-day cycles | accrual {Accrual:0.#}, decay {DecayDay:0.##}/day, occupied x{Occupied:0.##}, clean below {CleanBelow:0.#}",
            $"fertility {(fertility > 0 ? $"{fertility:0}" : "unknown")} | shedding x{fertMul:0.00}",
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
                levels[k] = Math.Max(0, levels[k] - cycleDays * DecayDay * (fallow ? 1.0 : Occupied) * fertMul);

            if (fallow) { rows.Add($"{i + 1,5}  {c,4}  {"",9}  x1.00  x1.00"); continue; }

            levels.TryGetValue(c, out double lvl);
            lvl = Math.Min(Max, lvl + Accrual);
            levels[c] = lvl;

            rows.Add($"{i + 1,5}  {c,4}  {lvl,9:0.#}  x{SpeedMul(lvl):0.00}  x{YieldMul(lvl):0.00}"
                     // No angle brackets: the game's chat renders VTML, so a bare '<' opens a
                     // tag and the parser swallows the rest of the line. The marker went missing
                     // in play before anyone noticed the rows it belonged on.
                     + (Bites(lvl) ? "   (felt)" : Notable(lvl) ? "   (tiring)" : ""));
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
                           + "Usage: /tcmsoil | /tcmsoil sim <pattern> [cycles] [cycleDays] [fertility]")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("mode"),
                      sapi.ChatCommands.Parsers.OptionalWord("pattern"),
                      sapi.ChatCommands.Parsers.OptionalInt("cycles"),
                      sapi.ChatCommands.Parsers.OptionalInt("cycleDays"),
                      sapi.ChatCommands.Parsers.OptionalInt("fertility"))
            .HandleWith(args =>
            {
                string mode = (args[0] as string ?? "").ToLowerInvariant();

                if (mode == "sim")
                {
                    string pattern = (args[1] as string ?? "AAAA").ToUpperInvariant();
                    int cycles = args[2] as int? ?? 16;
                    int days = args[3] as int? ?? 54;
                    int simFert = args[4] as int? ?? 0;
                    return TextCommandResult.Success(string.Join("\n",
                        Simulate(pattern, GameMath.Clamp(cycles, 1, 60), Math.Max(1, days), Math.Max(0, simFert))));
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
                double fert = FertOf(sapi, pos);
                var sb = new StringBuilder();
                sb.Append($"{pos}\n");
                sb.Append($"  decaying    {DecayPerDay(occupied, fert):0.###}/day ({(occupied ? "occupied" : "bare")})\n");
                sb.Append($"  fertility   {(fert > 0 ? $"{fert:0} of {FertCeil:0}, shedding x{FertMul(fert):0.00}" : "unknown, no bonus")}\n");
                sb.Append($"  now         day {sapi.World.Calendar.TotalDays:0.##}, clean below {CleanBelow:0} of {Max:0}\n");
                foreach (var kv in t.Fams)
                {
                    sb.Append($"  {kv.Key,-12} {kv.Value.Level,6:0.##}  growth x{SpeedMul(kv.Value.Level):0.000}"
                            + $"  yield x{YieldMul(kv.Value.Level):0.000}"
                            + (Bites(kv.Value.Level) ? "  (felt)"
                               : Notable(kv.Value.Level) ? "  (tiring)" : "  (not felt)") + "\n");
                }
                return TextCommandResult.Success(sb.ToString().TrimEnd());
            });

        TcmLog.Info(sapi, "soil sickness: /tcmsoil registered (inspect + sim)");
    }
}
