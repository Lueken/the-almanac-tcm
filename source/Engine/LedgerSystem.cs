using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AlmanacTcm.Config;
using AlmanacTcm.Leveling;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlmanacTcm.Engine;

/// <summary>
/// The day-ledger engine (xp-engine-design.md §§2-6): listeners log practice
/// events here; nothing touches banked skill state until the 3am boundary
/// consolidation flushes through LevelingServer.AddExperience. Death scatters
/// pending practice (λ) and can never reach banked XP.
/// </summary>
public class LedgerSystem
{
    private readonly ICoreServerAPI sapi;
    private readonly TcmGlobalConfig config;
    private readonly DomainSetTemplate template;
    private readonly LevelingServer leveling;

    /// <summary>Per-domain engine configs, loaded server-side only (hidden-values rule).</summary>
    public Dictionary<string, DomainConfig> DomainConfigs { get; } = new();

    /// <summary>Domain modules register their config seeds here before StartServerSide;
    /// used only when no ModConfig file exists yet.</summary>
    public static readonly Dictionary<string, System.Func<DomainConfig>> DefaultFactories = new();

    /// <summary>Effective per-technique raw values with ifModPresent scaling pre-applied
    /// (mod presence is static per session).</summary>
    private readonly Dictionary<string, Dictionary<string, (double raw, double k)>> effective = new();

    /// <summary>All known ledgers, keyed by player UID (kept across relogs).</summary>
    private readonly Dictionary<string, PracticeLedger> ledgers = new();

    /// <summary>T1.3 plugs the affinity ceiling in here; default = no ceiling.</summary>
    public System.Func<IPlayer, Domain, int> ClassCeilingProvider { get; set; }
        = (player, domain) => domain.MaxLevel;

    /// <summary>Affinity Smax scaling (±20% envelope, §10.5); default = neutral.</summary>
    public System.Func<IPlayer, Domain, double> SmaxScaleProvider { get; set; }
        = (player, domain) => 1.0;

    private string LedgerFileName
    {
        get
        {
            string invalidChars = new string(Path.GetInvalidFileNameChars());
            Regex regex = new(string.Format("[{0}]", Regex.Escape(invalidChars)));
            string str = sapi.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", regex.Replace(str, "") + "-ledger.json");
        }
    }

    public LedgerSystem(ICoreServerAPI sapi, TcmGlobalConfig config, DomainSetTemplate template, LevelingServer leveling)
    {
        this.sapi = sapi;
        this.config = config;
        this.template = template;
        this.leveling = leveling;

        LoadDomainConfigs();
        LoadLedgers();

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerDeath += OnPlayerDeath;
        sapi.Event.GameWorldSave += SaveLedgers;
        sapi.Event.RegisterGameTickListener(OnEngineTick, 5000);
    }

