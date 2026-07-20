using System;
using System.Collections.Generic;
using System.Reflection;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// RAN Phase 4 — THE MARKSMAN'S EYE (rank-bonus-design §RAN Axis 6, AMENDED 2026-07-20: the
/// read ships as a rank-earned LEAD MARKER, not text; range text stays HUN's Tracker's Eye).
///
/// While aiming a CO ranged weapon at a target, a small ring is drawn at the screen point the
/// shooter must bring their (still-drifting) aim to for the projectile to intercept:
///   • Journeyman I (level 9): the drop-corrected hold on the target's CURRENT position.
///   • Master I (level 13): full lead — target trajectory extrapolated over flight time.
///   • Below Journeyman: nothing. The marker is the earned capability.
///
/// The boundary, honored: the marker is computed from the held weapon's real ballistics and
/// the target's current velocity only (a jinking deer invalidates it, like any lead indicator);
/// it never moves the aim, and CO's steadyAim sway is untouched — the player still aligns a
/// drifting reticle with the marker and times the loose.
///
/// Ballistics replicate the engine exactly (EntityBehaviorPassivePhysics.MotionAndCollision,
/// vsapi:110574): per step, motion *= AirDragAlways^(dt*33); motion.y -= GravityPerSecond*dt;
/// pos += motion*60*dt — with GravityPerSecond = 0.37 and AirDragAlways = 0.983 (CO arrow
/// entities ship gravityFactor/airDragFactor 1). Muzzle speed comes off the held item's
/// resolved attributes (bows "ArrowVelocity", firearms "BulletVelocity" — ByType keys resolve
/// at asset load) times the stack's projectileSpeed, in motion units (blocks per 1/60 s).
///
/// Rendered as an always-open HudElement (the vignette lesson: Render2DTexture only composites
/// inside the GUI pass). Aim state is read from CO's ClientAimingSystem via cached reflection.
/// </summary>
public class RanMarksmansEye : HudElement
{
    private LoadedTexture? ring;

    // CO aim-state reflection (soft dep — resolved lazily, never throws).
    private object? coSystem;
    private PropertyInfo? aimingSystemProp;
    private PropertyInfo? aimingProp;
    private bool coLookupFailed;

    // Target velocity sampling: entityId -> (pos, clientMs). One entry per aimed target.
    private long sampleEntityId = -1;
    private Vec3d samplePos = new();
    private long sampleMs;
    private Vec3d targetVel = new(); // blocks per second, smoothed

    public RanMarksmansEye(ICoreClientAPI capi) : base(capi)
    {
        BuildRing();
        ComposeStub();
        capi.Gui.RegisterDialog(this);
        capi.Event.RegisterGameTickListener(_ => { if (!IsOpened()) TryOpen(); }, 500);
    }

    private void ComposeStub()
    {
        var panel = ElementBounds.Fixed(0, 0, 1, 1);
        var dialogBounds = panel.ForkBoundingParent().WithAlignment(EnumDialogArea.LeftTop);
        SingleComposer = capi.Gui.CreateCompo("ranmarksmanseye", dialogBounds).Compose();
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.045; // under the tracker panel, over the vignette

    private void BuildRing()
    {
        const int size = 40;
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        double c = size / 2.0;
        // A thin gold ring with a faint dark outline for readability on bright sky.
        ctx.SetSourceRGBA(0, 0, 0, 0.55);
        ctx.LineWidth = 4.5;
        ctx.Arc(c, c, 13, 0, Math.PI * 2);
        ctx.Stroke();
        ctx.SetSourceRGBA(1.0, 0.84, 0.35, 0.95);
        ctx.LineWidth = 2.2;
        ctx.Arc(c, c, 13, 0, Math.PI * 2);
        ctx.Stroke();
        ctx.Arc(c, c, 1.4, 0, Math.PI * 2);
        ctx.Fill();
        surface.Flush();
        ctx.Dispose();

        ring = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref ring);
        surface.Dispose();
    }

