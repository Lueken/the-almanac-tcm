using AlmanacTcm.Config;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

[assembly: ModInfo("The Almanac: Trades, Callings & Mastery", "almanactcm",
    Authors = new string[] { "Venah" },
    Description = "Identity-first trade progression for the modded world.",
    Version = "0.3.112-dev")]

namespace AlmanacTcm;

/// <summary>
/// Engine spine for The Almanac: Trades, Callings & Mastery. Owns the day
/// ledger, the 3am consolidation, rank state, and the domain registries.
/// Build order per docs/design/build-track-1.md (T1.0 scaffold → T1.1 vendored
/// substrate → T1.2 ledger engine → T1.3 affinity → T1.4 MET pilot).
/// </summary>
public class AlmanacTcmModSystem : ModSystem
{
    /// <summary>Minimum sibling version enforced at runtime; modinfo declares the
    /// dependency bare ("") so X.Y.Z-dev builds satisfy it (Almanac convention).</summary>
    private const string MinIlluminatedVersion = "0.0.13";

    /// <summary>Static access for Harmony patches (set in Start, cleared in Dispose).</summary>
    public static AlmanacTcmModSystem? Instance { get; private set; }

    /// <summary>Static: one patch pass per process (singleplayer runs Start for both
    /// sides in one process; tooltip patches must exist client-side too).</summary>
    private static HarmonyLib.Harmony? harmony;

    public TcmGlobalConfig GlobalConfig { get; private set; } = new();

    /// <summary>The domain registry. Populated (all 21 domains, conditionals marked
    /// Enabled=false when their mod is absent) before any player joins.</summary>
    public DomainSetTemplate Template { get; } = new();

    public LevelingServer? Server { get; private set; }
    public LevelingClient? Client { get; private set; }
    public Engine.LedgerSystem? Ledger { get; private set; }
    public Engine.AffinitySystem? Affinity { get; private set; }
    public Engine.TcmCommands? Commands { get; private set; }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        Instance = this;
        EnforceSiblingVersions(api);
        RegisterDomains(api);

        // The worked-ground overlay (FOR Memory + FIS Read) rides the vanilla map system's own
        // per-player data channel; registered both sides like every MapLayer.
        api.ModLoader.GetModSystem<Vintagestory.GameContent.WorldMapManager>()
            ?.RegisterMapLayer<Overlay.AlmanacSpotsLayer>("almanacworkedground", 0.7);

        // HUN Phase 3 — the Hunter's Map: SHELVED 2026-07-18, see source/Overlay/HuntersMapLayer.cs
        // for the state of play. The layer works end to end but two problems remain unsolved: the
        // painted country is cut at a hard border on the leading edge of the view, and four merged
        // species blanket most of the map. Re-enable this line to bring it back.
        // api.ModLoader.GetModSystem<Vintagestory.GameContent.WorldMapManager>()
        //     ?.RegisterMapLayer<Overlay.HuntersMapLayer>("almanachuntersmap", 0.75);

        if (harmony == null)
        {
            harmony = new HarmonyLib.Harmony("almanactcm");
            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());

            // Each conditional patch is isolated: a single bad mod-seam (an ambiguous match, a
            // renamed type) must WARN and skip its own domain, never abort Start and half-load
            // the mod (0.3.85 lesson: an ambiguous BESnare.OnInteract match crashed the whole
            // mod client+server and ping-timed-out every join).
            void Try(string label, System.Action patch)
            {
                try { patch(); }
                catch (System.Exception e)
                {
                    TcmLog.Error(api, $"conditional patch '{label}' failed ({e.Message}); that domain's seam is inactive, rest of the mod loads");
                }
            }

