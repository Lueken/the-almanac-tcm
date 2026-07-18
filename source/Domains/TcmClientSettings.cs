using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace AlmanacTcm.Domains;

/// <summary>
/// Client-side, per-player cosmetic settings exposed through ConfigLib's in-game GUI
/// (assets/almanactcm/config/configlib-patches.json). ConfigLib owns the GUI and writes the
/// chosen values to ModConfig/almanactcm-client.json; we just read that flat file and re-read
/// it when ConfigLib fires its reload event. No compile-time dependency on ConfigLib — if it is
/// ever absent the file simply will not exist and the shipped defaults stand.
/// </summary>
public static class TcmClientSettings
{
    public static float FocusDelay = 2.5f;        // seconds of held sneak-look before the read resolves
    public static float VignetteIntensity = 0.42f; // max corner darkening alpha
    public static float VignetteReach = 0.6f;      // radial stop where darkening begins (0 tunnel .. 1 edges only)

    private const string FileName = "almanactcm-client.json";

    private class Data
    {
        public float focusDelay { get; set; } = 2.5f;
        public float vignetteIntensity { get; set; } = 0.42f;
        public float vignetteReach { get; set; } = 0.6f;
    }

    public static void Register(ICoreClientAPI capi)
    {
        Load(capi);
        // ConfigLib re-broadcasts this whenever a setting changes in its menu.
        capi.Event.RegisterEventBusListener(
            (string name, ref EnumHandling h, IAttribute data) => Load(capi), 0.5, "configlib:config-reload");
    }

    private static void Load(ICoreClientAPI capi)
    {
        try
        {
            var d = capi.LoadModConfig<Data>(FileName);
            if (d == null) return; // ConfigLib not installed / file not written yet: keep defaults
            FocusDelay = Math.Max(0f, d.focusDelay);
            VignetteIntensity = d.vignetteIntensity;
            VignetteReach = d.vignetteReach;
        }
        catch { /* malformed file: keep whatever we had */ }
    }
}
