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

    // Blood-trail vibrancy spreads: how far HUN rank swings BloodTrail's trail around its stock
    // look, which is anchored at Journeyman I. 0 = no rank effect (stock for everyone); 0.5 =
    // Novice sees half, Grandmaster one and a half. Visibility drives drop count (size rides half).
    public static float BloodVisibility = 0.5f;
    public static float BloodPersistence = 0.5f;

    private const string FileName = "almanactcm-client.json";

    private class Data
    {
        public float focusDelay { get; set; } = 2.5f;
        public float vignetteIntensity { get; set; } = 0.42f;
        public float vignetteReach { get; set; } = 0.6f;
        public float bloodVisibility { get; set; } = 0.5f;
        public float bloodPersistence { get; set; } = 0.5f;
    }

    public static void Register(ICoreClientAPI capi)
    {
        Load(capi);
        // ConfigLib pushes "configlib:{domain}:config-saved" (ConfigLibModSystem.OnConfigSaved) the
        // moment its menu saves a setting, so we re-read live. NOTE: "configlib:config-reload" is the
        // INBOUND event ConfigLib itself listens to; broadcasting on it never fires our read (the bug
        // that left every slider stuck on its default until a full relaunch).
        capi.Event.RegisterEventBusListener(
            (string name, ref EnumHandling h, IAttribute data) => Load(capi), 0.5, "configlib:almanactcm:config-saved");
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
            BloodVisibility = Math.Max(0f, d.bloodVisibility);
            BloodPersistence = Math.Max(0f, d.bloodPersistence);
        }
        catch { /* malformed file: keep whatever we had */ }
    }
}
