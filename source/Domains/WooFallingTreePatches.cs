using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO Axis 3 — DIRECTIONAL FELLING (the signature; RULED from Jeffrey's mock 2026-07-15).
/// A felled tree lands along the STRUCK FACE, rotated by a random angle drawn from a cone
/// whose WIDTH shrinks with WOO rank and whose CENTER biases from toward-player (Untrained,
/// lethal — FallingTree's impact damage kills) to away-from-player (GM, laid exactly where
/// aimed). Positioning + face choice is the input; rank is accuracy and safety.
///
/// FallingTree-conditional. Seam: a priority-first prefix on ItemAxe.OnBlockBrokenWith stashes
/// the feller + struck face and computes the skewed direction once; a prefix on FallingTree's
/// ConfigurePivotFaller overrides the (dirX, dirZ) it bakes into every falling log of that tree.
/// </summary>
public static class WooFallingTreePatches
{
    private const double Deg2Rad = Math.PI / 180.0;

    // The break→fell→ConfigurePivotFaller chain is synchronous on the server thread.
    [ThreadStatic] private static bool skewValid;
    [ThreadStatic] private static float skewX;
    [ThreadStatic] private static float skewZ;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("fallingtree")) return;

        var ftPatch = AccessTools.TypeByName("FallingTree.FallingEntityPatch");
        var axeBreak = AccessTools.Method(typeof(ItemAxe), "OnBlockBrokenWith");
        var configurePivot = AccessTools.Method(ftPatch, "ConfigurePivotFaller");
        if (axeBreak == null || configurePivot == null)
        {
            TcmLog.Warn(api, "fallingtree present but OnBlockBrokenWith/ConfigurePivotFaller not found; WOO directional felling inactive");
            return;
        }

        // Priority.First so our stash lands BEFORE FallingTree's own prefix runs the fell and
        // calls ConfigurePivotFaller.
        harmony.Patch(axeBreak,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FellStashPatch), "Prefix")) { priority = Priority.First });
        harmony.Patch(configurePivot,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PivotDirPatch), "Prefix")));
        TcmLog.Info(api, "WOO directional felling hooked to FallingTree (rank-skewed fall direction)");

        // The impact leg is independently optional: if either seam moves in a future FallingTree
        // the direction axis above still stands on its own.
        var applyDamage = AccessTools.Method(ftPatch, "ApplyImpactDamage");
        var shredPath = AccessTools.Method(ftPatch, "ShredLeavesAndGlassAlongPath");
        if (applyDamage == null || shredPath == null)
        {
            TcmLog.Warn(api, "fallingtree present but ApplyImpactDamage/ShredLeavesAndGlassAlongPath not found; WOO fell damage inactive");
            return;
        }

        harmony.Patch(applyDamage,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(LandingDamagePatch), "Prefix")));
        harmony.Patch(shredPath,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SweepDamagePatch), "Prefix")));
        TcmLog.Info(api, "WOO fell damage hooked to FallingTree (flat hit along the swept path)");
    }

    /// <summary>Stashes the skewed fall direction from the struck face + feller rank, once per
    /// fell (all logs of the tree share it). Resets at the top; the postfix clears the feller so
    /// non-axe ConfigurePivotFaller calls (cascades) never inherit a stale skew.</summary>
    public static class FellStashPatch
    {
        public static void Prefix(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel)
        {
            skewValid = false;
            if (world.Side != EnumAppSide.Server || blockSel?.Face == null) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;

            Vec3i n = blockSel.Face.Normali;
            if (n.X == 0 && n.Z == 0) return; // up/down face — not a side chop; leave FallingTree's default

            (skewX, skewZ) = ComputeFall(player, blockSel.Position, n.X, n.Z, world.Rand);
            skewValid = true;
            TcmLog.Cat(world.Api, TcmLog.Hooks,
                $"WOO fell: face=({n.X},{n.Z}) WOO={WooDomain.LevelOf(player)} -> dir=({skewX:0.##},{skewZ:0.##})");
        }
        // No Postfix clear: the domino cascade (TryDomino) spawns the rest of the tree on LATER
        // ticks, so the skew must survive past this call. It's reset at the top of the next fell.
    }

    /// <summary>Redirects the WHOLE tree — every log, from both the initial SpawnFallers pass and
    /// the deferred TryDomino cascade (both route through here). FallingTree ignores its dirX/dirZ
    /// args (the topple is the pivot − treeCenter offset), so we recover the center from those args
    /// and re-aim the pivot, AND overwrite the per-entity drift attributes the physics reads.</summary>
    public static class PivotDirPatch
    {
        public static void Prefix(EntityBlockFalling faller, ref double pivotX, ref double pivotZ, float dirX, float dirZ)
        {
            if (!skewValid) return;
            double centerX = pivotX - dirX * 0.5;
            double centerZ = pivotZ - dirZ * 0.5;
            pivotX = centerX + skewX * 0.5;
            pivotZ = centerZ + skewZ * 0.5;

            // The per-log horizontal drift reads these; they were set to the default dir just before
            // this call, so overwrite them to match the rewritten pivot.
            faller.WatchedAttributes.SetFloat("fallingtree:dirX", skewX);
            faller.WatchedAttributes.SetFloat("fallingtree:dirZ", skewZ);
        }
    }

    // ==================================================== the impact leg (flat, swept)

    /// <summary>Horizontal/vertical reach from the log's centre line, in blocks. The log is 1 wide
    /// and a player is ~0.6 across, so ~0.9 is "the trunk touched you" without being generous.</summary>
    private const double HitRadius = 0.9;

    /// <summary>Victim entity id → world ms of its last connect. Server thread only. Bounded by
    /// <see cref="Prune"/> because mob ids are unique per spawn and would otherwise accumulate.</summary>
    private static readonly Dictionary<long, long> lastHitMs = new();

    /// <summary>Replaces FallingTree's landing damage outright. Theirs is 18 × |motionY| × mul
    /// against the final 1×1×1 cell, and a pivoted trunk arrives with motionY zeroed, so the
    /// multiplier scales a phantom number (verified live: /ftdamage 25 barely scratched). Ours is
    /// a flat hit, and the sweep patch below is what actually catches a moving trunk.</summary>
    public static class LandingDamagePatch
    {
        // __0/__1 by index, NOT by name: the target is static and its first parameter is literally
        // named "__instance", which Harmony would try to bind as an instance and throw on.
        public static bool Prefix(EntityBlockFalling __0, BlockPos __1)
        {
            if (__0?.Api?.World?.Side != EnumAppSide.Server || __1 == null) return false;
            if (__0.Block?.BlockMaterial != EnumBlockMaterial.Wood) return false;
            HitAlongSegment(__0, __1.X + 0.5, __1.Y + 0.5, __1.Z + 0.5, __1.X + 0.5, __1.Y + 0.5, __1.Z + 0.5);
            return false;
        }
    }

    /// <summary>The real fix for "a tree has never hit me": FallingTree already walks each log's
    /// per-tick path here to shred leaves, so the same last→current segment damages whatever the
    /// trunk swept through. Must be a PREFIX — the original overwrites lastX/Y/Z on its way out.</summary>
    public static class SweepDamagePatch
    {
        public static void Prefix(EntityBlockFalling e)
        {
            if (e?.Api?.World?.Side != EnumAppSide.Server) return;
            if (e.Block?.BlockMaterial != EnumBlockMaterial.Wood) return;

            var wa = e.WatchedAttributes;
            double lx = wa.GetDouble("fallingtree:lastX", e.ServerPos.X);
            double ly = wa.GetDouble("fallingtree:lastY", e.ServerPos.Y);
            double lz = wa.GetDouble("fallingtree:lastZ", e.ServerPos.Z);
            // The faller's pos is the block's horizontal centre already (ctor does Pos.X/Z += 0.5)
            // but its Y is the block's underside, so lift both ends to the log's mid-height.
            HitAlongSegment(e, lx, ly + 0.5, lz, e.ServerPos.X, e.ServerPos.Y + 0.5, e.ServerPos.Z);
        }
    }

    /// <summary>One broadphase query over the whole swept segment, then a cheap point-to-segment
    /// test per candidate. Keeps this to a single entity lookup per log per tick.</summary>
    private static void HitAlongSegment(EntityBlockFalling faller, double ax, double ay, double az,
        double bx, double by, double bz)
    {
        IWorldAccessor world = faller.World;
        if (world == null) return;
        if (WooDomain.Knob(WooDomain.FellImpactDamage, 8) <= 0) return;

        const double pad = HitRadius + 0.5;
        BlockPos min = new((int)Math.Floor(Math.Min(ax, bx) - pad), (int)Math.Floor(Math.Min(ay, by) - pad),
            (int)Math.Floor(Math.Min(az, bz) - pad));
        BlockPos max = new((int)Math.Ceiling(Math.Max(ax, bx) + pad), (int)Math.Ceiling(Math.Max(ay, by) + pad),
            (int)Math.Ceiling(Math.Max(az, bz) + pad));

        // EntityAgent only: players and creatures. Excludes EntityItem, so a felled tree can never
        // destroy the drops (or a death corpse's items) lying under it.
        Entity[] candidates = world.GetEntitiesInsideCuboid(min, max, e => e is EntityAgent && e.Alive);
        foreach (Entity victim in candidates)
        {
            double midY = victim.CollisionBox != null ? victim.CollisionBox.Y2 * 0.5 : 0.9;
            double d = DistToSegment(victim.ServerPos.X, victim.ServerPos.Y + midY, victim.ServerPos.Z,
                ax, ay, az, bx, by, bz);
            if (d <= HitRadius) TryHit(faller, victim);
        }
    }

    /// <summary>A flat, rank-independent crushing hit, rate-limited per victim so one tree lands
    /// one solid blow rather than one per log. SourceEntity is the log itself, which is what lets
    /// vanilla's lang-driven death messages resolve a deathmsg-blockfalling line.</summary>
    private static void TryHit(EntityBlockFalling faller, Entity victim)
    {
        long now = faller.World.ElapsedMilliseconds;
        long cooldown = (long)WooDomain.Knob(WooDomain.FellDamageCooldownMs, 600);
        if (lastHitMs.TryGetValue(victim.EntityId, out long prev) && now - prev < cooldown) return;
        lastHitMs[victim.EntityId] = now;
        if (lastHitMs.Count > 64) Prune(now);

        float dmg = (float)WooDomain.Knob(WooDomain.FellImpactDamage, 8);
        bool hurt = victim.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Block,
            Type = EnumDamageType.Crushing,
            SourceBlock = faller.Block,
            SourceEntity = faller,
            SourcePos = faller.ServerPos.XYZ,
        }, dmg);

        if (!hurt) return;
        if (faller.Block?.Sounds?.Break != null)
        {
            faller.World.PlaySoundAt(faller.Block.Sounds.Break, victim, null, 1f);
        }
        if (victim is EntityPlayer ep)
        {
            TcmLog.Cat(faller.Api, TcmLog.Hooks,
                $"WOO fell hit: {ep.Player?.PlayerName} took {dmg} crushing from {faller.Block?.Code}");
        }
    }

    private static void Prune(long now)
    {
        var stale = new List<long>();
        foreach (var kv in lastHitMs)
        {
            if (now - kv.Value > 10000) stale.Add(kv.Key);
        }
        foreach (long id in stale) lastHitMs.Remove(id);
    }

    /// <summary>Shortest distance from a point to the segment AB (not the infinite line).</summary>
    private static double DistToSegment(double px, double py, double pz,
        double ax, double ay, double az, double bx, double by, double bz)
    {
        double abx = bx - ax, aby = by - ay, abz = bz - az;
        double lenSq = abx * abx + aby * aby + abz * abz;
        double t = lenSq < 1e-9 ? 0 : ((px - ax) * abx + (py - ay) * aby + (pz - az) * abz) / lenSq;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        double dx = px - (ax + abx * t), dy = py - (ay + aby * t), dz = pz - (az + abz * t);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The cone math: base = struck-face normal; center rotated toward the player at low
    /// rank / away at high rank; half-width shrinks with rank; a uniform draw inside the cone.</summary>
    private static (float x, float z) ComputeFall(IPlayer player, BlockPos treePos, int faceX, int faceZ, Random rand)
    {
        double baseAngle = Math.Atan2(faceZ, faceX);

        // Which rotational direction is "toward the player" from the face normal.
        double px = player.Entity.ServerPos.X - (treePos.X + 0.5);
        double pz = player.Entity.ServerPos.Z - (treePos.Z + 0.5);
        double toPlayer = NormalizeAngle(Math.Atan2(pz, px) - baseAngle);
        double towardSign = toPlayer >= 0 ? 1.0 : -1.0;

        int level = WooDomain.LevelOf(player);
        double spread = Lerp(WooDomain.Knob(WooDomain.FellSpreadUntrained, 85),
            WooDomain.Knob(WooDomain.FellSpreadGm, 6), WooDomain.RankProgress(level)) * Deg2Rad;
        double bias = BiasDegrees(level) * Deg2Rad * towardSign;
        double theta = (rand.NextDouble() * 2 - 1) * spread;

        double a = baseAngle + bias + theta;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    /// <summary>Cone-centre bias (RULED 2026-07-15): leans TOWARD the feller the whole way up
    /// through Journeyman, reaches exactly zero at Master I, then rotates AWAY to the GM value.
    /// A plain untrained→GM lerp would cross zero back at Journeyman II, so this is piecewise.</summary>
    private static double BiasDegrees(int level)
    {
        double untrained = WooDomain.Knob(WooDomain.FellBiasUntrained, 35);
        double gm = WooDomain.Knob(WooDomain.FellBiasGm, -22);
        int masterEntry = 3 * Leveling.Domain.SubLevelsPerTier + 1; // Master I — the zero crossing
        int max = Leveling.Domain.MaxLevelDefault;

        if (level <= 0) return untrained;
        if (level >= max) return gm;
        // Untrained → Master I: toward the feller, decaying to zero.
        if (level <= masterEntry) return untrained * (1.0 - level / (double)masterEntry);
        // Master I → GM: rotate away from the feller.
        return gm * ((level - masterEntry) / (double)(max - masterEntry));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