    public override void OnRenderGUI(float dt)
    {
        base.OnRenderGUI(dt);
        if (ring == null || !capi.Input.MouseGrabbed) return;

        int level = RanDomain.ClientLevel();
        if (level < 9) return; // the Eye opens at Journeyman I

        var plr = capi.World?.Player?.Entity;
        if (plr == null || !IsAiming()) return;

        double muzzle = MuzzleSpeed(); // motion units (blocks per 1/60 s)
        if (muzzle <= 0.1) return;

        var eye = plr.Pos.XYZ.Add(plr.LocalEyePos.X, plr.LocalEyePos.Y, plr.LocalEyePos.Z);
        var look = plr.Pos.GetViewVector().Normalize();
        var lookD = new Vec3d(look.X, look.Y, look.Z);

        Entity? target = PickTarget(plr, eye, lookD);
        if (target == null) { sampleEntityId = -1; return; }

        var center = target.Pos.XYZ.Add(0, (target.SelectionBox?.Y2 ?? 0.8f) * 0.55, 0);
        UpdateVelocity(target, center);

        // Journeyman band: drop only — the hold is computed on the standing target.
        Vec3d vel = level >= 13 ? targetVel : new Vec3d();

        if (!SolveAim(eye, center, vel, muzzle, out Vec3d aimDir, out double aimDist)) return;

        // The marker sits on the ray of the REQUIRED launch direction: bring the aim there.
        var aimPoint = eye.AddCopy(aimDir.X * aimDist, aimDir.Y * aimDist, aimDir.Z * aimDist);
        var screen = MatrixToolsd.Project(aimPoint,
            capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat,
            capi.Render.FrameWidth, capi.Render.FrameHeight);
        if (screen.Z <= 0) return; // behind the camera

        float x = (float)screen.X - ring.Width / 2f;
        float y = capi.Render.FrameHeight - (float)screen.Y - ring.Height / 2f;
        capi.Render.Render2DTexturePremultipliedAlpha(ring.TextureId, x, y,
            ring.Width, ring.Height, 60f, new Vec4f(1, 1, 1, 0.9f));
    }

    // ------------------------------------------------------------ target + velocity

