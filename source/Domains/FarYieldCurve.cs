using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains
{
    /// <summary>
    /// What a crop actually gives you at each stage of its life, read from the drop tables at
    /// runtime rather than baked into a data file.
    ///
    /// WHY LIVE AND NOT BAKED. The pack rewrites these very numbers through the far-comb patches,
    /// Art of Growing rewrites them again, and the breeding addon adds a whole second sized
    /// ladder. A baked copy would go stale silently and the readout would start lying, which is
    /// worse than saying nothing. Reading the registry costs one pass per crop species, cached
    /// forever after.
    ///
    /// THE THING THIS EXISTS TO CATCH. Vanilla crops ripen at their last stage and hold there.
    /// Art of Growing's roots and leaves do not: carrot peaks at stage 7 of 11 and then declines
    /// for four stages into a bolted stage that drops NO food at all, only seed and rot. A
    /// readout that reports ripeness against the last stage points the farmer at the worst
    /// possible moment. Grains are unaffected (they peak at the last stage), so this must be
    /// detected per crop, never assumed.
    /// </summary>
    public static class FarYieldCurve
    {
        /// <summary>A drop that is not the point of growing the plant. Straw and rot come off
        /// nearly every stage and would flatten the curve; seed is tracked separately because
        /// whether a stage yields it is the whole question.</summary>
        private static readonly HashSet<string> ChaffCodes = new()
        {
            "rot", "drygrass", "dryhay", "agedgrass", "grass",
            "thatch", "straw", "strawlayer", "firewood",
        };

        // MATCH WHOLE CODE SEGMENTS, NEVER SUBSTRINGS. The first version of this used
        // path.Contains(), and "carrot" contains "rot": every carrot harvest was classified as
        // chaff, so the peak never resolved, the curve came back null, and the whole readout
        // silently fell back to vanilla. Caught in play 2026-08-24. "strawberry" contains "straw"
        // and would have been the next one. VS codes are hyphen-separated segments, so compare
        // segments.
        private static string Head(string path)
        {
            int i = path.IndexOf('-');
            return i < 0 ? path : path.Substring(0, i);
        }

        private static bool IsChaff(string path) => ChaffCodes.Contains(Head(path));

        private static bool IsSeed(string path)
        {
            string h = Head(path);
            return h == "seeds" || h == "seed" || h.StartsWith("seedling", StringComparison.Ordinal);
        }

        public sealed class Curve
        {
            /// <summary>Stage carrying the most food. For a grain this equals FinalStage.</summary>
            public int PeakStage;
            public double PeakFood;
            public int FinalStage;

            /// <summary>Food yield per stage, indexed by stage number (index 0 unused).</summary>
            public double[] Food = Array.Empty<double>();

            /// <summary>True when the last stage gives seed and no food: the plant has bolted.
            /// This plus PeakStage &lt; FinalStage is what makes a crop a bolting crop.</summary>
            public bool BoltsToSeed;

            /// <summary>Roots and leaves under Art of Growing. Grains and pulses are false and
            /// keep vanilla's meaning of "ripe".</summary>
            public bool Bolts => BoltsToSeed && PeakStage > 0 && PeakStage < FinalStage;

            /// <summary>The ruled warning point: the SECOND stage of decline. The first still
            /// holds most of the yield, so flagging there reads as alarmist.</summary>
            public int GoingOverStage => PeakStage + 2;

            /// <summary>True when no stage of this plant gives both food and seed, so a harvest
            /// for the table returns nothing to sow. The silent failure the readout must name.</summary>
            public bool FoodOrSeedNeverBoth;
        }

        private static readonly Dictionary<string, Curve?> cache = new();

        /// <summary>Reads the whole stage ladder for the crop this block belongs to. Returns null
        /// when the ladder cannot be walked, and every caller must treat null as "say nothing"
        /// rather than guessing, because a wrong ripeness claim is worse than a missing one.</summary>
        public static Curve? Of(ICoreAPI api, Block cropBlock)
        {
            var cp = cropBlock.CropProps;
            if (cp == null || cp.GrowthStages < 2) return null;

            string[] parts = cropBlock.Code.Path.Split('-');
            if (parts.Length < 2) return null;
            int lastIdx = parts.Length - 1;

            // Key on the ladder, not the block: every stage of one crop shares a curve. The size
            // segment stays in the key because a gigantic carrot is a different ladder.
            parts[lastIdx] = "*";
            string key = cropBlock.Code.Domain + ":" + string.Join("-", parts);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit)) return hit;
            }

            var curve = Build(api, cropBlock, lastIdx, cp.GrowthStages);
            lock (cache) { cache[key] = curve; }
            return curve;
        }

        private static Curve? Build(ICoreAPI api, Block sample, int lastIdx, int stages)
        {
            var c = new Curve { FinalStage = stages, Food = new double[stages + 1] };
            bool anyResolved = false;
            bool anyStageHadBoth = false;

            for (int n = 1; n <= stages; n++)
            {
                Block? b = api.World.GetBlock(sample.CodeWithPart(n.ToString(), lastIdx));
                if (b?.Drops == null) continue;
                anyResolved = true;

                double food = 0;
                bool seed = false;
                foreach (var d in b.Drops)
                {
                    string? path = d?.ResolvedItemstack?.Collectible?.Code?.Path;
                    if (path == null) continue;
                    if (IsSeed(path)) { seed = true; continue; }
                    if (IsChaff(path)) continue;
                    food += d!.Quantity?.avg ?? 0;
                }

                c.Food[n] = food;
                if (food > 0 && seed) anyStageHadBoth = true;
                if (n == stages) c.BoltsToSeed = seed && food <= 0;
                if (food > c.PeakFood) { c.PeakFood = food; c.PeakStage = n; }
            }

            if (!anyResolved || c.PeakStage == 0) return null;
            c.FoodOrSeedNeverBoth = !anyStageHadBoth;
            return c;
        }

        /// <summary>Test hook: the registry is fixed for a session, but a reload should not serve
        /// a curve built against the previous asset set.</summary>
        public static void Clear() { lock (cache) { cache.Clear(); } }
    }
}
