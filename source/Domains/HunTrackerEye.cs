using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 2 — THE TRACKER'S EYE (rank-bonus-design §HUN Axis 3, ruled; interaction ruled
/// 2026-07-17: SNEAK + LOOK). While crouched and centred on a live animal within a rank-scaled
/// range, the hunter reads real, already-networked state — nothing hidden ever crosses to the
/// client (T1.0). The read is a pure BONUS band above vanilla's free 4.5-block hover name; it
/// never degrades the baseline, and below Apprentice it simply does not appear.
///
/// Ladder (ruled):
///   Apprentice: species + size, short range.
///   Journeyman: + condition (health) and agitation (stress), range grows.
///   Master+:    + wound state (BloodTrail isBleeding) and heading, full range.
///
/// Entirely client-side: it reads the entity's networked WatchedAttributes and the local
/// player's synced HUN level, and paints a small HUD readout under the crosshair. No server
/// round-trip, no new packet.
/// </summary>
public class HunTrackerEye : HudElement
{
    private readonly StringBuilder sb = new();
    private string lastText = "";

    public HunTrackerEye(ICoreClientAPI capi) : base(capi)
    {
        Compose();
        capi.Event.RegisterGameTickListener(OnTick, 150);
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.05;

    private void Compose()
    {
        var font = CairoFont.WhiteSmallText().WithStroke(new double[] { 0, 0, 0, 1 }, 1.5);
        var text = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 120, 340, 108);
        SingleComposer = capi.Gui.CreateCompo("huntrackereye", text)
            .AddDynamicText("", font, text, "read")
            .Compose();
        TryOpen();
    }

    private void OnTick(float dt)
    {
        string text = BuildRead();
        if (text == lastText) return;
        lastText = text;
        SingleComposer?.GetDynamicText("read")?.SetNewText(text);
    }

    private string BuildRead()
    {
        var plr = capi.World?.Player?.Entity;
        if (plr == null || !plr.Controls.Sneak) return "";

        int level = HunDomain.ClientLevel();
        if (level < 5) return ""; // pre-Apprentice: the read has not been learned

        int tier = (level - 1) / 4; // 1 App .. 4 GM
        float range = tier switch { 1 => 12f, 2 => 20f, 3 => 30f, _ => 42f };

        Entity? target = PickLookedAt(plr, range);
        if (target == null) return "";

        sb.Clear();
        // Species + size (Apprentice+). Size from the entity's own selection-box height band.
        string name = target.GetName();
        string size = SizeWord(target);
        sb.Append(Lang.Get("almanactcm:track-name", name, size));

        // Condition + agitation (Journeyman+).
        if (tier >= 2)
        {
            float hpPct = HealthPct(target);
            if (hpPct >= 0)
            {
                string cond = hpPct > 0.85f ? "track-cond-strong"
                    : hpPct > 0.45f ? "track-cond-hurt" : "track-cond-failing";
                sb.Append('\n').Append(Lang.Get("almanactcm:" + cond));
            }
            if (target.WatchedAttributes?.GetFloat("stressLevel", 0) > 0.1f)
                sb.Append(' ').Append(Lang.Get("almanactcm:track-agitated"));
        }

        // Wounds + heading (Master+).
        if (tier >= 4 || (tier >= 3 && level >= 15))
        {
            if (target.WatchedAttributes?.GetBool("isBleeding") == true)
                sb.Append('\n').Append(Lang.Get("almanactcm:track-bleeding"));
            string? heading = Heading(target);
            if (heading != null)
                sb.Append('\n').Append(Lang.Get("almanactcm:track-heading", heading));
        }

        return sb.ToString();
    }

    /// <summary>Nearest live huntable animal whose bearing is within a narrow cone of where the
    /// player is looking. Angle test (cheap, robust) rather than a precise box ray; the cone
    /// tightens with distance so far reads still require aiming.</summary>
    private Entity? PickLookedAt(EntityPlayer plr, float range)
    {
        var eye = plr.Pos.XYZ.Add(0, plr.LocalEyePos.Y, 0);
        var look = plr.Pos.GetViewVector().Normalize();
        Entity? best = null;
        double bestDist = range;

        foreach (var e in capi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive || e == plr) continue;
            if (e.GetBehavior<EntityBehaviorHarvestable>() == null) continue; // huntable game only

            var center = e.Pos.XYZ.Add(0, (e.SelectionBox?.Y2 ?? 0.5f) * 0.5, 0);
            var to = center.Sub(eye);
            double dist = to.Length();
            if (dist < 0.5 || dist > range) continue;

            var dir = to.Normalize();
            double dot = dir.X * look.X + dir.Y * look.Y + dir.Z * look.Z;
            // ~7 deg cone up close widening slightly with range, so aiming still matters.
            double minDot = 1.0 - 0.012 - dist / (range * 220.0);
            if (dot < minDot) continue;

            if (dist < bestDist) { bestDist = dist; best = e; }
        }
        return best;
    }

    private static float HealthPct(Entity e)
    {
        var tree = e.WatchedAttributes?.GetTreeAttribute("health");
        if (tree == null) return -1;
        float max = tree.GetFloat("maxhealth", 0);
        return max <= 0 ? -1 : GameMath.Clamp(tree.GetFloat("currenthealth", 0) / max, 0, 1);
    }

    private static string SizeWord(Entity e)
    {
        float h = e.SelectionBox?.Y2 ?? 1f;
        string key = h < 0.6f ? "track-size-small" : h < 1.3f ? "track-size-mid" : "track-size-large";
        return Lang.Get("almanactcm:" + key);
    }

    private static string? Heading(Entity e)
    {
        var m = e.Pos.Motion;
        if (m == null || m.Length() < 0.003) return null; // standing
        double ang = Math.Atan2(-m.X, -m.Z); // world yaw of travel
        string[] pts = { "track-dir-n", "track-dir-ne", "track-dir-e", "track-dir-se",
                         "track-dir-s", "track-dir-sw", "track-dir-w", "track-dir-nw" };
        int idx = (int)Math.Round(ang / (Math.PI / 4)) & 7;
        return Lang.Get("almanactcm:" + pts[idx]);
    }
}
