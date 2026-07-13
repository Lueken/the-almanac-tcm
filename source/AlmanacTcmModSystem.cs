using AlmanacTcm.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

[assembly: ModInfo("The Almanac: Trades, Callings & Mastery", "almanactcm",
    Authors = new string[] { "Venah" },
    Description = "Identity-first trade progression for the modded world.",
    Version = "0.1.0-dev")]

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
    private const string MinIlluminatedVersion = "0.0.1";

    public TcmGlobalConfig GlobalConfig { get; private set; } = new();

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        EnforceSiblingVersions(api);
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        LoadGlobalConfig(sapi);
        TcmLog.Cat(sapi, TcmLog.Config,
            $"engine config: consolidationHour={GlobalConfig.ConsolidationHour}, " +
            $"gmDomainCap={GlobalConfig.GmDomainCap}, lambdaDeath={GlobalConfig.LambdaDeath}, " +
            $"sigma={GlobalConfig.Sigma}");
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        // Client receives synced STATE only (rank, pending %); engine constants
        // stay server-side by design (build-track-1 T1.0 hidden-values rule).
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

    private void EnforceSiblingVersions(ICoreAPI api)
    {
        Mod? illuminated = api.ModLoader.GetMod("almanacilluminated");
        if (illuminated == null)
        {
            // modinfo dependency already blocks loading without it; this guard only
            // reports a version shortfall, which modinfo's bare "" cannot express.
            return;
        }

        if (GameVersion.IsLowerVersionThan(illuminated.Info.Version, MinIlluminatedVersion))
        {
            TcmLog.Error(api,
                $"almanacilluminated {illuminated.Info.Version} is below the required {MinIlluminatedVersion}; " +
                "update The Almanac: Illuminated");
        }
    }
}
