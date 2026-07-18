using System;
using System.Linq;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 2 — THE TRACKER'S EYE (rank-bonus-design §HUN Axis 3; interaction ruled 2026-07-17:
/// SNEAK + LOOK; readout redesigned 2026-07-17 to a hunter's internal monologue). Two zones,
/// both only while crouched and only for real ANIMALS (has the harvestable behaviour AND a
/// creatureDiet — temporal hostiles like drifters/shivers eat nothing and are excluded):
///
///   READ (aimed within ~7 deg AND inside the rank scan range): the quarry, catalogued.
///     "{Name} (gender, age, size). {N}m."  + condition (Journeyman+) + wounds/heading (Master+).
///   SENSE (nearest animal within a wider radius, when not producing a read): the country.
///     "Sign of an animal to the {bearing}."  + ", moving {heading}." at Master+.
///
/// Entirely client-side: reads networked WatchedAttributes and the local synced HUN level; no
/// server round-trip. Below Apprentice nothing shows. Never displays unless crouched.
/// </summary>
public class HunTrackerEye : HudElement
{
    private readonly StringBuilder sb = new();
    private string lastText = "";

    // Focus delay: the hunter must hold the sneak-look for this long before the read resolves,
    // so it reads as concentration and never flashes on a quick crouch (ruled 2026-07-17;
    // tunable in-game via ConfigLib, TcmClientSettings.FocusDelay).
    private double focusAccum;

    // DIAG (temporary): throttles the read-gate trace to ~once a second so we can see, while the
    // hunter holds sneak, exactly which gate stops a read (grab/sneak/level/range/detection).
    private double diagAccum;
    private bool diagNow;

    /// <summary>0..1 focus progress, published for the vignette to darken the edges AS the
    /// hunter concentrates (0 = nothing, 1 = read resolved). One local player, so static.</summary>
    public static float FocusFraction { get; private set; }

    private const double LineHeight = 26;   // per text line at WhiteSmallText
    private const double PadX = 16, PadY = 11;
    private const double MinWidth = 120, MaxWidth = 520;