    /// <summary>Aimed-entity pick, the Tracker's Eye pattern: tightest cone winner among
    /// living creatures near the look ray, out to 60 blocks (a marksman's problem is long).</summary>
    private Entity? PickTarget(Entity plr, Vec3d eye, Vec3d look)
    {
        Entity? aimed = null;
        double best = 60;
        foreach (var e in capi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive || e == plr || e is not EntityAgent) continue;
            if (!IsMarkTarget(e)) continue;
            var center = e.Pos.XYZ.Add(0, (e.SelectionBox?.Y2 ?? 0.5f) * 0.5, 0);
            var to = center.Sub(eye);
            double dist = to.Length();
            if (dist < 1.5 || dist >= best) continue;
            var dir = to.Normalize();
            double dot = dir.X * look.X + dir.Y * look.Y + dir.Z * look.Z;
            // A wider cone than the tracker's read (~11 deg): lead means the target sits
            // OFF the crosshair by design while the shooter tracks ahead of it.
            if (dot >= 0.981) { best = dist; aimed = e; }
        }
        return aimed;
    }

    /// <summary>Quarry worth a marksman's lead (ruled 2026-07-20: animals and rust monsters,
    /// never butterflies): anything harvestable — which covers game AND the temporal hostiles
    /// (drifters/bells carry the behavior; critters do not) — plus rustboundmagic's creatures
    /// by domain in case theirs skip it.</summary>
    private static bool IsMarkTarget(Entity e)
    {
        if (e.GetBehavior<Vintagestory.GameContent.EntityBehaviorHarvestable>() != null) return true;
        return e.Code?.Domain == "rustboundmagic";
    }

    /// <summary>Velocity from position sampling (client Motion on server-driven entities is
    /// unreliable between updates). Smoothed lightly so the marker does not jitter.</summary>
    private void UpdateVelocity(Entity target, Vec3d center)
    {
        long now = capi.World.ElapsedMilliseconds;
        if (sampleEntityId != target.EntityId)
        {
            sampleEntityId = target.EntityId;
            samplePos = center.Clone();
            sampleMs = now;
            targetVel.Set(0, 0, 0);
            return;
        }
        long dtMs = now - sampleMs;
        if (dtMs < 120) return; // sample window
        double s = 1000.0 / dtMs;
        var diff = center.SubCopy(samplePos);
        double vx = diff.X * s, vy = diff.Y * s, vz = diff.Z * s;
        // Discard teleports (chunk pops, respawn syncs) and smooth the rest.
        if (Math.Sqrt(vx * vx + vy * vy + vz * vz) < 30)
        {
            targetVel.X += (vx - targetVel.X) * 0.4;
            targetVel.Y += (vy - targetVel.Y) * 0.4;
            targetVel.Z += (vz - targetVel.Z) * 0.4;
        }
        samplePos = center.Clone();
        sampleMs = now;
    }

    // ------------------------------------------------------------ the ballistic solve

    /// <summary>Finds the launch direction whose simulated arc meets the moving target:
    /// an outer intercept loop (predict target at flight time, re-solve) around an inner
    /// pitch search against the engine-exact arc. Cheap: a few hundred small steps.</summary>
    private static bool SolveAim(Vec3d eye, Vec3d targetPos, Vec3d targetVel, double muzzle,
        out Vec3d aimDir, out double aimDist)
    {
        aimDir = new Vec3d(); aimDist = 0;
        double speedBps = muzzle * 60.0; // blocks per second, at the muzzle
        double t = targetPos.DistanceTo(eye) / Math.Max(1, speedBps);

        for (int outer = 0; outer < 3; outer++)
        {
            var p = targetPos.AddCopy(targetVel.X * t, targetVel.Y * t, targetVel.Z * t);
            double dx = p.X - eye.X, dz = p.Z - eye.Z, dy = p.Y - eye.Y;
            double r = Math.Sqrt(dx * dx + dz * dz);
            if (r < 0.5) return false;

            double lo = Math.Atan2(dy, r) - 0.05, hi = lo + 0.9, flight = t;
            bool hit = false;
            for (int i = 0; i < 14; i++)
            {
                double pitch = (lo + hi) / 2;
                double simY = SimulateArc(muzzle, pitch, r, out flight);
                if (double.IsNaN(simY)) { lo = pitch; continue; } // fell short: more loft
                if (simY > dy) hi = pitch; else lo = pitch;       // flat-regime monotonic
                hit = true;
            }
            if (!hit) return false;

            double finalPitch = (lo + hi) / 2;
            t = flight;
            double cx = dx / r, cz = dz / r;
            double cp = Math.Cos(finalPitch), sp = Math.Sin(finalPitch);
            aimDir.Set(cx * cp, sp, cz * cp);
            aimDist = Math.Sqrt(r * r + dy * dy);
        }
        return aimDist > 0;
    }

    /// <summary>The engine-exact arc (PassivePhysics integration, vsapi:110574): returns the
    /// projectile height (relative to launch) when it crosses horizontal range r, and the
    /// flight time to get there. NaN if the shot cannot reach r (drag bleeds it dry).</summary>
    private static double SimulateArc(double muzzle, double pitch, double r, out double flight)
    {
        const double dt = 1.0 / 30.0;
        const double drag = 0.983;     // GlobalConstants.AirDragAlways
        const double gravity = 0.37;   // GlobalConstants.GravityPerSecond
        double dragStep = Math.Pow(drag, dt * 33.0);

        double mh = muzzle * Math.Cos(pitch); // horizontal motion (blocks per 1/60 s)
        double mv = muzzle * Math.Sin(pitch); // vertical
        double x = 0, y = 0;
        flight = 0;

        for (int i = 0; i < 240; i++) // 8 seconds of flight, far past any real shot
        {
            mh *= dragStep;
            mv = mv * dragStep - gravity * dt;
            double step = mh * 60.0 * dt;
            if (x + step >= r)
            {
                // Interpolate the crossing inside this step for a stable read.
                double f = (r - x) / step;
                flight += dt * f;
                return y + mv * 60.0 * dt * f;
            }
            x += step;
            y += mv * 60.0 * dt;
            flight += dt;
            if (mh * 60.0 < 0.5) break; // drag has bled the shot dry
        }
        return double.NaN;
    }

    // ------------------------------------------------------------ CO interop

    /// <summary>True while CO's client aiming system reports the player aiming a ranged
    /// weapon. Reflection-bound once; degrades to never-true if CO's shape changes.</summary>
    private bool IsAiming()
    {
        if (coLookupFailed) return false;
        try
        {
            if (aimingProp == null)
            {
                coSystem = capi.ModLoader.GetModSystem("CombatOverhaul.CombatOverhaulSystem");
                aimingSystemProp = coSystem?.GetType().GetProperty("AimingSystem");
                var aimingSystem = aimingSystemProp?.GetValue(coSystem);
                aimingProp = aimingSystem?.GetType().GetProperty("Aiming");
                if (aimingProp == null) { coLookupFailed = true; return false; }
            }
            var sys = aimingSystemProp!.GetValue(coSystem);
            return sys != null && (bool)aimingProp.GetValue(sys)!;
        }
        catch (Exception)
        {
            coLookupFailed = true;
            return false;
        }
    }

    /// <summary>Muzzle speed of the held launcher in motion units: bows carry ArrowVelocity,
    /// firearms BulletVelocity (ByType-resolved at asset load), scaled by the per-stack
    /// projectileSpeed attribute (the ItemStackRangedStats seam).</summary>
    private double MuzzleSpeed()
    {
        var stack = capi.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        var attrs = stack?.Collectible?.Attributes;
        if (attrs == null) return 0;
        double v = attrs["ArrowVelocity"].AsDouble(0);
        if (v <= 0) v = attrs["BulletVelocity"].AsDouble(0);
        if (v <= 0) return 0;
        return v * stack!.Attributes.GetFloat("projectileSpeed", 1f);
    }

    public override void Dispose()
    {
        ring?.Dispose();
        ring = null;
        base.Dispose();
    }
}
