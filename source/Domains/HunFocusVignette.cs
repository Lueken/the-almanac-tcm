using System;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 2 flourish — THE FOCUS VIGNETTE. As the hunter holds the sneak-look and concentration
/// builds (HunTrackerEye.FocusFraction 0..1 over the focus delay), the screen edges darken, landing
/// full as the read resolves; it eases back out when the hunter stands or looks away. Cosmetic only.
///
/// Rendered as an always-open HUD <see cref="GuiDialog"/>, NOT a raw IRenderer. The engine's
/// Render2DTexture(Premultiplied) sets uniforms on guiShaderProg and draws WITHOUT binding it, so it
/// only paints inside the GUI render pass. A raw Ortho renderer runs outside that pass: the draw
/// call executed with a valid texture and alpha but composited nothing (the 0.3.90-0.3.97 invisible-
/// vignette bug, proven by a draw-trace showing focus=1 alpha=0.42 tex=346 while the texture itself
/// sampled correctly, corner A233 -> centre A0). OnRenderGUI of a HUD dialog runs inside the pass,
/// where the shader is bound; that is the path the tracker panel and Illuminated's overlay use.
/// </summary>
public class HunFocusVignette : GuiDialog
{
    private LoadedTexture? tex;
    private float current;         // eased 0..1
    private float builtReach = -1; // the reach the current texture was built for
    private float logAccum;        // throttles the verbose draw-trace to ~once every 2s

    public HunFocusVignette(ICoreClientAPI capi) : base(capi)
    {
        BuildTexture(TcmClientSettings.VignetteReach);
        capi.Gui.RegisterDialog(this);
        // Always open so OnRenderGUI runs every frame; it draws nothing while focus is ~0. A HUD
        // dialog neither grabs the mouse nor pauses the game, so this is invisible to gameplay.
        capi.Event.RegisterGameTickListener(_ => { if (!IsOpened()) TryOpen(); }, 500);
    }

    public override string ToggleKeyCombinationCode => null!;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override bool Focusable => false;
    public override bool PrefersUngrabbedMouse => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    // Under the tracker text panel (0.05) and the crosshair, over the world.
    public override double DrawOrder => 0.04;

    /// <summary>Radial gradient from dead centre out to the corner: transparent through
    /// <paramref name="reach"/> of the half-diagonal, then darkening to solid black at the corners.
    /// MUST start at centre (r=0) so the clear core falls inside the visible frame. MUST flush the
    /// surface before upload, else LoadOrUpdateCairoTexture reads an all-zero buffer.</summary>
    private void BuildTexture(float reach)
    {
        const int size = 512;
        float clear = GameMath.Clamp(reach, 0.05f, 0.9f);
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        var grad = new RadialGradient(size / 2.0, size / 2.0, 0,
                                      size / 2.0, size / 2.0, size * 0.72);
        grad.AddColorStop(0, new Color(0, 0, 0, 0));
        grad.AddColorStop(clear, new Color(0, 0, 0, 0));
        grad.AddColorStop(1, new Color(0, 0, 0, 1));
        ctx.SetSource(grad);
        ctx.Rectangle(0, 0, size, size);
        ctx.Fill();
        surface.Flush();

        grad.Dispose();
        ctx.Dispose();

        tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        builtReach = reach;
        surface.Dispose();
    }

    public override void OnRenderGUI(float dt)
    {
        // Rebuild only when the reach setting actually changed (rare: a config edit).
        if (Math.Abs(TcmClientSettings.VignetteReach - builtReach) > 0.001f)
            BuildTexture(TcmClientSettings.VignetteReach);

        float target = HunTrackerEye.FocusFraction;
        // Ease toward the target: fade-in tracks the focus build, fade-out is a soft glide.
        float rate = target > current ? 6f : 3f;
        current += (target - current) * Math.Min(1f, rate * dt);
        if (current <= 0.003f) { current = 0; return; }
        if (tex == null) return;

        logAccum += dt;
        if (logAccum >= 2f)
        {
            logAccum = 0;
            TcmLog.Cat(capi, "hun",
                $"vignette draw: focus={HunTrackerEye.FocusFraction:0.00} current={current:0.00} " +
                $"alpha={current * TcmClientSettings.VignetteIntensity:0.00} reach={builtReach:0.00} tex={tex.TextureId}");
        }

        // Cairo-sourced (premultiplied) texture, drawn inside the GUI pass where guiShaderProg is
        // bound, so this actually composites (unlike the old raw-Ortho renderer).
        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, 0, 0,
            capi.Render.FrameWidth, capi.Render.FrameHeight, 50f,
            new Vec4f(1, 1, 1, current * TcmClientSettings.VignetteIntensity));
    }

    public override void Dispose()
    {
        tex?.Dispose();
        tex = null;
        base.Dispose();
    }
}
