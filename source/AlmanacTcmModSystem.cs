using AlmanacTcm.Config;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

[assembly: ModInfo("The Almanac: Trades, Callings & Mastery", "almanactcm",
    Authors = new string[] { "Venah" },
    Description = "Identity-first trade progression for the modded world.",
    Version = "0.3.10-dev")]

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
    private const string MinIlluminatedVersion = "0.0.10";

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

        if (harmony == null)
        {
            harmony = new HarmonyLib.Harmony("almanactcm");
            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            Domains.MetConditionalPatches.PatchAllPresent(api, harmony);
            TcmLog.Info(api, "Harmony patches applied (anvil, quench, mold, smelt, firepit, tooltip + conditionals)");
        }
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        LoadGlobalConfig(sapi);
        Engine.LedgerSystem.DefaultFactories[Domains.MetDomain.Code] = Domains.MetDomain.Defaults;
        Server = new LevelingServer(sapi, Template);
        Ledger = new Engine.LedgerSystem(sapi, GlobalConfig, Template, Server);
        Affinity = new Engine.AffinitySystem(sapi, Server, Ledger);
        Commands = new Engine.TcmCommands(sapi, this);

        TcmLog.Cat(sapi, TcmLog.Config,
            $"engine config: consolidationHour={GlobalConfig.ConsolidationHour}, " +
            $"gmDomainCap={GlobalConfig.GmDomainCap}, lambdaDeath={GlobalConfig.LambdaDeath}, " +
            $"sigma={GlobalConfig.Sigma}; {Template.Count} domains registered");
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        // Client receives synced STATE only (rank, pending %); engine constants
        // stay server-side by design (build-track-1 T1.0 hidden-values rule).
        Client = new LevelingClient(capi);

        // The Callings page lives in Illuminated's book (hard dependency, so the
        // assembly is always present; the tab API is 0.0.2+, enforced above).
        capi.ModLoader.GetModSystem<AlmanacIlluminated.AlmanacIlluminatedModSystem>()
            ?.RegisterBookTab(new Gui.CallingsTab(Client));
    }

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
