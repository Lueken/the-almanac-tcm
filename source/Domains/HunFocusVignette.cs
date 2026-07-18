using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 2 flourish — THE FOCUS VIGNETTE. As the hunter holds the sneak-look and
/// concentration builds (HunTrackerEye.FocusFraction 0..1 over the 2.5s focus delay), the
/// screen edges darken, landing full exactly as the read resolves; it eases back out when the
/// hunter stands or looks away. Cosmetic only. A single radial-gradient texture (transparent
/// centre, black corners) drawn full-screen with a rank-of-focus alpha; the current level lerps
/// toward the target so the fade is smooth, not a snap.
/// </summary>
public class HunFocusVignette : IRenderer
{
    private readonly ICoreClientAPI capi;
    private LoadedTexture? tex;
    private float current;      // eased 0..1
    private float builtReach = -1; // the reach the current texture was built for

    public HunFocusVignette(ICoreClientAPI capi)
    {
        this.capi = capi;
        BuildTexture(TcmClientSettings.VignetteReach);
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "hunfocusvignette");
    }

    public double RenderOrder => 0.4; // over the world, under the HUD text + crosshair
    public int RenderRange => 1;

    /// <summary>Radial gradient: transparent through <paramref name="reach"/> of the radius,
    /// then darkening to solid black at the corners. Higher reach = darkening hugs the edges;
    /// lower = it creeps toward centre (more tunnel).</summary>
    private void BuildTexture(float reach)
    {
        const int size = 512;
        float clear = GameMath.Clamp(reach, 0.05f, 0.95f);
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        var grad = new RadialGradient(size / 2.0, size / 2.0, size * 0.30,
                                      size / 2.0, size / 2.0, size * 0.75);
        grad.AddColorStop(0, new Color(0, 0, 0, 0));
        grad.AddColorStop(clear, new Color(0, 0, 0, 0));
        grad.AddColorStop(1, new Color(0, 0, 0, 1));
        ctx.SetSource(grad);
        ctx.Rectangle(0, 0, size, size);
        ctx.Fill();

        tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        builtReach = reach;

        grad.Dispose();
        ctx.Dispose();
        surface.Dispose();
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        // Rebuild only when the reach setting actually changed (rare: a config edit).
        if (Math.Abs(TcmClientSettings.VignetteReach - builtReach) > 0.001f)
            BuildTexture(TcmClientSettings.VignetteReach);

        float target = HunTrackerEye.FocusFraction;
        // Ease toward the target: fade-in tracks the focus build (target itself ramps over the
        // focus delay), fade-out is a soft ~0.35s glide when target snaps to 0.
        float rate = target > current ? 6f : 3f;
        current += (target - current) * Math.Min(1f, rate * dt);
        if (current <= 0.003f) { current = 0; return; }
        if (tex == null) return;

        capi.Render.Render2DTexture(tex.TextureId, 0, 0,
            capi.Render.FrameWidth, capi.Render.FrameHeight, 50f,
            new Vec4f(1, 1, 1, current * TcmClientSettings.VignetteIntensity));
    }

    public void Dispose()
    {
        tex?.Dispose();
        tex = null;
    }
}
