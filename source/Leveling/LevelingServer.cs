// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlmanacTcm.Leveling;

/// <summary>
/// Server side of the vendored substrate: per-player persistence (own save path,
/// never xLib's), join-time sync on the almanactcm channel, and the single
/// server-authoritative grant point whose only caller is the 3am consolidation
/// flush. No death handling lives here — death is a ledger concern (T1.2), and
/// banked XP is structurally untouchable.
/// </summary>
public class LevelingServer
{
    private readonly ICoreServerAPI sapi;
    private readonly DomainSetTemplate template;
    private IServerNetworkChannel channel = null!;

    public Dictionary<IPlayer, PlayerDomainSet> DomainSets { get; } = new();

    /// <summary>Skill sets of players not currently online, keyed by player UID.</summary>
    public Dictionary<string, SavedPlayerDomainSet> OfflineDomainSets { get; private set; } = new();

    /// <summary>Fires after a player's set is created and loaded from save, before the
    /// full client sync — the seam where affinity start levels apply.</summary>
    public event System.Action<IServerPlayer, PlayerDomainSet>? DomainSetReady;

    public string SaveFileDirectory => Path.Combine(GamePaths.Saves, "AlmanacTcm");
    public string BackupFileDirectory => Path.Combine(GamePaths.Backups, "AlmanacTcm");

    /// <summary>Per-world filename, remembered in world config so a world rename never
    /// orphans its save (xLib's proven pattern, own config key).</summary>
    private string FileName
    {
        get
        {
            string invalidChars = new string(Path.GetInvalidFileNameChars());
            Regex regex = new(string.Format("[{0}]", Regex.Escape(invalidChars)));
            string str = sapi.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi.WorldManager.SaveGame?.WorldName
                ?? "almanactcm_save";
            return regex.Replace(str, "") + ".json";
        }
    }

    public string SaveFileName => Path.Combine(SaveFileDirectory, FileName);
    public string BackupSaveFileName => Path.Combine(BackupFileDirectory, FileName);

    public LevelingServer(ICoreServerAPI sapi, DomainSetTemplate template)
    {
        this.sapi = sapi;
        this.template = template;

        if (!Directory.Exists(SaveFileDirectory)) Directory.CreateDirectory(SaveFileDirectory);
        if (!File.Exists(SaveFileName))
        {
            sapi.World.Config.SetString("AlmanacTcmSaveFile", sapi.WorldManager.SaveGame.WorldName);
        }

        LoadData();

        channel = sapi.Network.RegisterChannel("almanactcm");
        channel.RegisterMessageType(typeof(PlayerDomainPacket));
        channel.RegisterMessageType(typeof(KnowledgePacket));
        channel.RegisterMessageType(typeof(KnowledgeBatchPacket));
        channel.RegisterMessageType(typeof(AffinityPacket));
        channel.RegisterMessageType(typeof(ClientConfigPacket));
        channel.RegisterMessageType(typeof(PracticeGainPacket));
        channel.RegisterMessageType(typeof(RankUpPacket));

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        sapi.Event.GameWorldSave += SaveData;

        // Held rank-up ceremonies flush on a slow poll; a no-op when nothing is pending.
        sapi.Event.RegisterGameTickListener(FlushPendingCeremonies, 1000);
    }

    private int LoadFromFile(string fileName)
    {
        if (fileName == null) return 0;
        if (!File.Exists(fileName)) return 0;
        try
        {
            JsonSerializerSettings settings = new();
            settings.Error = (sender, err) =>
            {
                TcmLog.Error(sapi, $"error loading {fileName}: {err.ErrorContext.Error.Message}");
                err.ErrorContext.Handled = true;
            };

            OfflineDomainSets = JsonConvert.DeserializeObject<Dictionary<string, SavedPlayerDomainSet>>(
                File.ReadAllText(fileName), settings)!;
            if (OfflineDomainSets == null)
            {
                OfflineDomainSets = new Dictionary<string, SavedPlayerDomainSet>();
                TcmLog.Error(sapi, $"error loading {fileName}: file seems damaged");
                return -1;
            }
        }
        catch (Exception error)
        {
            TcmLog.Error(sapi, $"error loading {fileName}: {error.Message}");
            return -1;
        }
        return 1;
    }

    private void LoadData()
    {
        int result = LoadFromFile(SaveFileName);
        LoadStoredCeremonies();
        if (result > 0) return;
        if (result < 0)
        {
            TcmLog.Warn(sapi, "failed to load save file, trying backup");
            result = LoadFromFile(BackupSaveFileName);
        }
        if (result > 0) return;
        OfflineDomainSets = new Dictionary<string, SavedPlayerDomainSet>();
    }

