using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using AlmanacTcm.Domains;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Gui;

/// <summary>
/// The practice toast: a "+0.4 Mining" that spawns centered over the hotbar, drifts
/// upward, and fades (xp-toast-overlay-study.md, ruled 2026-07-27). Pure sensation:
/// the ledger and the Callings tab remain the record; this renderer holds no state
/// the server didn't already show in the Info-tab chat line.
///
/// Mechanics are the vanilla chat-bubble pair: GenTextTexture for the string,
/// Render2DTexture in an Ortho-stage renderer, age-driven expiry. Same-domain gains
/// inside the merge window fold into one toast whose number ticks up (felling and
/// mining are burst verbs; without merging this is wallpaper). All feel values come
/// from TcmClientSettings (ConfigLib), including the text color as a hex code.
/// </summary>
public class PracticeToastRenderer : IRenderer
{
    private class Toast
    {
        public string DomainCode = "";
        public string DomainName = "";
        public string Technique = "";
        public double Total;
        public float AgeMs;
        public LoadedTexture? Texture;
    }

    private readonly ICoreClientAPI capi;
    private readonly DomainSetTemplate template;
    private readonly List<Toast> toasts = new();  // index 0 = newest (lowest on screen)

    public double RenderOrder => 0.98;
    public int RenderRange => 10;

    public PracticeToastRenderer(ICoreClientAPI capi, DomainSetTemplate template)
    {
        this.capi = capi;
        this.template = template;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "almanactcm-practice-toasts");
    }

    /// <summary>Entry point from the network handler (main thread).</summary>
    public void OnPracticeGain(string domainCode, string technique, float raw)
    {
        if (!TcmClientSettings.ToastsEnabled || raw <= 0) return;

        float mergeMs = TcmClientSettings.ToastMerge * 1000f;
        foreach (Toast live in toasts)
        {
            if (live.DomainCode == domainCode && live.AgeMs <= mergeMs)
            {
                live.Total += raw;
                live.Technique = technique;
                live.AgeMs = 0f;      // a merged gain restarts the lifetime, number ticks up
                Retexture(live);
                return;
            }
        }

        var toast = new Toast
        {
            DomainCode = domainCode,
            DomainName = template.FindDomain(domainCode)?.DisplayName ?? domainCode,
            Technique = technique,
            Total = raw,
        };
        Retexture(toast);
        toasts.Insert(0, toast);

        int max = (int)TcmClientSettings.ToastMax;
        while (toasts.Count > max)
        {
            toasts[^1].Texture?.Dispose();
            toasts.RemoveAt(toasts.Count - 1);
        }
    }

    private void Retexture(Toast toast)
    {
        string text = $"+{toast.Total:0.##} {toast.DomainName}";
        if (TcmClientSettings.ToastTechnique && toast.Technique.Length > 0)
        {
            text += $" ({toast.Technique})";
        }

        // Font size is UNSCALED: CairoFont.SetupContext applies GUI scale itself
        // (GuiElement.scaled), so the texture comes back in framebuffer pixels already.
        CairoFont font = new CairoFont(
                TcmClientSettings.ToastFontSize,
                GuiStyle.StandardFontName,
                ParseHex(TcmClientSettings.ToastColor))
            .WithStroke(new double[] { 0, 0, 0, 0.55 }, 2.0);

        toast.Texture?.Dispose();
        toast.Texture = capi.Gui.TextTexture.GenTextTexture(text, font);
    }

    /// <summary>#RGB, #RRGGBB or #AARRGGBB (leading # optional); anything unparsable
    /// falls back to the shipped amber so a typo in config can't blank the toast.</summary>
    internal static double[] ParseHex(string? hex)
    {
        double[] fallback = { 0.91, 0.765, 0.416, 1 };  // #E8C36A
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string h = hex.Trim().TrimStart('#');
        try
        {
            if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            if (h.Length == 6)
            {
                return new double[] {
                    Convert.ToInt32(h[..2], 16) / 255.0,
                    Convert.ToInt32(h[2..4], 16) / 255.0,
                    Convert.ToInt32(h[4..6], 16) / 255.0,
                    1
                };
            }
            if (h.Length == 8)
            {
                return new double[] {
                    Convert.ToInt32(h[2..4], 16) / 255.0,
                    Convert.ToInt32(h[4..6], 16) / 255.0,
                    Convert.ToInt32(h[6..8], 16) / 255.0,
                    Convert.ToInt32(h[..2], 16) / 255.0
                };
            }
        }
        catch (Exception) { }
        return fallback;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (toasts.Count == 0) return;

        float lifetimeMs = TcmClientSettings.ToastLifetime * 1000f;
        float guiScale = RuntimeEnv.GUIScale;
        float baseY = capi.Render.FrameHeight - TcmClientSettings.ToastOffsetY * guiScale;
        float stack = 0f;  // cumulative height of newer toasts below the one being drawn

        for (int i = 0; i < toasts.Count; i++)
        {
            Toast toast = toasts[i];
            toast.AgeMs += deltaTime * 1000f;
            float t = toast.AgeMs / lifetimeMs;

            if (t >= 1f || toast.Texture == null)
            {
                toast.Texture?.Dispose();
                toasts.RemoveAt(i);
                i--;
                continue;
            }

            // Hold full ink briefly, then ease out; drift is linear over the lifetime.
            float alpha = t < 0.55f ? 1f : 1f - (t - 0.55f) / 0.45f;
            float rise = TcmClientSettings.ToastRise * guiScale * t;
            float x = (capi.Render.FrameWidth - toast.Texture.Width) / 2f;
            float y = baseY - stack - rise - toast.Texture.Height;

            capi.Render.Render2DTexture(
                toast.Texture.TextureId, x, y,
                toast.Texture.Width, toast.Texture.Height,
                50f, new Vec4f(1, 1, 1, alpha));

            stack += toast.Texture.Height + 4f * guiScale;
        }
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        foreach (Toast toast in toasts) toast.Texture?.Dispose();
        toasts.Clear();
    }
}
