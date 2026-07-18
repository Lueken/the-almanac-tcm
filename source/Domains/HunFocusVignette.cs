using System;
using System.Runtime.InteropServices;
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

    // MUST be ~0.95, not a low value. Render2DTexture(Premultiplied) sets uniforms on the
    // engine's guiShaderProg and draws WITHOUT calling Use() — it assumes that shader is already
    // the bound program, which only becomes true late in the Ortho stage once the GUI pass binds
    // it. At a low RenderOrder (the 0.3.90-0.3.92 bug) the vignette runs before that bind, so the
    // quad draws with the wrong/no shader and paints nothing. 0.95 matches VS's own sleep overlay.
    public double RenderOrder => 0.95;
    public int RenderRange => 1;

    private float logAccum; // throttles the verbose draw-trace to ~once every 2s

    /// <summary>Radial gradient from dead centre out to the corner: transparent through
    /// <paramref name="reach"/> of the half-diagonal, then darkening to solid black at the
    /// corners. Higher reach = darkening hugs the very edges; lower = it creeps toward centre
    /// (more tunnel). The gradient MUST start at centre (r=0), not partway out — otherwise its
    /// clear core blankets the whole visible screen cross and only the extreme corner pixels
    /// darken, which reads as nothing on a normal display (the 0.3.91 invisible-vignette bug).</summary>
    private void BuildTexture(float reach)
    {
        const int size = 512;
        float clear = GameMath.Clamp(reach, 0.05f, 0.9f);
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        // Outer radius 0.72*size ~ the square's half-diagonal, so the four corners land on full
        // black and the clear stop falls inside the visible frame instead of beyond it.
        var grad = new RadialGradient(size / 2.0, size / 2.0, 0,
                                      size / 2.0, size / 2.0, size * 0.72);
        grad.AddColorStop(0, new Color(0, 0, 0, 0));
        grad.AddColorStop(clear, new Color(0, 0, 0, 0));
        grad.AddColorStop(1, new Color(0, 0, 0, 1));
        ctx.SetSource(grad);
        ctx.Rectangle(0, 0, size, size);
        ctx.Fill();

        // CRITICAL: commit the drawing to the surface's pixel buffer BEFORE uploading.
        // LoadOrUpdateCairoTexture reads surface.DataPtr directly and never flushes, so a
        // still-buffered Context leaves the backing buffer all-zero and the texture uploads
        // fully transparent (the 0.3.90-0.3.93 invisible-vignette bug: the draw executed with a
        // valid tex id and alpha, but composited nothing). Every working Cairo-texture path in the
        // codebase disposes/flushes the Context before upload (IconOverlayDialog, TextTextureUtil);
        // this one uploaded first. Flush, then dispose the Context, THEN upload.
        surface.Flush();

        // DIAG: sample the real pixels of the built surface so we can tell blank-texture from
        // render-path failure. Cairo Argb32 is stored BGRA premultiplied. Corner should be near
        // opaque black (A high), centre fully transparent (A 0).
        try
        {
            IntPtr p = surface.DataPtr;
            int stride = surface.Stride;
            string Px(int x, int y)
            {
                int i = y * stride + x * 4;
                return $"B{Marshal.ReadByte(p, i)} G{Marshal.ReadByte(p, i + 1)} R{Marshal.ReadByte(p, i + 2)} A{Marshal.ReadByte(p, i + 3)}";
            }
            TcmLog.Cat(capi, "hun", $"vignette tex build: reach={reach:0.00} stride={stride} corner(4,4)=[{Px(4, 4)}] mid(256,4)=[{Px(size / 2, 4)}] centre(256,256)=[{Px(size / 2, size / 2)}]");
        }
        catch (Exception e) { TcmLog.Warn(capi, $"vignette tex sample failed: {e.Message}"); }

        grad.Dispose();
        ctx.Dispose();

        tex = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
        builtReach = reach;

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

        // Throttled draw-trace: confirms the renderer runs, the focus signal, and the drawn alpha.
        logAccum += dt;
        if (logAccum >= 2f)
        {
            logAccum = 0;
            TcmLog.Cat(capi, "hun",
                $"vignette draw: focus={HunTrackerEye.FocusFraction:0.00} current={current:0.00} " +
                $"alpha={current * TcmClientSettings.VignetteIntensity:0.00} reach={builtReach:0.00} tex={tex.TextureId}");
        }

        // The texture is Cairo-sourced (premultiplied alpha), so it MUST go through the
        // premultiplied render path — the plain Render2DTexture path silently fails to paint in
        // this HUD-overlay stage (same lesson as Codex Illuminated's IconOverlayDialog).
        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, 0, 0,
            capi.Render.FrameWidth, capi.Render.FrameHeight, 50f,
            new Vec4f(1, 1, 1, current * TcmClientSettings.VignetteIntensity));
    }

    public void Dispose()
    {
        tex?.Dispose();
        tex = null;
    }
}
