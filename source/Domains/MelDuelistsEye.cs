using System;
using System.Collections.Generic;
using System.Reflection;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// MEL Phase 4 — THE DUELIST'S EYE (rank-bonus-design §MEL Axis 6, AMENDED 2026-07-20 after the
/// CO-combat research: the text reads CO already gives on hover are dropped; the identity is a
/// spatial VITAL-POINT overlay, the melee twin of the Marksman's Eye). dependsOn combatoverhaulfork.
///
///   • Apprentice I (level 5): a crouch-and-look condition read — the foe's health band. CO's own
///     ShowStatsBehavior already prints attack tier and vulnerability, so this adds only what it
///     does not: the live condition (healthy / wounded / near death).
///   • Master I (level 13): the vital-point overlay, LEARNED OVER TIME. As you fight a creature its
///     tells reveal (a per-encounter, per-individual read meter fills with engagement; rank shortens
///     the fill). Once learned, markers paint its zones on its body: a bright mark on the vital spot
///     (Critical 2x, else the head), a dim mark on Resistant zones (0.2x, wasted hits). You still
///     land the strike yourself under CO's collider hit resolution — it closes the knowledge gap,
///     not the execution gap. Confirmed rich on the rust bestiary: the bowtorn's Critical neck, the
///     bell's resistant shell vs its vertebrae.
///
/// Reads CO's client-side data by reflection (soft dep): the mob's CollidersEntityBehavior.Colliders
/// (name -> ShapeElementCollider.InworldVertices, the 8 world-space box corners) and CollidersTypes
/// (name -> ColliderTypes zone). Rendered as an always-open HudElement (the vignette lesson: the
/// engine's Render2DTexture only composites inside the GUI pass).
/// </summary>
public class MelDuelistsEye : HudElement
{
    private readonly ICoreClientAPI capi;
    private LoadedTexture? strikeMark;  // the vital point: strike here
    private LoadedTexture? avoidMark;   // resistant: wasted hits

    // Learn-over-time, driven by COMBAT CONTACT (server pings via MelEngagedPacket when you hit a
    // mob or it hits you). A foe you actually fight reveals; one you glance at through terrain does
    // not. Learn accrues only while contact is recent; the overlay draws while it stays recent.
    private sealed class Engage { public float Learn; public long LastContactMs; }
    private readonly Dictionary<long, Engage> engaged = new();
    private const long ContactActiveMs = 8000;  // still "in combat" this long after the last hit
    private const long ContactPruneMs = 15000;  // forget the encounter after this quiet

    // The condition card (crouch-look): its own tiny panel, rebuilt on text change.
    private string lastCard = "";

    // CO reflection (resolved lazily; degrades to inert if the shape changes).
    private Type? collidersBehType;
    private PropertyInfo? collidersProp;      // .Colliders : Dictionary<string, ShapeElementCollider>
    private PropertyInfo? colliderTypesProp;  // .CollidersTypes : Dictionary<string, ColliderTypes>
    private PropertyInfo? inworldVertsProp;   // ShapeElementCollider.InworldVertices : Vector4d[8]
    private bool coLookupFailed;

