using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 2 flourish — THE FOCUS VIGNETTE. As the hunter holds the sneak-look and concentration
/// builds (HunTrackerEye.FocusFraction 0..1), the screen edges darken, easing back out when the
/// hunter stands or looks away. Cosmetic only.
///
/// Rendered as an always-open <see cref="HudElement"/> — the SAME vehicle as the tracker panel,
/// which provably paints. The engine's Render2DTexture(Premultiplied) only composites inside the
/// GUI render pass (it never binds guiShaderProg itself), so the earlier raw Ortho IRenderer drew
/// nothing despite a valid texture and alpha. OnRenderGUI of an open HudElement runs inside that
/// pass. The draw-trace here is UNCONDITIONAL so we can confirm the method runs and the open state.
/// </summary>
public class HunFocusVignette : HudElement
{
    private LoadedTexture? tex;
    private float current;         // eased 0..1
    private float builtReach = -1; // the reach the current texture was built for
    private float logAccum;        // throttles the trace to ~once every 2s

    public HunFocusVignette(ICoreClientAPI capi) : base(capi)
    {
        BuildTexture(TcmClientSettings.VignetteReach);
        ComposeStub();
        capi.Gui.RegisterDialog(this);
        // Keep it open so OnRenderGUI runs every frame. A HUD element neither grabs the mouse nor
        // pauses the game, so an always-open one is invisible to gameplay.
        capi.Event.RegisterGameTickListener(_ => { if (!IsOpened()) TryOpen(); }, 500);
    }

    /// <summary>A 1x1 composer so the HUD element has something to open with; the vignette itself
    /// is painted directly in OnRenderGUI, not by the composer.</summary>
    private void ComposeStub()
    {
        var panel = ElementBounds.Fixed(0, 0, 1, 1);
        var dialogBounds = panel.ForkBoundingParent().WithAlignment(EnumDialogArea.LeftTop);
        SingleComposer = capi.Gui.CreateCompo("hunfocusvignette", dialogBounds).Compose();
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.04; // under the tracker text panel (0.05), over the world

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
        surface.Flush(); // commit the draw before upload, else the texture uploads all-zero

        grad.Dispose();
        ctx.Dispose();

        tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        builtReach = reach;
        surface.Dispose();
    }

    public override void OnRenderGUI(float dt)
    {
        base.OnRenderGUI(dt); // renders the 1x1 stub composer

        if (Math.Abs(TcmClientSettings.VignetteReach - builtReach) > 0.001f)
            BuildTexture(TcmClientSettings.VignetteReach);

        float target = HunTrackerEye.FocusFraction;
        float rate = target > current ? 6f : 3f;
        current += (target - current) * Math.Min(1f, rate * dt);

        // UNCONDITIONAL trace: proves OnRenderGUI runs and reports the open + focus state even
        // when nothing is drawn, so we can tell "not rendering" from "rendering but invisible".
        logAccum += dt;
        if (logAccum >= 2f)
        {
            logAccum = 0;
            TcmLog.Cat(capi, "hun",
                $"vignette gui: opened={IsOpened()} focus={target:0.00} current={current:0.00} " +
                $"alpha={current * TcmClientSettings.VignetteIntensity:0.00} tex={(tex?.TextureId ?? -1)} fw={capi.Render.FrameWidth}");
        }

        if (current <= 0.003f) { current = 0; return; }
        if (tex == null) return;

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