    public HunTrackerEye(ICoreClientAPI capi) : base(capi)
    {
        Compose("");
        capi.Gui.RegisterDialog(this); // without this a GuiDialog never enters the render loop
        capi.Event.RegisterGameTickListener(OnTick, 100);
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.05;

    /// <summary>Panel is rebuilt to hug the content in BOTH axes: height tracks the line count,
    /// width tracks the widest line (measured, clamped), so a short read gets a small snug box
    /// instead of a fixed slab (ruled 2026-07-17).</summary>
    private void Compose(string text)
    {
        var font = CairoFont.WhiteSmallText().WithStroke(new double[] { 0, 0, 0, 1 }, 2.0);
        font.Orientation = EnumTextOrientation.Center;

        string measure = text.Length == 0 ? " " : text;
        // Widest line, measured. GetTextExtents does NOT count the 2px glyph stroke, so the
        // rendered text is a few px wider than measured; the +20 margin covers the stroke and
        // keeps single lines from wrapping (the clip in 0.3.88).
        double scaledW = 0;
        foreach (var ln in measure.Split('\n')) scaledW = Math.Max(scaledW, font.GetTextExtents(ln).Width);
        double textW = scaledW / RuntimeEnv.GUIScale; // extents are scaled; bounds are unscaled
        double w = GameMath.Clamp(textW + 2 * PadX + 20, MinWidth, MaxWidth);
        double innerW = w - 2 * PadX;

        // Height from the TRUE wrapped line count at this width, so anything that still wraps
        // (a long Master read past MaxWidth) gets a tall enough box instead of clipping.
        double hScaled = capi.Gui.Text.GetMultilineTextHeight(font, measure, innerW * RuntimeEnv.GUIScale);
        double textH = hScaled / RuntimeEnv.GUIScale;
        double h = textH + 2 * PadY;

        var panel = ElementBounds.Fixed(0, 0, w, h);
        var textBounds = ElementBounds.Fixed(PadX, PadY, innerW, textH);
        var dialogBounds = panel.ForkBoundingParent()
            .WithAlignment(EnumDialogArea.CenterMiddle)
            .WithFixedAlignmentOffset(0, 150);
        SingleComposer = capi.Gui.CreateCompo("huntrackereye", dialogBounds)
            .AddStaticCustomDraw(panel, DrawPanel)
            .AddDynamicText("", font, textBounds, "read")
            .Compose();
        // Start closed; only opens while there is something to read (no empty panel).
    }

    /// <summary>Semi-transparent dark rounded panel for legibility over bright terrain.</summary>
    private void DrawPanel(Context ctx, ImageSurface surface, ElementBounds b)
    {
        ctx.SetSourceRGBA(0, 0, 0, 0.55);
        GuiElement.RoundRectangle(ctx, b.drawX, b.drawY, b.OuterWidth, b.OuterHeight, 5);
        ctx.Fill();
    }

    private void OnTick(float dt)
    {
        diagAccum += dt;
        diagNow = diagAccum >= 1.0;
        if (diagNow) diagAccum = 0;

        string text = BuildRead();

        // Nothing to read: reset the focus timer and hide.
        if (text.Length == 0)
        {
            focusAccum = 0;
            FocusFraction = 0;
            if (lastText.Length != 0) { lastText = ""; TryClose(); }
            return;
        }

        // Focusing: hold the read back until the hunter has concentrated for the focus delay.
        double focusDelay = TcmClientSettings.FocusDelay;
        focusAccum += dt;
        FocusFraction = focusDelay <= 0.01 ? 1f : (float)Math.Min(1.0, focusAccum / focusDelay);
        if (focusAccum < focusDelay)
        {
            if (IsOpened()) { lastText = ""; TryClose(); }
            return;
        }

        if (text == lastText) return;
        lastText = text;

        // Width and height both depend on the content, so rebuild the box each new read.
        Compose(text);
        SingleComposer?.GetDynamicText("read")?.SetNewText(text);
        if (!IsOpened()) TryOpen();
    }

    private string BuildRead()
    {
        if (diagNow) TcmLog.Cat(capi, "hun",
            $"read gate: grabbed={capi.Input.MouseGrabbed} sneak={capi.World?.Player?.Entity?.Controls.Sneak} level={HunDomain.ClientLevel()}");

        // Only while actively playing: the moment any menu/inventory opens the mouse ungrabs,
        // so the panel closes and never overlays (or blocks) the escape menu's buttons.
        if (!capi.Input.MouseGrabbed) return "";

        var plr = capi.World?.Player?.Entity;
        if (plr == null || !plr.Controls.Sneak) return ""; // only ever while crouched

        int level = HunDomain.ClientLevel();
        if (level < 5) return ""; // pre-Apprentice: the read has not been learned
        int tier = (level - 1) / 4; // 1 App .. 4 GM
        float range = tier switch { 1 => 12f, 2 => 20f, 3 => 30f, _ => 42f };
        float senseRange = range + 24f;

        var eye = plr.Pos.XYZ.Add(0, plr.LocalEyePos.Y, 0);
        var look = plr.Pos.GetViewVector().Normalize();

        Entity? aimed = null; double aimedDist = range;
        Entity? nearest = null; double nearestDist = senseRange;

        foreach (var e in capi.World.LoadedEntities.Values)
        {
            if (e == null || !e.Alive || e == plr || !IsAnimal(e)) continue;
            var center = e.Pos.XYZ.Add(0, (e.SelectionBox?.Y2 ?? 0.5f) * 0.5, 0);
            var to = center.Sub(eye);
            double dist = to.Length();
            if (dist < 0.4) continue;

            if (dist < nearestDist) { nearestDist = dist; nearest = e; }

            if (dist <= range)
            {
                var dir = to.Normalize();
                double dot = dir.X * look.X + dir.Y * look.Y + dir.Z * look.Z;
                if (dot >= 0.991 && dist < aimedDist) { aimedDist = dist; aimed = e; } // ~7.6 deg cone
            }
        }

        if (diagNow) TcmLog.Cat(capi, "hun",
            $"read scan: tier={tier} range={range:0} senseRange={senseRange:0} " +
            $"aimed={(aimed != null ? aimedDist : -1):0.0} nearest={(nearest != null ? nearestDist : -1):0.0} loaded={capi.World.LoadedEntities.Count}");

        if (aimed != null) return ReadQuarry(aimed, aimedDist, tier);
        if (nearest != null) return SenseLine(plr, nearest, tier);
        return "";
    }

    // ------------------------------------------------------------ the aimed read

    private string ReadQuarry(Entity e, double dist, int tier)
    {
        sb.Clear();
        // The display name already carries the gender in its own parenthetical ("Wolf (male)");
        // strip it so our fuller descriptor doesn't repeat it ("Wolf (male, grown)", not
        // "Wolf (male) (male, grown)").
        string rawName = e.GetName();
        int cut = rawName.IndexOf(" (", StringComparison.Ordinal);
        string name = cut > 0 ? rawName.Substring(0, cut) : rawName;
        string paren = Descriptors(e, name);
        sb.Append(paren.Length > 0 ? Lang.Get("almanactcm:track-quarry", name, paren, (int)Math.Round(dist))
                                   : Lang.Get("almanactcm:track-quarry-plain", name, (int)Math.Round(dist)));

        if (tier >= 2)
        {
            float hp = HealthPct(e);
            if (hp >= 0)
            {
                string cond = hp > 0.7f ? "track-cond-healthy" : hp > 0.3f ? "track-cond-wounded" : "track-cond-neardeath";
                sb.Append('\n').Append(Lang.Get("almanactcm:" + cond));
                if (e.WatchedAttributes?.GetFloat("stressLevel", 0) > 0.1f)
                    sb.Append(' ').Append(Lang.Get("almanactcm:track-agitated"));
            }
        }

        if (tier >= 3)
        {
            if (e.WatchedAttributes?.GetBool("isBleeding") == true)
                sb.Append('\n').Append(Lang.Get("almanactcm:track-bleeding"));
            string? head = Heading(e);
            if (head != null)
                sb.Append('\n').Append(Lang.Get("almanactcm:track-moving", head));
        }
        return sb.ToString();
    }

    /// <summary>"(male, young, large)" from what is confidently known; parts omitted otherwise.
    /// Age is dropped when the name already implies it (fawn/chick/etc.).</summary>
    private static string Descriptors(Entity e, string name)
    {
        string g = Gender(e);
        string age = Age(e);
        if (age.Length > 0 && name.ToLowerInvariant().Contains(Lang.Get("almanactcm:track-age-young").ToLowerInvariant()))
            age = "";
        string size = Size(e);
        var parts = new[] { g, age, size }.Where(s => s.Length > 0).ToArray();
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------ the sense line

    private string SenseLine(EntityPlayer plr, Entity e, int tier)
    {
        string bearing = Bearing(plr, e);
        string? head = tier >= 3 ? Heading(e) : null; // Master I+ adds run heading
        return head != null
            ? Lang.Get("almanactcm:track-sense-moving", bearing, head)
            : Lang.Get("almanactcm:track-sense", bearing);
    }

    // ------------------------------------------------------------ helpers

    private static bool IsAnimal(Entity e)
    {
        if (e.GetBehavior<EntityBehaviorHarvestable>() == null) return false;
        // Animals eat; temporal hostiles (drifter/shiver/bowtorn/bells) have no diet.
        return e.Properties?.Attributes?["creatureDiet"]?.Exists == true;
    }

    private static float HealthPct(Entity e)
    {
        var tree = e.WatchedAttributes?.GetTreeAttribute("health");
        if (tree == null) return -1;
        float max = tree.GetFloat("maxhealth", 0);
        return max <= 0 ? -1 : GameMath.Clamp(tree.GetFloat("currenthealth", 0) / max, 0, 1);
    }

    private static string Gender(Entity e)
    {
        var parts = e.Code?.Path?.Split('-') ?? Array.Empty<string>();
        if (parts.Contains("female")) return Lang.Get("almanactcm:track-gender-female");
        if (parts.Contains("male")) return Lang.Get("almanactcm:track-gender-male");
        string? g = e.WatchedAttributes?.GetString("gender");
        if (g == "female") return Lang.Get("almanactcm:track-gender-female");
        if (g == "male") return Lang.Get("almanactcm:track-gender-male");
        return "";
    }

    private static readonly string[] YoungMarkers =
        { "baby", "child", "chick", "calf", "cub", "kit", "kitten", "piglet", "fawn", "lamb", "foal", "pup", "joey" };

    private static string Age(Entity e)
    {
        var parts = e.Code?.Path?.Split('-') ?? Array.Empty<string>();
        if (parts.Any(p => YoungMarkers.Contains(p))) return Lang.Get("almanactcm:track-age-young");
        return ""; // adults carry no age word
    }

    private static string Size(Entity e)
    {
        float h = e.SelectionBox?.Y2 ?? 1f;
        string key = h < 0.6f ? "track-size-small" : h < 1.3f ? "track-size-grown" : "track-size-large";
        return Lang.Get("almanactcm:" + key);
    }

    /// <summary>Compass bearing of the entity relative to the player (where it is).</summary>
    private static string Bearing(EntityPlayer plr, Entity e)
    {
        double dx = e.Pos.X - plr.Pos.X, dz = e.Pos.Z - plr.Pos.Z;
        return Compass(Math.Atan2(-dx, -dz));
    }

    /// <summary>Compass heading of the entity's own motion (where it is going), null if still.</summary>
    private static string? Heading(Entity e)
    {
        var m = e.Pos.Motion;
        if (m == null || m.Length() < 0.003) return null;
        return Compass(Math.Atan2(-m.X, -m.Z));
    }

    private static string Compass(double ang)
    {
        string[] pts = { "track-dir-n", "track-dir-ne", "track-dir-e", "track-dir-se",
                         "track-dir-s", "track-dir-sw", "track-dir-w", "track-dir-nw" };
        int idx = (int)Math.Round(ang / (Math.PI / 4)) & 7;
        return Lang.Get("almanactcm:" + pts[idx]);
    }
}
