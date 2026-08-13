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
    public static float VignetteIntensity = 0.8f;  // max corner darkening alpha
    public static float VignetteReach = 0.2f;      // radial stop where darkening begins (0 tunnel .. 1 edges only)

    // Blood-trail vibrancy spreads: how far HUN rank swings BloodTrail's trail around its stock
    // look, which is anchored at Journeyman I. 0 = no rank effect (stock for everyone); 0.5 =
    // Novice sees half, Grandmaster one and a half. Visibility drives drop count (size rides half).
    public static float BloodVisibility = 0.5f;
    public static float BloodPersistence = 0.5f;

    // Practice toasts (the "+0.4 Mining" drift-and-fade over the hotbar). All feel values,
    // so all live here per the standing rule: tune in-game, never in a rebuild loop.
    public static bool ToastsEnabled = true;
    public static string ToastColor = "#E8C36A";   // pending-wash amber; flavor, not truth
    public static float ToastFontSize = 22f;        // unscaled GUI points
    public static float ToastLifetime = 1.6f;       // seconds from spawn to fully faded
    public static float ToastRise = 34f;            // GUI px drifted upward over the lifetime
    public static float ToastMerge = 0.7f;          // seconds: same-domain gains merge, felling is a burst verb
    public static float ToastMax = 4f;              // live toasts cap; overflow drops the oldest
    public static float ToastOffsetY = 130f;        // GUI px above the bottom edge (over the hotbar)
    public static bool ToastTechnique = false;      // append the technique name to the domain

    // Discovery banners (the heraldic ribbon for rank-ups and named knowledge earns,
    // 2026-08-08). Distinct surface from the practice toasts above: the toast is the
    // running tally, the banner is the ceremony. Own renderer, not TriggerIngameDiscovery
    // — vanilla's element has no backing and gold-on-sand was unreadable.
    public static bool DiscoveryBanners = true;
    public static float BannerOpacity = 0.8f;      // ribbon ink; lower ghosts it, 0 leaves bare text
    public static float BannerFontSize = 26f;       // unscaled GUI points, decorative face
    public static float BannerHold = 4f;            // seconds at full ink between the fades

    // Quest-step toasts (the small parchment strip with a checklist line and a check
    // dropping into its box, 2026-08-08). Fires when an earned knowledge key closes a
    // `doneWhen` step in a guide's quest block. Shares the banner's opacity and ancestry,
    // so it needs one knob of its own: on or off.
    public static bool QuestToasts = true;

    private const string FileName = "almanactcm-client.json";

    private class Data
    {
        public float focusDelay { get; set; } = 2.5f;
        public float vignetteIntensity { get; set; } = 0.8f;
        public float vignetteReach { get; set; } = 0.2f;
        public float bloodVisibility { get; set; } = 0.5f;
        public float bloodPersistence { get; set; } = 0.5f;
        public bool toastsEnabled { get; set; } = true;
        public string toastColor { get; set; } = "#E8C36A";
        public float toastFontSize { get; set; } = 22f;
        public float toastLifetime { get; set; } = 1.6f;
        public float toastRise { get; set; } = 34f;
        public float toastMerge { get; set; } = 0.7f;
        public float toastMax { get; set; } = 4f;
        public float toastOffsetY { get; set; } = 130f;
        public bool toastTechnique { get; set; } = false;
        public bool discoveryBanners { get; set; } = true;
        public float bannerOpacity { get; set; } = 0.8f;
        public float bannerFontSize { get; set; } = 26f;
        public float bannerHold { get; set; } = 4f;
        public bool questToasts { get; set; } = true;
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
            ToastsEnabled = d.toastsEnabled;
            ToastColor = string.IsNullOrWhiteSpace(d.toastColor) ? "#E8C36A" : d.toastColor.Trim();
            ToastFontSize = Math.Clamp(d.toastFontSize, 10f, 40f);
            ToastLifetime = Math.Clamp(d.toastLifetime, 0.5f, 5f);
            ToastRise = Math.Clamp(d.toastRise, 0f, 120f);
            ToastMerge = Math.Clamp(d.toastMerge, 0.1f, 3f);
            ToastMax = Math.Clamp(d.toastMax, 1f, 8f);
            ToastOffsetY = Math.Clamp(d.toastOffsetY, 40f, 400f);
            ToastTechnique = d.toastTechnique;
            DiscoveryBanners = d.discoveryBanners;
            BannerOpacity = Math.Clamp(d.bannerOpacity, 0f, 1f);
            BannerFontSize = Math.Clamp(d.bannerFontSize, 14f, 44f);
            BannerHold = Math.Clamp(d.bannerHold, 1.5f, 10f);
            QuestToasts = d.questToasts;
        }
        catch { /* malformed file: keep whatever we had */ }
    }
}