    public MelDuelistsEye(ICoreClientAPI capi) : base(capi)
    {
        this.capi = capi;
        BuildMarks();
        ComposeCard("");
        capi.Network.RegisterChannel("almanactcmmel").RegisterMessageType<MelEngagedPacket>()
            .SetMessageHandler<MelEngagedPacket>(OnEngaged);
        capi.Gui.RegisterDialog(this);
        capi.Event.RegisterGameTickListener(_ => { if (!IsOpened()) TryOpen(); }, 500);
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.046;

    // ------------------------------------------------------------ textures

    private void BuildMarks()
    {
        strikeMark = MakeStar(1.0, 0.85, 0.30, 0.98); // gold 4-point star: strike here
        avoidMark = MakeRing(0.55, 0.55, 0.6, 0.5);   // dim slate ring: resistant, avoid
    }

    /// <summary>A 4-pointed star (the Illuminated affinity-callout shape), to read distinctly
    /// from the bow's ring: gold fill with a dark outline for legibility on bright sky.</summary>
    private LoadedTexture MakeStar(double r, double g, double b, double a)
    {
        const int size = 40;
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        double c = size / 2.0, outer = 17, inner = 5.0;
        void StarPath()
        {
            for (int i = 0; i < 8; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 4;   // tips up/right/down/left
                double rad = (i % 2 == 0) ? outer : inner;
                double px = c + rad * Math.Cos(ang), py = c + rad * Math.Sin(ang);
                if (i == 0) ctx.MoveTo(px, py); else ctx.LineTo(px, py);
            }
            ctx.ClosePath();
        }
        StarPath();
        ctx.SetSourceRGBA(0, 0, 0, 0.6);
        ctx.LineWidth = 3.0;
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.Fill();
        surface.Flush();
        ctx.Dispose();
        var tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        surface.Dispose();
        return tex;
    }

    private LoadedTexture MakeRing(double r, double g, double b, double a)
    {
        const int size = 34;
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        double c = size / 2.0;
        ctx.SetSourceRGBA(0, 0, 0, 0.5);
        ctx.LineWidth = 4.0;
        ctx.Arc(c, c, 10, 0, Math.PI * 2);
        ctx.Stroke();
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.LineWidth = 2.0;
        ctx.Arc(c, c, 10, 0, Math.PI * 2);
        ctx.Stroke();
        ctx.Arc(c, c, 1.3, 0, Math.PI * 2);
        ctx.Fill();
        surface.Flush();
        ctx.Dispose();
        var tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        surface.Dispose();
        return tex;
    }

    // ------------------------------------------------------------ render

    public override void OnRenderGUI(float dt)
    {
        base.OnRenderGUI(dt);
        if (!capi.Input.MouseGrabbed) { SetCard(""); return; }

        var plr = capi.World?.Player?.Entity;
        if (plr == null) { SetCard(""); return; }

        int level = MelDomain.ClientLevel();
        if (level < 5) { SetCard(""); PruneEngaged(); return; } // pre-Apprentice: nothing learned

        var eye = plr.Pos.XYZ.Add(plr.LocalEyePos.X, plr.LocalEyePos.Y, plr.LocalEyePos.Z);
        var look = plr.Pos.GetViewVector().Normalize();
        var lookD = new Vec3d(look.X, look.Y, look.Z);

        // The condition card: crouch + look at a hostile is the deliberate size-up gesture.
        Entity? aimed = plr.Controls.Sneak ? PickHostile(plr, eye, lookD) : null;
        SetCard(aimed != null ? ConditionLine(aimed) : "");

        // The vital overlay: Master+, learned over time by FIGHTING the creature (contact-driven).
        if (level >= 13) UpdateAndDrawOverlay(dt, level);
        PruneEngaged();
    }

    private void OnEngaged(MelEngagedPacket p)
    {
        if (!engaged.TryGetValue(p.MobId, out var e)) engaged[p.MobId] = e = new Engage();
        e.LastContactMs = capi.World.ElapsedMilliseconds;
    }

    private void UpdateAndDrawOverlay(float dt, int level)
    {
        long now = capi.World.ElapsedMilliseconds;
        float reveal = RevealSeconds(level);
        foreach (var kv in engaged)
        {
            var e = kv.Value;
            bool active = now - e.LastContactMs <= ContactActiveMs;
            if (active) e.Learn += dt;              // learn only while actively fighting it
            if (!active || e.Learn < reveal) continue;
            var mob = capi.World.GetEntityById(kv.Key);
            if (mob == null || !mob.Alive) continue;
            DrawVitalMarks(mob, dt);
        }
    }

    /// <summary>Paints a strike star on the mob's vital collider (Critical, else the head) and a
    /// dim ring on each Resistant collider, at their world-space centres (position-smoothed).</summary>
    private void DrawVitalMarks(Entity mob, float dt)
    {
        if (!ResolveCo()) return;
        object? beh = FindBehavior(mob, collidersBehType);
        if (beh == null) return;
        if (collidersProp!.GetValue(beh) is not System.Collections.IDictionary colliders) return;
        if (colliderTypesProp!.GetValue(beh) is not System.Collections.IDictionary types) return;

        // Best strike collider (Critical > Head) among names that ACTUALLY have a collider object
        // (types has more zone entries than there are colliders), and mark Resistants likewise.
        string? bestStrike = null; int bestRank = -1;
        foreach (System.Collections.DictionaryEntry e in types)
        {
            string name = (string)e.Key;
            if (!colliders.Contains(name)) continue; // no collider object -> nothing to place
            string zone = e.Value?.ToString() ?? "";
            if (zone == "Resistant")
            {
                if (TryCenter(colliders, name, out Vec3d rc)) DrawMark(avoidMark!, rc, mob.EntityId, name, dt);
            }
            else
            {
                int rank = zone == "Critical" ? 2 : zone == "Head" ? 1 : -1;
                if (rank > bestRank) { bestRank = rank; bestStrike = name; }
            }
        }
        if (bestStrike != null && TryCenter(colliders, bestStrike, out Vec3d sc))
            DrawMark(strikeMark!, sc, mob.EntityId, bestStrike, dt);
    }

    private bool TryCenter(System.Collections.IDictionary colliders, string name, out Vec3d center)
    {
        center = new Vec3d();
        if (!colliders.Contains(name)) return false;
        object col = colliders[name]!;
        if (inworldVertsProp!.GetValue(col) is not Array verts || verts.Length == 0) return false;

        double x = 0, y = 0, z = 0; int n = 0;
        foreach (var v in verts)
        {
            if (v == null) continue;
            var t = v.GetType();
            // Vector4d exposes X/Y/Z as fields or properties depending on the lib; read either.
            double vx = ReadD(v, t, "X"), vy = ReadD(v, t, "Y"), vz = ReadD(v, t, "Z");
            x += vx; y += vy; z += vz; n++;
        }
        if (n == 0) return false;
        center.Set(x / n, y / n, z / n);
        // Guard against un-transformed (all-zero) colliders.
        return !(center.X == 0 && center.Y == 0 && center.Z == 0);
    }

    private static readonly Dictionary<(Type, string), MemberInfo?> memberCache = new();
    private static double ReadD(object obj, Type t, string name)
    {
        var key = (t, name);
        if (!memberCache.TryGetValue(key, out var m))
        {
            m = (MemberInfo?)t.GetField(name) ?? t.GetProperty(name);
            memberCache[key] = m;
        }
        object? val = m switch
        {
            FieldInfo f => f.GetValue(obj),
            PropertyInfo p => p.GetValue(obj),
            _ => null
        };
        return val == null ? 0 : Convert.ToDouble(val);
    }

    // Position smoothing: the collider centre bobs with the mob's animation, so the mark is
    // eased toward it (~90ms time constant) to take the jitter off without visibly trailing.
    // Keyed per mob+zone; large jumps (teleport / chunk pop) snap instead of lerp.
    private readonly Dictionary<string, Vec3d> smooth = new();

    private void DrawMark(LoadedTexture tex, Vec3d worldCenter, long mobId, string zone, float dt)
    {
        string key = mobId + ":" + zone;
        if (!smooth.TryGetValue(key, out var sp) || worldCenter.DistanceTo(sp) > 3.0)
        {
            sp = worldCenter.Clone();
        }
        else
        {
            double f = Math.Min(1.0, dt * 11.0);
            sp = new Vec3d(sp.X + (worldCenter.X - sp.X) * f,
                           sp.Y + (worldCenter.Y - sp.Y) * f,
                           sp.Z + (worldCenter.Z - sp.Z) * f);
        }
        smooth[key] = sp;

        var screen = MatrixToolsd.Project(sp,
            capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat,
            capi.Render.FrameWidth, capi.Render.FrameHeight);
        if (screen.Z <= 0) return; // behind camera
        float x = (float)screen.X - tex.Width / 2f;
        float y = capi.Render.FrameHeight - (float)screen.Y - tex.Height / 2f;
        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 55f);
    }