            Try("MET-conditional", () => Domains.MetConditionalPatches.PatchAllPresent(api, harmony));
            Try("MET-gate", () => Domains.MetGatePatches.PatchConditional(api, harmony));
            Try("MET-signature", () => Domains.MetSignaturePatches.PatchConditional(api, harmony));
            Try("MIN-conditional", () => Domains.MinConditionalPatches.PatchAllPresent(api, harmony));
            Try("WOO-fallingtree", () => Domains.WooFallingTreePatches.PatchConditional(api, harmony));
            Try("WOO-idg", () => Domains.WooIdgPatches.PatchConditional(api, harmony));
            Try("WOO-iw", () => Domains.WooIwPatches.PatchConditional(api, harmony));
            Try("FOR-aca", () => Domains.ForAcaPatches.PatchConditional(api, harmony));
            Try("FIS-ps", () => Domains.FisPsPatches.PatchConditional(api, harmony));
            Try("FIS-trap", () => Domains.FisTrapPatches.PatchConditional(api, harmony));
            Try("FIS-ecology", () => Domains.FisEcologyPatches.PatchConditional(api, harmony));
            Try("PAN", () => Domains.PanPatches.PatchConditional(api, harmony));
            Try("PAN-surveyor", () => Domains.PanSurveyor.PatchConditional(api, harmony));
            Try("HUN", () => Domains.HunPatches.PatchConditional(api, harmony));
            Try("HUN-bloodtrail", () => Domains.HunBloodTrailPatches.PatchConditional(api, harmony));
            Try("WOO-collider", () => Domains.WooColliderPatches.PatchAll(api, harmony));
            Try("WOO-colliersmark", () => Domains.WooColliersMark.PatchAll(api, harmony));
            Try("alloy-ledger-furnace", () => Gui.AlloyLedgerBrickFurnacePatch.Register(api, harmony));
            TcmLog.Info(api, "Harmony patches applied (anvil, quench, mold, smelt, firepit, tooltip, gate, mining, cave-in, felling + conditionals)");
        }
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        LoadGlobalConfig(sapi);
        Engine.LedgerSystem.DefaultFactories[Domains.MetDomain.Code] = Domains.MetDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.MinDomain.Code] = Domains.MinDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.WooDomain.Code] = Domains.WooDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.ForDomain.Code] = Domains.ForDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.FisDomain.Code] = Domains.FisDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.PanDomain.Code] = Domains.PanDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.HunDomain.Code] = Domains.HunDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.RanDomain.Code] = Domains.RanDomain.Defaults;
        Server = new LevelingServer(sapi, Template);
        Ledger = new Engine.LedgerSystem(sapi, GlobalConfig, Template, Server);
        Affinity = new Engine.AffinitySystem(sapi, Server, Ledger);
        Commands = new Engine.TcmCommands(sapi, this);

        // MIN's zero-Harmony server hooks (knapping practice, oreDropRate reconcile) need
        // the ledger live, so they register after it — the vanilla mining/cave-in patches
        // were already applied in Start via PatchAll.
        Domains.MinPatches.RegisterServer(sapi);

        // FOR's zero-Harmony server hooks (harvest event listener, the two-stat yield reconcile,
        // the persisted novel-finds + tapline-owner state) register after the ledger is live.
        Domains.ForPatches.RegisterServer(sapi);

        // FIS trap owners persist in a side map (no trap BE stores an owner); needs the server
        // API for its save file.
        Domains.FisTrapPatches.RegisterServer(sapi);

        // The single fish population needs the server calendar for its gradual-recovery tick
        // (the vanilla restore patch no-ops until this runs).
        Domains.FisEcologyPatches.RegisterServer(sapi);

        // PAN's pan-yield stat rides vanilla's own DropModbyStat path; the stat name is
        // injected onto the parsed drop table once the world is running. The Surveyor's depth
        // store + sync channel register alongside it.
        Domains.PanPatches.RegisterServer(sapi);
        Domains.PanSurveyor.RegisterServer(sapi);

        // HUN's kill event + species ledger (Phase 3 map fuel), stat reconcile, and trap-owner
        // side map register once the ledger is live.
        Domains.HunPatches.RegisterServer(sapi);

        // The shared MEL/RAN combat kill listener (death hook + bleed last-attacker store).
        // RAN grants this build; the MEL branch goes live when MelDomain registers.
        Domains.MelRanKillPatches.RegisterServer(sapi);

        // The Collier's Mark keeps a small persisted pos->collier map (the charcoal pile is a
        // BE-less block, so it has nowhere to carry provenance itself). Needs the server API for
        // its save file, so it registers here rather than in Start.
        Domains.WooColliersMark.RegisterServer(sapi);

        // The shared IM stamina hook (one patch on VigorHook.TryConsume) scales per tool via
        // this map: Pickaxe→MIN, Axe→WOO. Registered here so both domains' knobs are live.
        Domains.MinConditionalPatches.ToolFactor[EnumTool.Pickaxe] = p =>
            Domains.MinDomain.RankLinear(Domains.MinDomain.LevelOf(p),
                Domains.MinDomain.Knob(Domains.MinDomain.StaminaUntrained, 1.3),
                Domains.MinDomain.Knob(Domains.MinDomain.StaminaGm, 0.7));
        Domains.MinConditionalPatches.ToolFactor[EnumTool.Axe] = p =>
            Domains.WooDomain.RankLinear(Domains.WooDomain.LevelOf(p),
                Domains.WooDomain.Knob(Domains.WooDomain.StaminaUntrained, 1.15),
                Domains.WooDomain.Knob(Domains.WooDomain.StaminaGm, 0.85));

        TcmLog.Cat(sapi, TcmLog.Config,
            $"engine config: consolidationHour={GlobalConfig.ConsolidationHour}, " +
            $"gmDomainCap={GlobalConfig.GmDomainCap}, lambdaDeath={GlobalConfig.LambdaDeath}, " +
            $"sigma={GlobalConfig.Sigma}; {Template.Count} domains registered");
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        // Client receives synced STATE only (rank, pending %); engine constants
        // stay server-side by design (build-track-1 T1.0 hidden-values rule).
        // Reset client-synced flags to their SAFE default first, so a value carried over
        // from a previous server can't linger into this one before its join packet lands.
        AlloyLedgerGated = true;
        Client = new LevelingClient(capi);

        // The Surveyor's depth bands arrive on their own channel (the PT tooltip reads them).
        Domains.PanSurveyor.RegisterClient(capi);

        // Hunter's Map envelope sync — dormant with the shelved layer (see Start).
        // Domains.HunPatches.RegisterClient(capi);

        // Client cosmetic settings (ConfigLib GUI -> almanactcm-client.json); load before the
        // tracker + vignette so they pick up the tuned values.
        Domains.TcmClientSettings.Register(capi);

        // The Tracker's Eye HUD (sneak + look read of live game). Client-only, reads networked
        // entity state and the local HUN rank; no server round-trip.
        new Domains.HunTrackerEye(capi);
        // The focus vignette: edges darken as concentration builds, landing full with the read.
        new Domains.HunFocusVignette(capi);

        // The Callings page lives in Illuminated's book (hard dependency, so the
        // assembly is always present; the tab API is 0.0.2+, enforced above).
        capi.ModLoader.GetModSystem<AlmanacIlluminated.AlmanacIlluminatedModSystem>()
            ?.RegisterBookTab(new Gui.CallingsTab(Client));

        // Axis 4 Apprentice unlock: the Alloy Ledger modal. It opens on an empty-handed
        // right-click of a placed crucible (see MetSignaturePatches / the crucible interact
        // hook), gated to Apprentice+ MET on open. Held here so the interact patch can toggle it.
        AlloyLedger = new Gui.GuiDialogAlloyLedger(capi);
    }

    /// <summary>The client Alloy Ledger dialog (Axis 4 Apprentice unlock), opened from the
    /// placed-crucible interact hook. Null on the server.</summary>
    public Gui.GuiDialogAlloyLedger? AlloyLedger { get; private set; }

    /// <summary>Client mirror of the server's <see cref="Config.TcmGlobalConfig.AlloyLedgerGated"/>,
    /// synced on join. Defaults to the ruled gated state until the server says otherwise.</summary>
    public bool AlloyLedgerGated { get; set; } = true;

    private void RegisterDomains(ICoreAPI api)
    {
        // The full roster registers on BOTH sides in DomainRoster order so packet
        // ids line up. Only wired domains grant practice (MET so far); the rest
        // exist as rank state — visible on the Callings page, ready for hooks.
        foreach (Domains.DomainRoster.Entry entry in Domains.DomainRoster.All)
        {
            Domain domain = new(entry.Code, entry.DisplayName);
            if (entry.RequiredMod != null && !api.ModLoader.IsModEnabled(entry.RequiredMod))
            {
                domain.Enabled = false;
            }
            Template.AddDomain(domain);
        }
    }

    private void LoadGlobalConfig(ICoreServerAPI sapi)
    {
        try
        {
            GlobalConfig = sapi.LoadModConfig<TcmGlobalConfig>("almanactcm/global.json") ?? new TcmGlobalConfig();
        }
        catch (System.Exception e)
        {
            TcmLog.Error(sapi, $"global.json unreadable ({e.Message}) — using defaults, NOT overwriting the broken file");
            GlobalConfig = new TcmGlobalConfig();
            TcmLog.Verbose = GlobalConfig.VerboseDebugLogging;
            return;
        }

        sapi.StoreModConfig(GlobalConfig, "almanactcm/global.json");
        TcmLog.Verbose = GlobalConfig.VerboseDebugLogging;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("almanactcm");
        harmony = null;
        Instance = null;
        base.Dispose();
    }

    private void EnforceSiblingVersions(ICoreAPI api)
    {
        Mod? illuminated = api.ModLoader.GetMod("almanacilluminated");
        if (illuminated == null)
        {
            // modinfo dependency already blocks loading without it; this guard only
            // reports a version shortfall, which modinfo's bare "" cannot express.
            return;
        }

        // Almanac dev-dep convention: X.Y.Z-dev SATISFIES an X.Y.Z requirement, so
        // compare the release part only (GameVersion treats -dev as lower).
        string releasePart = illuminated.Info.Version.Split('-')[0];
        if (GameVersion.IsLowerVersionThan(releasePart, MinIlluminatedVersion))
        {
            TcmLog.Error(api,
                $"almanacilluminated {illuminated.Info.Version} is below the required {MinIlluminatedVersion}; " +
                "update The Almanac: Illuminated");
        }
    }
}