    private void LoadDomainConfigs()
    {
        foreach (Domain domain in template.Domains)
        {
            string path = $"almanactcm/{domain.Code}.json";
            DomainConfig? domainConfig = null;
            try
            {
                domainConfig = sapi.LoadModConfig<DomainConfig>(path);
            }
            catch (Exception e)
            {
                TcmLog.Error(sapi, $"{path} unreadable ({e.Message}) — using defaults, NOT overwriting");
            }

            if (domainConfig == null)
            {
                domainConfig = DefaultFactories.TryGetValue(domain.Code, out var factory)
                    ? factory()
                    : new DomainConfig { Code = domain.Code };
                sapi.StoreModConfig(domainConfig, path);
            }
            else if (DefaultFactories.TryGetValue(domain.Code, out var factory))
            {
                // Migration: newly shipped techniques/knobs join an EXISTING config
                // file additively; values the server already tuned are never touched.
                DomainConfig defaults = factory();
                bool changed = false;
                foreach (var (name, tech) in defaults.Techniques)
                {
                    if (!domainConfig.Techniques.ContainsKey(name))
                    {
                        domainConfig.Techniques[name] = tech;
                        changed = true;
                        TcmLog.Cat(sapi, TcmLog.Config, $"{domain.Code}: new technique '{name}' merged into config");
                    }
                }
                foreach (var (knob, value) in defaults.Bonus)
                {
                    if (!domainConfig.Bonus.ContainsKey(knob))
                    {
                        domainConfig.Bonus[knob] = value;
                        changed = true;
                    }
                }
                if (changed) sapi.StoreModConfig(domainConfig, path);
            }
            DomainConfigs[domain.Code] = domainConfig;
            try
            {
                domain.SetTierTotals(domainConfig.TierTotals);
            }
            catch (ArgumentException e)
            {
                // A bad config value must never kill the mod at boot — degrade to
                // the built-in pacing table and say so loudly.
                TcmLog.Error(sapi, $"{domain.Code} TierTotals invalid ({e.Message}) — using built-in defaults");
            }

            var effectiveTechs = new Dictionary<string, (double raw, double k)>();
            foreach (var (name, tech) in domainConfig.Techniques)
            {
                double raw = tech.Raw;
                if (tech.IfModPresent != null && sapi.ModLoader.IsModEnabled(tech.IfModPresent))
                {
                    raw *= tech.RawScale;
                }
                effectiveTechs[name] = (raw, tech.K);
            }
            effective[domain.Code] = effectiveTechs;

            TcmLog.Cat(sapi, TcmLog.Config,
                $"domain {domain.Code}: {domainConfig.Techniques.Count} techniques, m={domainConfig.M}, " +
                $"adjacency=[{string.Join(",", domainConfig.Adjacency)}]");
        }
    }

    // ------------------------------------------------------------------ logging

    /// <summary>The one entry point for listeners. Raw value and K come from server
    /// config, never the caller; identical contexts inside the dedup window log zero
    /// (place-and-rebreak guard); first contact reveals a hidden domain.
    /// <paramref name="rawMultiplier"/> scales the config raw for THIS action only —
    /// the seam for ruled per-action quality scaling (MIN Q5: ore rarity/depth), never
    /// a way to inject a caller-chosen base (the base still lives in config).</summary>
    public void Log(IPlayer player, string domainCode, string technique, int contextHash, double rawMultiplier = 1.0)
    {
        Domain? domain = template.FindDomain(domainCode);
        if (domain == null || !domain.Enabled) return;

        PracticeLedger ledger = LedgerFor(player);

        PlayerDomainSet? domainSet = leveling.GetDomainSet(player);
        PlayerDomain? playerDomain = domainSet?[domain.Id];
        if (playerDomain == null) return;
        // Maxed out: a domain at its terminal rank (Grandmaster) has nothing left to earn, so
        // skip all practice accounting for it — no accumulator writes, no dedup ring, no
        // consolidation work. This is the per-action hot path, so the guard pays for itself.
        if (playerDomain.Level >= playerDomain.Domain.MaxLevel) return;
        if (playerDomain.Hidden) leveling.RevealDomain(player, domain.Id);

        double raw = 1.0, k = 50.0;
        if (effective.TryGetValue(domainCode, out var techs) && techs.TryGetValue(technique, out var e))
        {
            (raw, k) = e;
        }
        else
        {
            TcmLog.Warn(sapi, $"unconfigured technique {domainCode}/{technique} — using raw=1, K=50");
        }

        if (rawMultiplier != 1.0) raw = System.Math.Max(0, raw * rawMultiplier);

        bool duplicate = IsDuplicateContext(ledger, domainCode, technique, contextHash);
        if (duplicate) raw = 0;

        if (raw > 0)
        {
            var accs = ledger.AccumulatorsFor(domainCode);
            accs.TryGetValue(technique, out double x);
            accs[technique] = x + raw;
            TcmLog.Cat(sapi, TcmLog.Ledger,
                $"{player.PlayerName} {domainCode}/{technique} +{raw:0.##} -> x={accs[technique]:0.##}");

            if (config.PracticeGainMessages)
            {
                SendInfoLine(player, "almanactcm:practice-gain",
                    domain.DisplayName, technique, raw, accs[technique]);
            }
        }
        else if (duplicate && config.PracticeGainMessages)
        {
            SendInfoLine(player, "almanactcm:practice-repeat", domain.DisplayName, technique);
        }
    }

