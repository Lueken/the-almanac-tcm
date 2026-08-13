using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using AlmanacTcm.Domains;

namespace AlmanacTcm.Gui;

/// <summary>
/// The quest-step toast: a small strip of the almanac's parchment carrying one checklist
/// line from a guide's quest block, with an empty box beside it, and then a rust check
/// dropping in and settling. It exists because the checklists in the book are the only
/// place the player is told what a job actually takes, and until now nothing told them
/// when the book had ticked one off.
///
/// Fed by LevelingClient.QuestKnowledge (every live first knowledge earn, bannered or
/// not) and answered by Illuminated's GetQuestStepsFor, which returns the text of every
/// quest item whose `doneWhen` matches that key. The API returns an empty list until
/// Illuminated's own doneWhen work lands, and an empty list means no toast, which is the
/// right degrade rather than a hole.
///
/// Same ancestry as the discovery banner and by the same standing rule: the palette, the
/// tearing and the texel paint all come from ParchmentStrip, magnified nearest-neighbour,
/// antialiasing off, shapes deterministic. This is a scrap of the same page, not a new
/// visual language. Sequential like the banner: one strip holds the stage and the next
/// waits, so a key that ticks two steps reads as two ticks.
/// </summary>
public class QuestStepToastRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly AlmanacIlluminated.AlmanacIlluminatedModSystem? illuminated;

    /// <summary>Waiting step texts, oldest first.</summary>
    private readonly Queue<string> pending = new();
    private const int PendingCap = 6;
    /// <summary>Steps shown per earn. One act rarely closes more than a couple of lines, and
    /// a wall of ticks is wallpaper.</summary>
    private const int MaxPerEarn = 2;

    private LoadedTexture? strip;
    private LoadedTexture? label;
    private LoadedTexture? check;
    private float ageMs;
    private float gapMs;

    private const float FadeInMs = 250f;
    /// <summary>The check waits for the strip to finish arriving, then lands.</summary>
    private const float CheckDropMs = 300f;
    private const float HoldMs = 2500f;
    private const float FadeOutMs = 600f;
    /// <summary>Quiet beat between two strips.</summary>
    private const float GapMs = 300f;

    /// <summary>Unscaled GUI points. Deliberately not a ConfigLib knob: this is a legibility
    /// floor for a line of prose, not a feel value like the banner's ceremony sizing.</summary>
    private const float FontSize = 18f;

    // Geometry of the composed strip, all framebuffer px unless the name says texels.
    private float pixelScale;
    private float stripH;
    private float contentH;
    private float boxPx;
    private float boxLeftFromStrip;   // px from the strip's left edge to the box's left edge
    private float labelLeftFromStrip;

    public double RenderOrder => 0.98;
    public int RenderRange => 10;

    public QuestStepToastRenderer(ICoreClientAPI capi,
        AlmanacIlluminated.AlmanacIlluminatedModSystem? illuminated)
    {
        this.capi = capi;
        this.illuminated = illuminated;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "almanactcm-quest-step-toasts");
    }

    /// <summary>Entry point from the network handler (main thread): a live knowledge key that
    /// carried no banner. Asks Illuminated which checklist lines that key closes.</summary>
    public void OnKnowledgeEarned(string key)
    {
        if (!TcmClientSettings.QuestToasts || string.IsNullOrEmpty(key)) return;
        if (illuminated == null) return;

        IReadOnlyList<string>? steps;
        try
        {
            steps = illuminated.GetQuestStepsFor(key);
        }
        catch (Exception e)
        {
            TcmLog.Error(capi, $"quest-step lookup for '{key}' threw ({e.Message}); no toast");
            return;
        }
        if (steps == null) return;

        int shown = 0;
        foreach (string step in steps)
        {
            if (string.IsNullOrWhiteSpace(step)) continue;
            if (pending.Count >= PendingCap) break;
            pending.Enqueue(step);
            if (++shown >= MaxPerEarn) break;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (strip == null || label == null)
        {
            if (pending.Count == 0) return;
            gapMs += deltaTime * 1000f;
            if (gapMs < GapMs) return;
            Compose(pending.Dequeue());
            ageMs = 0f;
            return;
        }

        ageMs += deltaTime * 1000f;
        float totalMs = FadeInMs + CheckDropMs + HoldMs + FadeOutMs;
        if (ageMs >= totalMs)
        {
            DisposeTextures();
            gapMs = 0f;
            return;
        }

        float alpha = 1f;
        if (ageMs < FadeInMs) alpha = ageMs / FadeInMs;
        else if (ageMs > totalMs - FadeOutMs) alpha = (totalMs - ageMs) / FadeOutMs;

        float guiScale = RuntimeEnv.GUIScale;
        // Below the discovery banner's anchor (155 GUI px above middle) and nowhere near the
        // practice toasts at the bottom of the screen, so a rank-up ceremony and a checklist
        // tick can land in the same second without stepping on each other.
        float anchorY = capi.Render.FrameHeight / 2f - 60f * guiScale;
        var tint = new Vec4f(1, 1, 1, alpha);

        float sw = strip.Width * pixelScale;
        float sh = strip.Height * pixelScale;
        float stripLeft = (capi.Render.FrameWidth - sw) / 2f;
        capi.Render.Render2DTexture(
            strip.TextureId, stripLeft, anchorY - stripH / 2f, sw, sh, 60f, tint);

        float contentTop = anchorY - contentH / 2f;
        capi.Render.Render2DTexture(
            label.TextureId, stripLeft + labelLeftFromStrip, contentTop,
            label.Width, label.Height, 61f, tint);

        // The check lands only after the strip has finished arriving: an empty box first,
        // then the tick, because the tick is the sentence.
        if (check == null || ageMs < FadeInMs) return;
        float t = Math.Clamp((ageMs - FadeInMs) / CheckDropMs, 0f, 1f);
        // Ease-out-back: falls in, overshoots by a hair, settles. Position only, so the
        // texel grid is never resampled at a fractional scale.
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float e = 1f + c3 * (t - 1f) * (t - 1f) * (t - 1f) + c1 * (t - 1f) * (t - 1f);
        float drop = (1f - e) * -(boxPx * 1.6f);
        float checkAlpha = alpha * Math.Min(1f, t / 0.5f);

        capi.Render.Render2DTexture(
            check.TextureId, stripLeft + boxLeftFromStrip, contentTop + drop,
            boxPx, boxPx, 62f, new Vec4f(1, 1, 1, checkAlpha));
    }

    // ------------------------------------------------------------------ composition

    private void Compose(string text)
    {
        double[] ink = { ParchmentStrip.Ink[0], ParchmentStrip.Ink[1], ParchmentStrip.Ink[2], 1 };
        // The book's body face, not the banner's decorative one: this is a line of
        // instructions being read back, not a ceremony being announced.
        CairoFont font = new CairoFont(FontSize, GuiStyle.StandardFontName, ink);

        // Line height drives both the texel grain and the box, so the box is always the
        // size of the text beside it whatever the GUI scale does.
        double lineH = font.GetFontExtents().Height;
        if (lineH < 4) lineH = FontSize * 1.4;   // defensive: a font with no extents

        // A long step wraps rather than running off the screen; the box stays beside the
        // first line either way.
        int maxTextW = Math.Max(120, (int)(capi.Render.FrameWidth * 0.42f));
        label?.Dispose();
        label = capi.Gui.TextTexture.GenTextTexture(text, font, maxTextW);

        // Same grain rule as the banner (a seventh of a line), so the two strips weave alike.
        int p = Math.Clamp((int)Math.Round(lineH / 7.0), 2, 12);
        pixelScale = p;

        int boxT = Math.Max(6, (int)Math.Round(lineH * 1.15 / p));
        int gapT = Math.Max(2, boxT / 3);
        int labelWT = (label.Width + p - 1) / p;
        int labelHT = (label.Height + p - 1) / p;
        int contentHT = Math.Max(boxT, labelHT);
        int contentWT = boxT + gapT + labelWT;

        int padHT = 5;
        int padVT = 2;
        int wT = contentWT + 2 * padHT;
        int hT = contentHT + 2 * padVT + 2;   // +2: wobble margin, one row each side

        stripH = hT * (float)p;
        contentH = contentHT * (float)p;
        boxPx = boxT * (float)p;
        boxLeftFromStrip = padHT * (float)p;
        labelLeftFromStrip = (padHT + boxT + gapT) * (float)p;

        Random rng = ParchmentStrip.Seed(wT, hT);
        int[,] cell = ParchmentStrip.BuildField(wT, hT, rng);
        ParchmentStrip.AgeRim(cell, rng);

        // The empty box: a one-texel ink ring, drawn after the rim pass so the ring counts
        // as ink rather than as parchment edge. Sits at the top of the content block, level
        // with the first line of the step.
        int boxX0 = padHT;
        int boxY0 = (hT - contentHT) / 2;
        for (int x = boxX0; x < boxX0 + boxT; x++)
        {
            for (int y = boxY0; y < boxY0 + boxT; y++)
            {
                bool ring = x == boxX0 || x == boxX0 + boxT - 1
                    || y == boxY0 || y == boxY0 + boxT - 1;
                if (ring && x < wT && y < hT && cell[x, y] != ParchmentStrip.Air)
                    cell[x, y] = ParchmentStrip.InkCell;
            }
        }

        strip?.Dispose();
        strip = ParchmentStrip.Paint(capi, cell, TcmClientSettings.BannerOpacity);

        check?.Dispose();
        check = ParchmentStrip.Paint(capi, BuildCheck(boxT), 1.0);
    }

    /// <summary>The tick, on the same texel grid as everything else: a short leg down-right
    /// into a long leg up-right, two texels of rust with a darker under-edge so it reads as
    /// something inked by a hand rather than stroked by a vector.</summary>
    private static int[,] BuildCheck(int size)
    {
        int[,] c = new int[size, size];
        int sx = Math.Max(1, size / 5);
        int sy = size / 2;
        int shortLeg = Math.Max(1, size / 4);

        for (int i = 0; i <= shortLeg; i++) Stroke(c, sx + i, sy + i);
        int longLeg = Math.Max(1, size - 2 - (sx + shortLeg));
        for (int i = 1; i <= longLeg; i++) Stroke(c, sx + shortLeg + i, sy + shortLeg - i);
        return c;
    }

    private static void Stroke(int[,] c, int x, int y)
    {
        Put(c, x, y, ParchmentStrip.RustCell);
        Put(c, x, y + 1, ParchmentStrip.RustDarkCell);
    }

    private static void Put(int[,] c, int x, int y, int v)
    {
        if (x < 0 || y < 0 || x >= c.GetLength(0) || y >= c.GetLength(1)) return;
        // The body wins wherever the two legs cross; the under-edge never eats it.
        if (v == ParchmentStrip.RustDarkCell && c[x, y] == ParchmentStrip.RustCell) return;
        c[x, y] = v;
    }

    private void DisposeTextures()
    {
        strip?.Dispose();
        label?.Dispose();
        check?.Dispose();
        strip = null;
        label = null;
        check = null;
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        DisposeTextures();
        pending.Clear();
    }
}
