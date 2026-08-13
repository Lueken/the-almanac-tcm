using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using AlmanacTcm.Domains;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Gui;

/// <summary>
/// The discovery banner: rank-ups and named knowledge earns, drawn where the vanilla
/// lore banner sits (screen center, 155 GUI px above middle). These moments are few
/// and far between, so they should feel special — and they belong to the BOOK: the
/// banner is a torn strip of the almanac's own parchment, sepia ink on the page tones
/// sampled from Illuminated's bookframe, with rust rubrication at the ends. Drawn
/// per-message on a texel grid (no shipped texture, see ParchmentStrip), so it fits any
/// text and GUI scale. Replaced TriggerIngameDiscovery (2026-08-08): the vanilla
/// HudIngameDiscovery element has no backing and no styling seam, and gold-on-sand was
/// unreadable.
///
/// A KNOWLEDGE banner carries a second, smaller ink line under the main text telling the
/// player which key opens the book, because the ink they just earned is on a page they
/// have to go and read. Rank-ups do not: nothing new was written, so there is nothing to
/// send them to. The key combo is resolved from the player's ACTUAL binding at compose
/// time and the line is dropped entirely if it cannot be resolved, so a rebound or
/// unbound book never prints a raw hotkey code at anybody.
///
/// Sequential where practice toasts stack: one banner holds the stage, the next waits
/// its turn, so three 3am rank-ups read as three moments. Feel values ride
/// TcmClientSettings (ConfigLib): ribbon opacity, text size, hold seconds.
/// </summary>
public class DiscoveryBannerRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;

    /// <summary>Waiting banners, oldest first. Capped so a pathological burst cannot
    /// park twenty minutes of stale ceremony on the queue.</summary>
    private readonly Queue<(string Text, BannerKind Kind)> pending = new();
    private const int PendingCap = 8;

    private LoadedTexture? ribbon;
    private LoadedTexture? label;
    /// <summary>The book-hotkey subline, knowledge banners only; null on a rank-up and on
    /// any client whose book binding could not be read.</summary>
    private LoadedTexture? subLabel;
    /// <summary>Framebuffer-px height of the central band inside the ribbon texture;
    /// the label centers on it, and the screen anchor is the band's midline.</summary>
    private float bandH;
    /// <summary>Framebuffer-px height of the text block (main line, plus gap and subline
    /// when there is one). Centered on the anchor, same as the bare label used to be.</summary>
    private float contentH;
    /// <summary>Framebuffer-px breathing room between the two lines.</summary>
    private float lineGap;
    private float ageMs;
    private float gapMs;

    private const float FadeInMs = 300f;
    private const float FadeOutMs = 1100f;
    /// <summary>Quiet beat between two banners; without it back-to-back ceremonies smear.</summary>
    private const float GapMs = 450f;

    /// <summary>The subline sits at this fraction of the main text size: small enough to read
    /// as an aside in the margin, large enough to actually read.</summary>
    private const double SubScale = 0.6;

    public double RenderOrder => 0.98;
    public int RenderRange => 10;

    public DiscoveryBannerRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "almanactcm-discovery-banners");
    }

    /// <summary>Entry point from the network handler (main thread).</summary>
    public void Show(string text, BannerKind kind)
    {
        if (!TcmClientSettings.DiscoveryBanners || string.IsNullOrEmpty(text)) return;
        if (pending.Count >= PendingCap) return;
        pending.Enqueue((text, kind));
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (ribbon == null || label == null)
        {
            if (pending.Count == 0) return;
            gapMs += deltaTime * 1000f;
            if (gapMs < GapMs) return;
            var next = pending.Dequeue();
            Compose(next.Text, next.Kind);
            ageMs = 0f;
            return;
        }

        ageMs += deltaTime * 1000f;
        float holdMs = TcmClientSettings.BannerHold * 1000f;
        float totalMs = FadeInMs + holdMs + FadeOutMs;

        if (ageMs >= totalMs)
        {
            DisposeTextures();
            gapMs = 0f;
            return;
        }

        float alpha = 1f;
        if (ageMs < FadeInMs) alpha = ageMs / FadeInMs;
        else if (ageMs > FadeInMs + holdMs) alpha = 1f - (ageMs - FadeInMs - holdMs) / FadeOutMs;

        float guiScale = RuntimeEnv.GUIScale;
        // The anchor is the band's midline, where vanilla centers its lore text.
        float anchorY = capi.Render.FrameHeight / 2f - 155f * guiScale;
        var tint = new Vec4f(1, 1, 1, alpha);

        // The ribbon texture is texel-sized; blow it up by the texel scale here. The
        // nearest-neighbour magnification (linearMag false) is what keeps it chunky.
        float rw = ribbon.Width * pixelScale;
        float rh = ribbon.Height * pixelScale;
        capi.Render.Render2DTexture(
            ribbon.TextureId,
            (capi.Render.FrameWidth - rw) / 2f,
            anchorY - bandH / 2f,
            rw, rh, 60f, tint);

        // The text block centers on the anchor as a whole, so a banner with a subline sits
        // where a banner without one would, only taller.
        float contentTop = anchorY - contentH / 2f;
        capi.Render.Render2DTexture(
            label.TextureId,
            (capi.Render.FrameWidth - label.Width) / 2f,
            contentTop,
            label.Width, label.Height, 61f, tint);

        if (subLabel != null)
        {
            capi.Render.Render2DTexture(
                subLabel.TextureId,
                (capi.Render.FrameWidth - subLabel.Width) / 2f,
                contentTop + label.Height + lineGap,
                subLabel.Width, subLabel.Height, 61f, tint);
        }
    }

    // ------------------------------------------------------------------ composition

    private void Compose(string text, BannerKind kind)
    {
        // Sepia ink on parchment, the book's own contract — no gold, no stroke. The
        // parchment behind it is what carries legibility over any terrain.
        double[] ink = { ParchmentStrip.Ink[0], ParchmentStrip.Ink[1], ParchmentStrip.Ink[2], 1 };
        CairoFont font = new CairoFont(
            TcmClientSettings.BannerFontSize,
            GuiStyle.DecorativeFontName,
            ink);

        label?.Dispose();
        label = capi.Gui.TextTexture.GenTextTexture(text, font);

        subLabel?.Dispose();
        subLabel = null;
        lineGap = 0f;

        // Knowledge only: a rank-up wrote nothing new to go and read.
        string? subText = kind == BannerKind.Knowledge ? BookHotkeyLine() : null;
        if (subText != null)
        {
            CairoFont subFont = new CairoFont(
                TcmClientSettings.BannerFontSize * SubScale,
                GuiStyle.DecorativeFontName,
                ink);
            subLabel = capi.Gui.TextTexture.GenTextTexture(subText, subFont);
            lineGap = Math.Max(2f, subLabel.Height * 0.25f);
        }

        int contentW = label.Width;
        contentH = label.Height;
        if (subLabel != null)
        {
            contentW = Math.Max(contentW, subLabel.Width);
            contentH += lineGap + subLabel.Height;
        }

        ribbon?.Dispose();
        // The texel grain follows the MAIN line only, so a banner does not change its
        // weave the moment a subline joins it.
        ribbon = DrawParchment(contentW, (int)Math.Ceiling(contentH), label.Height);
    }

    /// <summary>The subline text, or null when the player's book binding cannot be read.
    /// Never falls back to a hardcoded combo and never prints the raw hotkey code: an
    /// unresolvable binding means the line is simply not there.</summary>
    private string? BookHotkeyLine()
    {
        string? combo = null;
        try
        {
            HotKey? hotkey = capi.Input?.GetHotKeyByCode(
                AlmanacIlluminated.GuiDialogIlluminatedBook.HotkeyCode);
            // KeyCombination.ToString() answers "?" for an unset keycode; that is the
            // shape of "unbound", and it is not something to show a player.
            combo = hotkey?.CurrentMapping?.ToString();
        }
        catch (Exception) { /* no input API on this surface: drop the line, keep the banner */ }

        if (string.IsNullOrWhiteSpace(combo) || combo == "?") return null;
        return Lang.Get("almanactcm:toast-open-book", combo);
    }

    /// <summary>Screen-px size of one texel; set alongside bandH in DrawParchment.</summary>
    private float pixelScale;

    /// <summary>Draws the banner as a TORN STRIP OF THE ALMANAC'S OWN PARCHMENT (ruled
    /// 2026-08-08 after two rejected passes — smooth vector, then generic heraldry; the
    /// reference is Illuminated's bookframe.png and its page scraps). The tearing, the
    /// mottle and the aged rim live in ParchmentStrip, shared with the quest-step toast.
    /// The one flourish that belongs to the banner alone is manuscript rubrication: a
    /// rust-red diamond at each end.</summary>
    /// <param name="contentW">Framebuffer-px width of the widest text line.</param>
    /// <param name="contentH">Framebuffer-px height of the whole text block.</param>
    /// <param name="grainH">Framebuffer-px height the texel size is derived from.</param>
    private LoadedTexture DrawParchment(int contentW, int contentH, int grainH)
    {
        double opacity = TcmClientSettings.BannerOpacity;

        // One texel ≈ a seventh of the text height, the grain of the game's sprites.
        int p = Math.Clamp((int)Math.Round(grainH / 7.0), 2, 12);
        pixelScale = p;

        int textWT = (contentW + p - 1) / p;
        int textHT = (contentH + p - 1) / p;
        int padHT = 10;                             // torn end + rubric diamond + breathing room
        int padVT = 3;
        int wT = textWT + 2 * padHT;
        int hT = textHT + 2 * padVT + 2;            // +2: wobble margin, one row each side
        bandH = hT * (float)p;

        // Deterministic per size: the tear must not reshape frame-to-frame, and equal
        // messages tear equally (Date/random seeds are display-only here regardless).
        Random rng = ParchmentStrip.Seed(wT, hT);
        int[,] cell = ParchmentStrip.BuildField(wT, hT, rng);
        ParchmentStrip.AgeRim(cell, rng);

        // Rubrication: one rust diamond at each end, between the tear and the text.
        int cy = hT / 2;
        foreach (int dx in new[] { padHT / 2 + 2, wT - padHT / 2 - 3 })
        {
            for (int i = -2; i <= 2; i++)
            {
                int half = 2 - Math.Abs(i);
                for (int x = dx - half; x <= dx + half; x++)
                {
                    if (x >= 0 && x < wT && cell[x, cy + i] != ParchmentStrip.Air)
                        cell[x, cy + i] = ParchmentStrip.RustCell;
                }
            }
            if (cell[dx, cy] != ParchmentStrip.Air) cell[dx, cy] = ParchmentStrip.RustDarkCell;
        }

        return ParchmentStrip.Paint(capi, cell, opacity);
    }

    private void DisposeTextures()
    {
        ribbon?.Dispose();
        label?.Dispose();
        subLabel?.Dispose();
        ribbon = null;
        label = null;
        subLabel = null;
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        DisposeTextures();
        pending.Clear();
    }
}
