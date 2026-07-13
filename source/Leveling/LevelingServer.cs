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

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        sapi.Event.GameWorldSave += SaveData;
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
        if (result > 0) return;
        if (result < 0)
        {
            TcmLog.Warn(sapi, "failed to load save file, trying backup");
            result = LoadFromFile(BackupSaveFileName);
        }
        if (result > 0) return;
        OfflineDomainSets = new Dictionary<string, SavedPlayerDomainSet>();
    }

    /// <summary>Rotates the previous save into Backups before writing (xLib's crash guard —
    /// a write that dies mid-stream still leaves the previous day recoverable).</summary>
    public void SaveData()
    {
        if (OfflineDomainSets == null) return;

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
    }

    private void SyncAll(IServerPlayer byPlayer, PlayerDomainSet domainSet)
    {
        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            channel.SendPacket(new PlayerDomainPacket(playerDomain), byPlayer);
        }
        foreach (string key in domainSet.Knowledge.Keys)
        {
            channel.SendPacket(new KnowledgePacket(key, domainSet.Knowledge[key]), byPlayer);
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

    public void SetKnowledge(IPlayer player, string name, int level)
    {
        PlayerDomainSet? domainSet = GetDomainSet(player);
        if (domainSet == null) return;
        domainSet.Knowledge[name] = level;
        channel.SendPacket(new KnowledgePacket(name, level), player as IServerPlayer);
    }
}