    // ------------------------------------------------------------ target + condition

    /// <summary>The aimed hostile: tightest cone among living non-player agents that carry the
    /// harvestable behaviour (game) OR are rustboundmagic (the combat quarry set), out to 40m.</summary>
    private Entity? PickHostile(Entity plr, Vec3d eye, Vec3d look)
    {
        Entity? best = null; double bestDist = 40;
        foreach (var e in capi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive || e == plr || e is not EntityAgent) continue;
            if (!IsQuarry(e)) continue;
            var center = e.Pos.XYZ.Add(0, (e.SelectionBox?.Y2 ?? 0.6f) * 0.6, 0);
            var to = center.Sub(eye);
            double dist = to.Length();
            if (dist < 1.0 || dist >= bestDist) continue;
            // A generous cone: at melee range you are close to a big target, so small aim
            // offsets are large angles. ~35 deg keeps the foe you are fighting picked.
            var dir = to.Normalize();
            if (dir.X * look.X + dir.Y * look.Y + dir.Z * look.Z >= 0.82) { bestDist = dist; best = e; }
        }
        return best;
    }

    private static bool IsQuarry(Entity e) =>
        e.GetBehavior<Vintagestory.GameContent.EntityBehaviorHarvestable>() != null
        || e.Code?.Domain == "rustboundmagic";

    private string ConditionLine(Entity e)
    {
        var tree = e.WatchedAttributes?.GetTreeAttribute("health");
        float max = tree?.GetFloat("maxhealth", 0) ?? 0;
        float cur = tree?.GetFloat("currenthealth", 0) ?? 0;
        string name = Lang.GetIfExists($"item-creature-{e.Code?.Path}") ?? e.Code?.FirstCodePart() ?? "quarry";
        if (max <= 0) return name;
        float pct = GameMath.Clamp(cur / max, 0, 1);
        string cond = pct > 0.66f ? Lang.Get("almanactcm:duel-healthy")
            : pct > 0.25f ? Lang.Get("almanactcm:duel-wounded")
            : Lang.Get("almanactcm:duel-neardeath");
        return $"{name}. {cond}";
    }

    /// <summary>Reveal time by rank: Master I ~5s of engagement, GM ~2s. Rank sharpens the read.</summary>
    private static float RevealSeconds(int level)
    {
        int max = Leveling.Domain.MaxLevelDefault; // 17
        double t = GameMath.Clamp((level - 13) / (double)(max - 13), 0, 1);
        return (float)(5.0 - t * 3.0);
    }

    private void PruneEngaged()
    {
        if (engaged.Count == 0) return;
        long now = capi.World.ElapsedMilliseconds;
        List<long>? gone = null;
        foreach (var kv in engaged)
        {
            var mob = capi.World.GetEntityById(kv.Key);
            if (mob == null || !mob.Alive || now - kv.Value.LastContactMs > ContactPruneMs)
                (gone ??= new()).Add(kv.Key);
        }
        if (gone != null)
            foreach (var id in gone)
            {
                engaged.Remove(id);
                string prefix = id + ":";
                List<string>? keys = null;
                foreach (var k in smooth.Keys) if (k.StartsWith(prefix)) (keys ??= new()).Add(k);
                if (keys != null) foreach (var k in keys) smooth.Remove(k);
            }
    }

    // ------------------------------------------------------------ card panel

    private void SetCard(string text)
    {
        if (text == lastCard) return;
        lastCard = text;
        ComposeCard(text);
        if (text == "") { if (IsOpened()) { /* keep open for the overlay */ } return; }
    }

    private void ComposeCard(string text)
    {
        if (text == "")
        {
            var stub = ElementBounds.Fixed(0, 0, 1, 1);
            SingleComposer = capi.Gui.CreateCompo("melduelistseye", stub.ForkBoundingParent().WithAlignment(EnumDialogArea.LeftTop)).Compose();
            return;
        }
        var font = CairoFont.WhiteSmallText();
        double w = Math.Min(360, font.GetTextExtents(text).Width + 20);
        var textBounds = ElementBounds.Fixed(0, 0, w, 26);
        var bg = textBounds.ForkBoundingParent(8, 6, 8, 6).WithAlignment(EnumDialogArea.CenterMiddle)
            .WithFixedAlignmentOffset(0, capi.Render.FrameHeight * 0.16 / RuntimeEnv.GUIScale);
        SingleComposer = capi.Gui.CreateCompo("melduelistseye", bg)
            .AddGameOverlay(textBounds.ForkBoundingParent(8, 6, 8, 6), GuiStyle.DialogLightBgColor)
            .AddStaticText(text, font, textBounds)
            .Compose();
    }

    // ------------------------------------------------------------ CO interop

    private bool ResolveCo()
    {
        if (coLookupFailed) return false;
        if (collidersProp != null) return true;
        try
        {
            collidersBehType = HarmonyLib.AccessTools.TypeByName("CombatOverhaul.Colliders.CollidersEntityBehavior");
            var scType = HarmonyLib.AccessTools.TypeByName("CombatOverhaul.Colliders.ShapeElementCollider");
            collidersProp = collidersBehType?.GetProperty("Colliders");
            colliderTypesProp = collidersBehType?.GetProperty("CollidersTypes");
            inworldVertsProp = scType?.GetProperty("InworldVertices");
            if (collidersProp == null || colliderTypesProp == null || inworldVertsProp == null)
            {
                coLookupFailed = true;
                return false;
            }
            return true;
        }
        catch (Exception) { coLookupFailed = true; return false; }
    }

    private object? FindBehavior(Entity entity, Type? t)
    {
        if (t == null) return null;
        foreach (var b in entity.SidedProperties?.Behaviors ?? new List<EntityBehavior>())
            if (t.IsInstanceOfType(b)) return b;
        return null;
    }

    public override void Dispose()
    {
        strikeMark?.Dispose();
        avoidMark?.Dispose();
        base.Dispose();
    }
}
