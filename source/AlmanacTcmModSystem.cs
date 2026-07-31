using AlmanacTcm.Config;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

[assembly: ModInfo("The Almanac: Trades, Callings & Mastery", "almanactcm",
    Authors = new string[] { "Venah" },
    Description = "Identity-first trade progression for the modded world.",
    Version = "0.4.22a")]

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
    /// dependency bare ("") so X.Y.Z-dev builds satisfy it (Almanac convention).
    ///
    /// 0.1.2, because this build's guides use PACK-LEVEL `revealedBy` (alloys, smithing),
    /// which Illuminated only honours from 0.0.18. Below that the field is ignored and the
    /// chapter degrades open, so nothing breaks loudly: the earned reveal simply never
    /// happens and the chapter sits visible from the first login, which is the whole thing
    /// it exists to prevent. Pinned to 0.1.2 rather than 0.0.18 so the pair stays in step.</summary>
    private const string MinIlluminatedVersion = "0.1.2";

    /// <summary>Static access for Harmony patches, split by side (set in Start, cleared in
    /// Dispose). Singleplayer loads BOTH a client-side and a server-side ModSystem in one
    /// process, sharing these statics, and the client's Start runs last. A single shared
    /// static therefore ended up pointing at the client instance, where Ledger/Server/Affinity
    /// and the loaded GlobalConfig are null, so every server-side grant silently no-opped
    /// (the 0.4.2 singleplayer zero-XP bug). Resolve by the side the call site runs on.</summary>
    public static AlmanacTcmModSystem? ServerInstance { get; private set; }

    /// <summary>Client-side counterpart of <see cref="ServerInstance"/>: the instance that owns
    /// Client, AlloyLedger, and the synced AlloyLedgerGated flag.</summary>
    public static AlmanacTcmModSystem? ClientInstance { get; private set; }

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
        if (api.Side == EnumAppSide.Server) ServerInstance = this;
        else ClientInstance = this;
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
            Try("RAN-recovery", () => Domains.RanPatches.PatchConditional(api, harmony));
            Try("RAN-firearms", () => Domains.RanFirearmsPatches.PatchConditional(api, harmony));
            Try("MEL-block", () => Domains.MelPatches.PatchConditional(api, harmony));
            Try("MEL-parry", () => Domains.MelParryPatches.PatchConditional(api, harmony));
            Try("FAR", () => Domains.FarPatches.PatchConditional(api, harmony));
            Try("FAR-bonus", () => Domains.FarBonusPatches.PatchConditional(api, harmony));
            Try("COO", () => Domains.CooPatches.PatchConditional(api, harmony));
            Try("COO-bonus", () => Domains.CooBonusPatches.PatchConditional(api, harmony));
            Try("ANI", () => Domains.AniPatches.PatchConditional(api, harmony));
            Try("ANI-bonus", () => Domains.AniBonusPatches.PatchConditional(api, harmony));
            Try("POT", () => Domains.PotPatches.PatchConditional(api, harmony));
            Try("POT-bonus", () => Domains.PotBonusPatches.PatchConditional(api, harmony));
            Try("GLA", () => Domains.GlaPatches.PatchConditional(api, harmony));
            // BEE: the any-of conditional domain (oreki OR fgc). Must run BEFORE FAR reads
            // BeeDomain.Enabled? No: both read mod presence, not each other's patch state,
            // so order is free. The RouteBeekeeping switch is that shared presence test.
            Try("BEE", () => Domains.BeePatches.PatchConditional(api, harmony));
            Try("part-marks", () => Domains.ToolPartMarks.PatchConditional(api, harmony));
            Try("BRE", () => Domains.BrePatches.PatchConditional(api, harmony));
            Try("ALC", () => Domains.AlcPatches.PatchConditional(api, harmony));
            Try("ALC-brand", () => Domains.AlcBrandPatches.PatchConditional(api, harmony));
            Try("TAI", () => Domains.TaiPatches.PatchConditional(api, harmony));
            Try("TAI-mark", () => Domains.TaiMarkPatches.PatchConditional(api, harmony));
            Try("MAS", () => Domains.MasPatches.PatchConditional(api, harmony));
            Try("ENG", () => Domains.EngPatches.PatchConditional(api, harmony));
            Try("TEM", () => Domains.TemPatches.PatchConditional(api, harmony));
            Try("ARC", () => Domains.ArcPatches.PatchConditional(api, harmony));
            Try("HUN-bloodtrail", () => Domains.HunBloodTrailPatches.PatchConditional(api, harmony));
            Try("WOO-collider", () => Domains.WooColliderPatches.PatchAll(api, harmony));
            Try("WOO-colliersmark", () => Domains.WooColliersMark.PatchAll(api, harmony));
            Try("alloy-ledger-furnace", () => Gui.AlloyLedgerBrickFurnacePatch.Register(api, harmony));
            Try("almanac-chat", () => Engine.AlmanacChatChannel.PatchClient(api, harmony));
            TcmLog.Info(api, "Harmony patches applied (anvil, quench, mold, smelt, firepit, tooltip, gate, mining, cave-in, felling + conditionals)");
        }
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        // Clear the singleplayer material-gate override before anything can read it. Only a
        // singleplayer world's own savegame may set it (TcmCommands.RestoreGateOverride), so a
        // gate switched off in one world can never carry into the next world or into a server
        // joined without restarting the game.
        Domains.MetMaterialGate.SinglePlayerOverride = null;
        LoadGlobalConfig(sapi);
        Engine.LedgerSystem.DefaultFactories[Domains.MetDomain.Code] = Domains.MetDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.MinDomain.Code] = Domains.MinDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.WooDomain.Code] = Domains.WooDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.ForDomain.Code] = Domains.ForDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.FisDomain.Code] = Domains.FisDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.PanDomain.Code] = Domains.PanDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.HunDomain.Code] = Domains.HunDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.RanDomain.Code] = Domains.RanDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.MelDomain.Code] = Domains.MelDomain.Defaults;
        // The farm-to-table trio (one incumbent design lineage, one shared owner stamp).
        Engine.LedgerSystem.DefaultFactories[Domains.FarDomain.Code] = Domains.FarDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.CooDomain.Code] = Domains.CooDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.AniDomain.Code] = Domains.AniDomain.Defaults;
        // The fire-craft/vessel pair (POT this build; GLA next). POT is vanilla-floored and
        // day-one; its Potter's Mark is the container-side mirror of COO's Cook's Mark.
        Engine.LedgerSystem.DefaultFactories[Domains.PotDomain.Code] = Domains.PotDomain.Defaults;
        // GLA: the first fully-mod domain (dormant without glassmakingfork, RequiredMod in the
        // roster). The factory registers regardless; the domain is excluded from breadth/affinity
        // when disabled, and its verbs only patch when the mod is present.
        Engine.LedgerSystem.DefaultFactories[Domains.GlaDomain.Code] = Domains.GlaDomain.Defaults;
        // The consumables cluster. BRE grants at the seal/ignite (the online skilled act); completion-
        // time effects use the frozen rank. ALC is the vanilla-floored other half: remedy crafting is
        // always-on, the potion (alchemy) + wet-chemistry (industrialstory) layers are conditional, and
        // the Alchemist's Brand rides both product families.
        Engine.LedgerSystem.DefaultFactories[Domains.BreDomain.Code] = Domains.BreDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.AlcDomain.Code] = Domains.AlcDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.TaiDomain.Code] = Domains.TaiDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.MasDomain.Code] = Domains.MasDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.EngDomain.Code] = Domains.EngDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.TemDomain.Code] = Domains.TemDomain.Defaults;
        Engine.LedgerSystem.DefaultFactories[Domains.ArcDomain.Code] = Domains.ArcDomain.Defaults;
        // BEE: the first any-of conditional (oreki OR fgc). The factory registers regardless,
        // like GLA/ARC; the roster entry disables the domain when neither mod is present and
        // beekeeping stays FAR #10 (RULED 2026-07-30).
        Engine.LedgerSystem.DefaultFactories[Domains.BeeDomain.Code] = Domains.BeeDomain.Defaults;
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

        // RAN's rank levers: steadyAim + held-launcher reloadSpeed stamp (CO) or the vanilla
        // stat floor, reconciled on a slow tick like HUN's.
        Domains.RanPatches.RegisterServer(sapi);

        // MEL's blocking verb needs the server API for its grant; the kill verb rides the
        // shared listener already registered above.
        Domains.MelPatches.RegisterServer(sapi);
        // The parry-widen grace tracks parry closes per defender.
        Domains.MelParryPatches.RegisterServer(sapi);

        // The farm-to-table trio's server hooks (vanilla-floor verb grants, the shared trough
        // owner stamp FAR writes and ANI reads, the unattended cooking/birth completion sinks).
        // Registered after the ledger is live like every other domain.
        Domains.FarPatches.RegisterServer(sapi);
        Domains.FarBonusPatches.RegisterServer(sapi);
        Domains.CooPatches.RegisterServer(sapi);
        Domains.CooBonusPatches.RegisterServer(sapi);
        Domains.AniPatches.RegisterServer(sapi);

        // POT's zero-Harmony clayforming listener + the persisted kiln-owner and vessel-mark
        // side maps (a pit kiln fires unattended; a placed crock cannot carry its own stamp).
        Domains.PotPatches.RegisterServer(sapi);
        Domains.PotBonusPatches.RegisterServer(sapi);

        // GLA is stateless server-side (window + provenance ride the stack; no side maps), but it
        // still needs the world handle for grant contextHashes.
        Domains.GlaPatches.RegisterServer(sapi);

        // BRE's persisted seal-owner map (a seal matures across days/restarts; the frozen rank
        // drives the unattended spoilage/portion/mark effects).
        Domains.BrePatches.RegisterServer(sapi);

        // ALC's persisted owner side maps (cauldron cooks + reactions complete unattended; the herb
        // rack carries the alchemist for the perish-slow rung). The Brand read is stateless.
        Domains.AlcPatches.RegisterServer(sapi);
        Domains.AlcBrandPatches.RegisterServer(sapi);
        Domains.AlcEmphasis.RegisterServer(sapi);

        // TAI's grant patches are stateless (spin/weave/knit grant at the act); the mark read is
        // stateless too. Only the emphasis channel + the spindle-thrift world lookup need the API.
        Domains.TaiPatches.RegisterServer(sapi);
        Domains.TaiEmphasis.RegisterServer(sapi);

        // TEM's per-player stat reconcile (gear cost + stability resistance) + the Storm-Sense forecast
        // run on a 2s tick; it resolves SystemTemporalStability for the storm schedule read.
        Domains.TemPatches.RegisterServer(sapi);

        // ARC's mana re-root reconcile (freeze RBM XP; playermaxmana_rm -> ARC-rank floor) + meditation
        // trickle run on a 2s tick, gated on rustboundmagic being present (else the domain is dormant).
        Domains.ArcPatches.RegisterServer(sapi);

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
        // Same reasoning for the singleplayer gate override: joining a REMOTE server never
        // restores one (only a local world's savegame does), so clearing here guarantees a
        // client that disabled the gate in singleplayer predicts the gate normally on The Quire.
        // In singleplayer the server side re-restores it from the savegame after this runs.
        if (!capi.IsSinglePlayer) Domains.MetMaterialGate.SinglePlayerOverride = null;
        Client = new LevelingClient(capi);

        // The Surveyor's depth bands arrive on their own channel (the PT tooltip reads them).
        Domains.PanSurveyor.RegisterClient(capi);

        // ALC emphasis: the Callings book's Potent/Lasting toggle sends on this channel.
        Domains.AlcEmphasis.RegisterClient(capi);
        // TAI emphasis: the Callings book's Warm/Lasting/Cool toggle sends on this channel.
        Domains.TaiEmphasis.RegisterClient(capi);

        // Hunter's Map envelope sync — dormant with the shelved layer (see Start).
        // Domains.HunPatches.RegisterClient(capi);

        // RAN steadyAim rides the client under CO (CO registers/reads it client-side, and
        // its Register wipes server-synced values — the 0.3.113 lesson).
        Domains.RanPatches.RegisterClient(capi);

        // The Marksman's Eye lead marker (P4, amended ruling 2026-07-20): client-only,
        // meaningless without CO's aiming system, so gated on it.
        if (capi.ModLoader.IsModEnabled("combatoverhaulfork"))
            new Domains.RanMarksmansEye(capi);

        // The Duelist's Eye (MEL P4): condition read + vital-point overlay, reads CO's client
        // collider/zone data, so gated on CO.
        if (capi.ModLoader.IsModEnabled("combatoverhaulfork"))
            new Domains.MelDuelistsEye(capi);

        // Client cosmetic settings (ConfigLib GUI -> almanactcm-client.json); load before the
        // tracker + vignette so they pick up the tuned values.
        Domains.TcmClientSettings.Register(capi);

        // Practice toasts: the "+0.4 Mining" drift-and-fade over the hotbar. Fed by
        // PracticeGainPacket via LevelingClient; every feel value rides TcmClientSettings.
        Toasts = new Gui.PracticeToastRenderer(capi, Template);
        Client.PracticeGain += Toasts.OnPracticeGain;

        // The Tracker's Eye HUD (sneak + look read of live game). Client-only, reads networked
        // entity state and the local HUN rank; no server round-trip.
        new Domains.HunTrackerEye(capi);
        // The focus vignette: edges darken as concentration builds, landing full with the read.
        new Domains.HunFocusVignette(capi);

        // The Callings page lives in Illuminated's book (hard dependency, so the
        // assembly is always present; the tab API is 0.0.2+, enforced above).
        var illuminated = capi.ModLoader.GetModSystem<AlmanacIlluminated.AlmanacIlluminatedModSystem>();
        illuminated?.RegisterBookTab(new Gui.CallingsTab(Client));

        // Guide reveal provider (Illuminated 0.0.16+): sections gated revealedBy
        // "almanactcm:..." render once the matching Knowledge key is earned. Keys are
        // stored in Knowledge under their FULL "almanactcm:..." form so guide JSON and
        // store match one-to-one. Milestone detectors write the keys server-side.
        var client = Client;
        illuminated?.RegisterRevealProvider("almanactcm",
            key => client.Knowledge.TryGetValue(key, out int v) && v > 0);

        // Axis 4 Apprentice unlock: the Alloy Ledger modal. It opens on an empty-handed
        // right-click of a placed crucible (see MetSignaturePatches / the crucible interact
        // hook), gated to Apprentice+ MET on open. Held here so the interact patch can toggle it.
        AlloyLedger = new Gui.GuiDialogAlloyLedger(capi);
    }

    /// <summary>The client Alloy Ledger dialog (Axis 4 Apprentice unlock), opened from the
    /// placed-crucible interact hook. Null on the server.</summary>
    public Gui.GuiDialogAlloyLedger? AlloyLedger { get; private set; }

    /// <summary>Client-only practice toast renderer (null server-side).</summary>
    public Gui.PracticeToastRenderer? Toasts { get; private set; }

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
            if (!entry.IsEnabled(api))
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
        Toasts?.Dispose();
        Toasts = null;
        // Singleplayer disposes the two instances independently, so clear only the static
        // that points at this one (a blind null would drop the surviving side's handle).
        if (ReferenceEquals(ServerInstance, this)) ServerInstance = null;
        if (ReferenceEquals(ClientInstance, this)) ClientInstance = null;
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