    /// <summary>Practice feedback to the client's Info tab (trial instrumentation;
    /// PracticeGainMessages turns it off when the novelty wears thin).</summary>
    private void SendInfoLine(IPlayer player, string langKey, params object[] args)
    {
        if (player is not IServerPlayer serverPlayer) return;
        serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup,
            Lang.GetL(serverPlayer.LanguageCode, langKey, args), EnumChatType.Notification);
    }

    private bool IsDuplicateContext(PracticeLedger ledger, string domain, string technique, int contextHash)
    {
        long now = sapi.World.ElapsedMilliseconds;
        long windowMs = (long)(config.DedupWindowSeconds * 1000);

        bool duplicate = false;
        foreach (var entry in ledger.DedupRing)
        {
            if (entry.domain == domain && entry.technique == technique
                && entry.contextHash == contextHash && now - entry.elapsedMs < windowMs)
            {
                duplicate = true;
                break;
            }
        }
        ledger.DedupRing.Enqueue((domain, technique, contextHash, now));
        while (ledger.DedupRing.Count > config.DedupRingSize) ledger.DedupRing.Dequeue();
        return duplicate;
    }

    public PracticeLedger LedgerFor(IPlayer player)
    {
        if (!ledgers.TryGetValue(player.PlayerUID, out PracticeLedger? ledger))
        {
            // A brand-new ledger anchors to the current boundary: no phantom
            // back-consolidations for first-time players.
            ledger = new PracticeLedger { LastConsolidatedBoundary = CurrentBoundary() };
            ledgers[player.PlayerUID] = ledger;
        }
        return ledger;
    }

    private long CurrentBoundary()
        => SaturationMath.BoundaryIndex(sapi.World.Calendar.TotalDays, config.ConsolidationHour);

    // ------------------------------------------------------------- consolidation

    private void OnEngineTick(float dt)
    {
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            TryConsolidate(player);
        }
    }

    private void OnPlayerNowPlaying(IServerPlayer byPlayer)
    {
        // Offline boundaries collapse into exactly ONE consolidation at login.
        TryConsolidate(byPlayer);
    }

    /// <summary>Boundary-keyed and idempotent: no-ops unless the calendar-derived
    /// index advanced past the ledger's. Login/logout/death never trigger this,
    /// so splitting a day across sessions can never beat the concave curve.</summary>
    public void TryConsolidate(IPlayer player)
    {
        PracticeLedger ledger = LedgerFor(player);
        long current = CurrentBoundary();
        if (current <= ledger.LastConsolidatedBoundary) return;

        PlayerDomainSet? domainSet = leveling.GetDomainSet(player);
        if (domainSet == null) return;

        Consolidate(player, domainSet, ledger, current);
    }

    private void Consolidate(IPlayer player, PlayerDomainSet domainSet, PracticeLedger ledger, long boundary)
    {
        // Pass 1 — each domain's own banked value (phase rules) + per-technique
        // banked for co-grant fan-out and dominant-technique history.
        Dictionary<string, double> primaryBanked = new();
        Dictionary<string, double> totals = new();

        foreach (Domain domain in template.Domains)
        {
            if (!domain.Enabled) continue;
            if (!ledger.Accumulators.TryGetValue(domain.Code, out var accs) || accs.Count == 0) continue;

            DomainConfig dc = DomainConfigs[domain.Code];
            PlayerDomain? playerDomain = domainSet[domain.Id];
            if (playerDomain == null) continue;

            bool depthPhase = playerDomain.Level >= JourneymanEntry;
            string? dominant = depthPhase
                ? ledger.DominantTechnique(domain.Code, boundary, config.DominantWindowDays)
                : null;
            System.Func<string, double> kOf = t =>
                effective[domain.Code].TryGetValue(t, out var e) ? e.k : 50.0;

            double smax = dc.Smax * SmaxScaleProvider(player, domain);
            double banked = depthPhase
                ? SaturationMath.DepthBanked(accs, kOf, smax, dominant, config.DepthOffTechniqueWeight)
                : SaturationMath.BreadthBanked(accs, kOf, smax, dc.M);

            primaryBanked[domain.Code] = banked;
            totals.TryGetValue(domain.Code, out double t0);
            totals[domain.Code] = t0 + banked;

            // Per-technique: history for the dominant election + co-grant fan-out
            // (shares apply OUTSIDE the receiving domain's saturation — FAR Q2).
            foreach (var (technique, x) in accs)
            {
                double techBanked = SaturationMath.TechniqueBanked(
                    x, kOf(technique), smax, dc.M, depthPhase, technique == dominant,
                    config.DepthOffTechniqueWeight);
                ledger.RecordHistory(domain.Code, technique, boundary, techBanked);

                if (dc.Techniques.TryGetValue(technique, out TechniqueConfig? tc))
                {
                    foreach (var (targetCode, share) in tc.CoGrants)
                    {
                        totals.TryGetValue(targetCode, out double prev);
                        totals[targetCode] = prev + techBanked * share;
                    }
                }
            }
        }

        // Pass 2 — adjacency spillover: σ·Σ(adjacent primary banked), capped at
        // 25% of the receiver's Smax, fading across Journeyman, never revealing
        // hidden domains.
        foreach (Domain domain in template.Domains)
        {
            if (!domain.Enabled) continue;
            PlayerDomain? playerDomain = domainSet[domain.Id];
            if (playerDomain == null || playerDomain.Hidden) continue;

            DomainConfig dc = DomainConfigs[domain.Code];
            double adjacentSum = 0;
            foreach (string neighbour in dc.Adjacency)
            {
                if (primaryBanked.TryGetValue(neighbour, out double nb)) adjacentSum += nb;
            }
            if (adjacentSum <= 0) continue;

            double fade = SaturationMath.SpilloverFade(
                playerDomain.Level, JourneymanEntry, Domain.SubLevelsPerTier);
            if (fade <= 0) continue;

            double spill = Math.Min(
                config.Sigma * adjacentSum * fade,
                config.SpilloverCapPct / 100.0 * dc.Smax);
            totals.TryGetValue(domain.Code, out double prev);
            totals[domain.Code] = prev + spill;
        }

        // Pass 3 — ceiling clamp + flush through THE grant point, tier detection.
        foreach (var (domainCode, banked) in totals)
        {
            if (banked <= 0) continue;
            Domain? domain = template.FindDomain(domainCode);
            PlayerDomain? playerDomain = domain == null ? null : domainSet[domain.Id];
            if (domain == null || playerDomain == null) continue;

            int ceiling = ClassCeilingProvider(player, domain);
            double allowed = ClampToCeiling(playerDomain, banked, ceiling);
            if (allowed <= 0) continue;

            int levelBefore = playerDomain.Level;
            leveling.AddExperience(player, domain.Id, (float)allowed);

            TcmLog.Cat(sapi, TcmLog.Consolidation,
                $"{player.PlayerName} {domainCode}: banked {banked:0.#} flushed {allowed:0.#} " +
                $"(level {playerDomain.Level})");

            if (playerDomain.Level > levelBefore)
            {
                SendMorningLine(player as IServerPlayer, playerDomain);
            }
        }

        ledger.PruneHistory(boundary, config.DominantWindowDays);
        ledger.ClearDay();
        ledger.LastConsolidatedBoundary = boundary;
    }

    /// <summary>XP that would cross the class-ceiling wall is discarded ("your hands
    /// have learned all they can"). Simulates the level walk up to the ceiling.</summary>
    internal static double ClampToCeiling(PlayerDomain playerDomain, double banked, int ceiling)
    {
        if (playerDomain.Level < 0) return 0;
        if (ceiling >= playerDomain.Domain.MaxLevel) return banked;
        if (playerDomain.Level > ceiling) return 0;

        double capacity = -playerDomain.Experience;
        for (int level = playerDomain.Level + 1; level <= ceiling; level++)
        {
            capacity += playerDomain.Domain.GetRequiredExperience(level);
        }
        // At the ceiling the bar may fill but never complete (that would cross).
        capacity += playerDomain.Domain.GetRequiredExperience(ceiling + 1) - 0.01;
        return Math.Clamp(banked, 0, Math.Max(capacity, 0));
    }

    private static readonly string[] TierNames = { "Novice", "Apprentice", "Journeyman", "Master", "Grandmaster" };
    private static readonly string[] Roman = { "", "I", "II", "III", "IV" };

    private void SendMorningLine(IServerPlayer? player, PlayerDomain playerDomain)
    {
        if (player == null) return;
        int tier = Domain.TierOf(playerDomain.Level);
        if (tier < 0) return;
        string rank = $"{TierNames[tier]} {Roman[Domain.SubLevelOf(playerDomain.Level)]}";
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.GetL(player.LanguageCode, "almanactcm:morning-rankup", rank, playerDomain.Domain.DisplayName),
            EnumChatType.Notification);
    }

    // -------------------------------------------------------------------- death

    private void OnPlayerDeath(IServerPlayer byPlayer, DamageSource damageSource)
    {
        PlayerDomainSet? domainSet = leveling.GetDomainSet(byPlayer);
        if (domainSet == null) return;

        Entity? causeEntity = damageSource?.GetCauseEntity();
        bool pvp = causeEntity is EntityPlayer && causeEntity != byPlayer.Entity;

        if (pvp)
        {
            PlayerDomainSet? killerSet = causeEntity!.GetBehavior<PlayerDomainSet>();
            if (domainSet.Sparring && (killerSet?.Sparring ?? false))
            {
                TcmLog.Cat(sapi, TcmLog.Ledger, $"{byPlayer.PlayerName} died sparring — no scatter");
                return;
            }
        }

        // Chain-death window: a penalized death opens a grace period during which
        // further deaths cost nothing (there is nothing left worth farming).
        double totalHours = sapi.World.Calendar.TotalHours;
        if (domainSet.LastDeath + config.ChainDeathCooldownHours > totalHours)
        {
            TcmLog.Cat(sapi, TcmLog.Ledger, $"{byPlayer.PlayerName} chain death — no scatter");
            return;
        }
        domainSet.LastDeath = totalHours;

        double lambda = pvp ? config.LambdaPvp : config.LambdaDeath;
        if (lambda <= 0) return;

        LedgerFor(byPlayer).Scatter(lambda);
        TcmLog.Cat(sapi, TcmLog.Ledger,
            $"{byPlayer.PlayerName} death scatter λ={lambda} (pvp={pvp}) — pending practice reduced");
    }

    // -------------------------------------------------------------- persistence

    private void LoadLedgers()
    {
        string file = LedgerFileName;
        if (!File.Exists(file)) return;
        try
        {
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, PracticeLedger>>(File.ReadAllText(file));
            if (loaded != null)
            {
                foreach (var (uid, ledger) in loaded) ledgers[uid] = ledger;
            }
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi, $"ledger file unreadable ({e.Message}) — starting with empty ledgers");
        }
    }

    private void SaveLedgers()
    {
        try
        {
            string file = LedgerFileName;
            string? dir = Path.GetDirectoryName(file);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(file, JsonConvert.SerializeObject(ledgers, Formatting.Indented));
            TcmLog.Cat(sapi, TcmLog.Ledger, $"saved {ledgers.Count} ledgers");
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi, $"ledger save failed: {e.Message}");
        }
    }

    private static int JourneymanEntry => 2 * Domain.SubLevelsPerTier + 1;
}