    /// <summary>The durable ceremony queue rides the world save (the AlcPatches StoreData
    /// pattern), not the leveling JSON file: no schema change to a save with a backup
    /// rotation, and a corrupt entry costs a banner, never a rank.</summary>
    private void LoadStoredCeremonies()
    {
        try
        {
            byte[]? data = sapi.WorldManager.SaveGame.GetData(CeremonyStoreKey);
            if (data != null)
                storedCeremonies = Vintagestory.API.Util.SerializerUtil
                    .Deserialize<Dictionary<string, List<string>>>(data) ?? new();
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi, $"pending-ceremony store unreadable ({e.Message}); starting empty");
            storedCeremonies = new();
        }
    }

    /// <summary>Rotates the previous save into Backups before writing (xLib's crash guard —
    /// a write that dies mid-stream still leaves the previous day recoverable).</summary>
    public void SaveData()
    {
        if (OfflineDomainSets == null) return;

        sapi.WorldManager.SaveGame.StoreData(CeremonyStoreKey,
            Vintagestory.API.Util.SerializerUtil.Serialize(storedCeremonies));

        try
        {
            string backupName = BackupSaveFileName;
            string? path = Path.GetDirectoryName(backupName);
            if (path != null && !Directory.Exists(path)) Directory.CreateDirectory(path);
            File.Move(SaveFileName, backupName, true);
        }
        catch (Exception) { }

        Dictionary<string, SavedPlayerDomainSet> toStore = new();
        foreach (IPlayer player in DomainSets.Keys)
        {
            toStore[player.PlayerUID] = new SavedPlayerDomainSet(DomainSets[player]);
        }
        foreach (string key in OfflineDomainSets.Keys)
        {
            if (!toStore.ContainsKey(key)) toStore[key] = OfflineDomainSets[key];
        }

        JsonSerializer serializer = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };
        using StreamWriter streamWriter = new(SaveFileName);
        using JsonWriter jsonWriter = new JsonTextWriter(streamWriter);
        serializer.Serialize(jsonWriter, toStore);

        TcmLog.Cat(sapi, TcmLog.Config, $"saved {toStore.Count} player domain sets");
    }

    private void OnPlayerNowPlaying(IServerPlayer byPlayer)
    {
        if (byPlayer == null) return;
        PlayerDomainSet domainSet = new(byPlayer, template);
        DomainSets[byPlayer] = domainSet;

        if (OfflineDomainSets.TryGetValue(byPlayer.PlayerUID, out SavedPlayerDomainSet? saved))
        {
            domainSet.FromSavedSet(saved);
            OfflineDomainSets.Remove(byPlayer.PlayerUID);
        }

        DomainSetReady?.Invoke(byPlayer, domainSet);
        SyncAll(byPlayer, domainSet);

        // A banner this player earned and never saw: reload it into the live queue, where
        // the grace floor and login protection gate it exactly like a fresh one. REPLACE,
        // never append: a fast relog can reach this handler before the flush's offline sweep
        // clears the old live entry, and the durable copy always equals what was queued
        // (both halves are written together and cleared together), so appending would show
        // the banner twice and replacing can lose nothing.
        if (storedCeremonies.TryGetValue(byPlayer.PlayerUID, out var held) && held.Count > 0)
        {
            var list = new List<RankUpPacket>();
            foreach (string packed in held)
            {
                string[] parts = packed.Split('|', 2);
                if (parts.Length == 2) list.Add(new RankUpPacket(parts[0], parts[1]));
            }
            if (list.Count > 0)
            {
                pendingCeremonies[byPlayer.PlayerUID] = list;
                ceremonyQueuedMs[byPlayer.PlayerUID] = sapi.World.ElapsedMilliseconds;
            }
        }
    }

    private void SyncAll(IServerPlayer byPlayer, PlayerDomainSet domainSet)
    {
        // Client-facing config flags first, so client-side gates honour the server's settings.
        var g = AlmanacTcmModSystem.ServerInstance?.GlobalConfig;
        channel.SendPacket(new ClientConfigPacket(
            g?.AlloyLedgerGated ?? true,
            g?.GrowerEyeFAR ?? true,
            g?.FamAcquaintedHarvests ?? 5,
            g?.FamVersedHarvests ?? 25,
            g?.FamFamilyVersedSum ?? 50,
            g?.FamSpread ?? 0.5), byPlayer);

        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            channel.SendPacket(new PlayerDomainPacket(playerDomain), byPlayer);
        }
        // The whole store in one silent batch — never per-key packets at join (per-key is
        // the LIVE path and now carries toast ceremony; a replay must not re-celebrate).
        if (domainSet.Knowledge.Count > 0)
        {
            channel.SendPacket(new KnowledgeBatchPacket(domainSet.Knowledge), byPlayer);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        if (byPlayer == null) return;
        PlayerDomainSet? domainSet = GetDomainSet(byPlayer);
        if (domainSet != null)
        {
            OfflineDomainSets[byPlayer.PlayerUID] = new SavedPlayerDomainSet(domainSet);
        }
        DomainSets.Remove(byPlayer);
    }

    public PlayerDomainSet? GetDomainSet(IPlayer player)
    {
        if (player == null) return null;
        DomainSets.TryGetValue(player, out PlayerDomainSet? domainSet);
        return domainSet;
    }

    /// <summary>Fire the HUD toast ping for one surviving practice gain. Display-only:
    /// the ledger already logged the value; this is the sensation channel.</summary>
    public void SendPracticeGain(IPlayer player, string domainCode, string technique, double raw)
    {
        if (player is not IServerPlayer serverPlayer) return;
        channel.SendPacket(new PracticeGainPacket(domainCode, technique, (float)raw), serverPlayer);
    }

    /// <summary>THE grant point. Only the 3am consolidation flush may call this —
    /// listeners write to the day ledger, never here. Server-authoritative; the
    /// resulting state (never the delta math) is synced to the client.</summary>
    public void AddExperience(IPlayer player, int domainId, float experience)
    {
        PlayerDomainSet? domainSet = GetDomainSet(player);
        PlayerDomain? playerDomain = domainSet?[domainId];
        if (playerDomain == null) return;

        int levelBefore = playerDomain.Level;
        playerDomain.Experience += experience;

        channel.SendPacket(new PlayerDomainPacket(playerDomain), player as IServerPlayer);
        if (playerDomain.Level != levelBefore)
        {
            TcmLog.Cat(sapi, TcmLog.Consolidation,
                $"{player.PlayerName} {playerDomain.Domain.Code}: level {levelBefore} -> {playerDomain.Level}");
        }
    }

    /// <summary>Re-syncs one domain's state to its player (affinity start-level
    /// changes outside the join flow use this).</summary>
    public void SyncDomain(IPlayer player, PlayerDomain playerDomain)
    {
        channel.SendPacket(new PlayerDomainPacket(playerDomain), player as IServerPlayer);
    }

    /// <summary>Mid-day pending sync: the same state packet plus the ledger's projection
    /// of what today's practice would bank at the next rest. Display-only — grants
    /// nothing; only the consolidation flush may call AddExperience.</summary>
    public void SyncPending(IPlayer player, PlayerDomain playerDomain, float pendingBanked)
    {
        var packet = new PlayerDomainPacket(playerDomain) { pendingBanked = pendingBanked };
        channel.SendPacket(packet, player as IServerPlayer);
    }

    /// <summary>Sends the resolved affinity band for one domain (the "why you started
    /// here" line). The grid stays server-side; only the band crosses.</summary>
    public void SyncAffinity(IPlayer player, int domainId, int band)
    {
        channel.SendPacket(new AffinityPacket(domainId, band), player as IServerPlayer);
    }

    /// <summary>Reveals a hidden domain (first-action-only discovery) and syncs it.</summary>
    public void RevealDomain(IPlayer player, int domainId)
    {
        PlayerDomainSet? domainSet = GetDomainSet(player);
        PlayerDomain? playerDomain = domainSet?[domainId];
        if (playerDomain == null || !playerDomain.Hidden) return;

        playerDomain.Hidden = false;
        channel.SendPacket(new PlayerDomainPacket(playerDomain), player as IServerPlayer);
        TcmLog.Cat(sapi, TcmLog.Ledger,
            $"{player.PlayerName} discovered domain {playerDomain.Domain.Code}");
    }

    /// <summary>Write one knowledge key and sync it live. No-ops when the stored value
    /// already equals <paramref name="level"/> — no packet, no toast, so callers need no
    /// ContainsKey dance to stay idempotent. <paramref name="toastLangKey"/> names the
    /// discovery for the client banner; null keeps the earn silent (the auto-mint stream).</summary>
    public void SetKnowledge(IPlayer player, string name, int level, string? toastLangKey = null)
    {
        PlayerDomainSet? domainSet = GetDomainSet(player);
        if (domainSet == null) return;
        if (domainSet.Knowledge.TryGetValue(name, out int existing) && existing == level) return;
        domainSet.Knowledge[name] = level;
        channel.SendPacket(new KnowledgePacket(name, level, toastLangKey), player as IServerPlayer);
    }

    // ------------------------------------------------------- rank-up ceremonies

    /// <summary>Ceremonies held for players still inside login protection. The STATE
    /// already synced at grant; only the banner waits (RULED 2026-08-08).</summary>
    private readonly Dictionary<string, List<RankUpPacket>> pendingCeremonies = new();

    /// <summary>The DURABLE mirror of the queue (0.5, verb-review blocker 5): uid -> packed
    /// "rank|domainName" entries, kept in the world save under <see cref="CeremonyStoreKey"/>.
    /// Before this, disconnecting inside the five-second grace discarded the banner
    /// permanently, and the ceremony pipeline is exactly what a Grandmaster ascension
    /// ceremony will one day extend: durable-first was the ruling. Entries are written at
    /// queue time, dropped only on successful delivery, and reloaded into the live queue
    /// when their player next joins. Ranks themselves were never at risk; only the moment.</summary>
    private Dictionary<string, List<string>> storedCeremonies = new();

    private const string CeremonyStoreKey = "almanacPendingCeremonies";

    /// <summary>Elapsed-ms each player's ceremonies were queued; enforces the grace floor.</summary>
    private readonly Dictionary<string, long> ceremonyQueuedMs = new();

    /// <summary>Minimum hold on a delayed ceremony even once protection reads clear —
    /// the fallback "fully in-game" proxy when loginprotection is not installed.</summary>
    private const long CeremonyGraceMs = 5000;

    /// <summary>Queue (or immediately send) the rank-up banner. <paramref name="delayed"/>
    /// marks a login-consolidation rank-up: hold it until login protection has released
    /// the player, which is the signal they are fully in-game and aware. A 3am rank-up
    /// caught in play sends at once.</summary>
    public void QueueRankUpCeremony(IPlayer player, string rank, string domainName, bool delayed)
    {
        if (player is not IServerPlayer serverPlayer) return;
        var packet = new RankUpPacket(rank, domainName);
        if (!delayed)
        {
            channel.SendPacket(packet, serverPlayer);
            return;
        }
        if (!pendingCeremonies.TryGetValue(player.PlayerUID, out var list))
        {
            list = new List<RankUpPacket>();
            pendingCeremonies[player.PlayerUID] = list;
            ceremonyQueuedMs[player.PlayerUID] = sapi.World.ElapsedMilliseconds;
        }
        list.Add(packet);

        // The durable half, written at queue time. Persisted to the save at the next world
        // save; dropped only when the banner actually reaches the player.
        if (!storedCeremonies.TryGetValue(player.PlayerUID, out var stored))
            storedCeremonies[player.PlayerUID] = stored = new List<string>();
        stored.Add($"{rank}|{domainName}");
    }

    private void FlushPendingCeremonies(float dt)
    {
        if (pendingCeremonies.Count == 0) return;
        long now = sapi.World.ElapsedMilliseconds;

        List<string>? done = null;
        foreach (var (uid, packets) in pendingCeremonies)
        {
            IServerPlayer? player = sapi.World.PlayerByUid(uid) as IServerPlayer;
            if (player == null || player.ConnectionState == EnumClientState.Offline)
            {
                // Out of the LIVE queue only. The durable mirror keeps the entry, and the
                // next join reloads it, which is the whole fix: this branch used to be
                // where a disconnect inside the grace window lost the banner forever.
                (done ??= new List<string>()).Add(uid);
                continue;
            }
            if (now - ceremonyQueuedMs.GetValueOrDefault(uid) < CeremonyGraceMs) continue;
            if (IsLoginProtected(player)) continue;

            foreach (RankUpPacket packet in packets) channel.SendPacket(packet, player);
            storedCeremonies.Remove(uid); // delivered: the durable copy has done its job
            (done ??= new List<string>()).Add(uid);
        }
        if (done != null)
        {
            foreach (string uid in done) { pendingCeremonies.Remove(uid); ceremonyQueuedMs.Remove(uid); }
        }
    }

    // LoginProtection (server-only mod, no compile-time ref) resolved once by reflection.
    // Protection ends on >0.5 block movement, fire, lava, or its own timeout
    // (LoginProtectionModSystem.StopProtectionIfPlayersHaveMoved, decompiled 1.4.1).
    private bool loginProtResolved;
    private ModSystem? loginProtSystem;
    private System.Reflection.MethodInfo? loginProtIsProtected;

    private bool IsLoginProtected(IServerPlayer player)
    {
        if (!loginProtResolved)
        {
            loginProtResolved = true;
            if (sapi.ModLoader.IsModEnabled("loginprotection"))
            {
                foreach (ModSystem system in sapi.ModLoader.Systems)
                {
                    if (system.GetType().FullName != "LoginProtection.LoginProtectionModSystem") continue;
                    loginProtSystem = system;
                    loginProtIsProtected = system.GetType().GetMethod("IsPlayerProtected");
                    break;
                }
                if (loginProtIsProtected == null)
                    TcmLog.Warn(sapi, "loginprotection present but IsPlayerProtected not found; rank-up banners fall back to the grace timer alone");
            }
        }
        if (loginProtSystem == null || loginProtIsProtected == null) return false;
        try { return loginProtIsProtected.Invoke(loginProtSystem, new object[] { player }) is true; }
        catch { return false; }
    }
}
