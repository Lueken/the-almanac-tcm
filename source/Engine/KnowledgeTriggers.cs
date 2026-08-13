using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace AlmanacTcm.Engine;

/// <summary>
/// The declarative knowledge mint (Phase A of the guide/knowledge pass, 2026-08-08).
/// Before this, every bespoke milestone key needed its own Harmony patch — which is why
/// only two ever existed (met-first-work, the ARC familiarity ladder) while the guides
/// wanted a vocabulary. Now a key is DATA: any mod (TCM, thequire, a future pack) ships
/// `assets/&lt;domain&gt;/almanac/triggers/*.json` and the entries mint per-player knowledge
/// on first contact with the named blocks. No C# per key, ever again.
///
/// Trigger file shape:
/// <code>
/// { "triggers": [ {
///     "key": "almanactcm:is-first-stackkiln",       // knowledge key, full form
///     "requires": ["industrialstory"],              // all listed mods must be loaded
///     "onBlockPlaced": ["industrialstory:stackkiln*"],
///     "onBlockUsed":   ["industrialstory:stackkiln*"],
///     "onBlockBroken": [],
///     "toast": "almanactcm:knowledge-is-stackkiln"  // lang key for the banner; omit = silent
/// } ] }
/// </code>
///
/// Design boundaries, deliberate:
/// - Knowledge only, never practice. A trigger cannot grant XP; the ledger's verbs stay
///   the single practice path. Knowledge marks that a thing happened, once.
/// - First-contact semantics: the key mints at level 1 and never again (SetKnowledge
///   no-ops on an unchanged value, and the per-player probe here skips known keys before
///   the wildcard walk even starts).
/// - `requires` prunes at LOAD: a trigger whose mods are absent is never registered, so
///   the per-event path only ever walks live triggers.
/// </summary>
public class KnowledgeTriggers
{
    private class Trigger
    {
        public string Key = "";
        public string? Toast;
        public AssetLocation[] Placed = Array.Empty<AssetLocation>();
        public AssetLocation[] Used = Array.Empty<AssetLocation>();
        public AssetLocation[] Broken = Array.Empty<AssetLocation>();
    }

    /// <summary>Raw JSON shape of one trigger file (Newtonsoft, via IAsset.ToObject).</summary>
    private class TriggerFileJson
    {
        public TriggerJson[]? Triggers { get; set; }
    }

    private class TriggerJson
    {
        public string? Key { get; set; }
        public string[]? Requires { get; set; }
        public string[]? OnBlockPlaced { get; set; }
        public string[]? OnBlockUsed { get; set; }
        public string[]? OnBlockBroken { get; set; }
        public string? Toast { get; set; }
    }

    private readonly ICoreServerAPI sapi;
    private readonly List<Trigger> placed = new();
    private readonly List<Trigger> used = new();
    private readonly List<Trigger> broken = new();

    public static KnowledgeTriggers? Instance { get; private set; }

    public static void RegisterServer(ICoreServerAPI sapi) => Instance = new KnowledgeTriggers(sapi);

    private KnowledgeTriggers(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        Load();
        if (placed.Count > 0) sapi.Event.DidPlaceBlock += OnBlockPlaced;
        if (used.Count > 0) sapi.Event.DidUseBlock += OnBlockUsed;
        if (broken.Count > 0) sapi.Event.DidBreakBlock += OnBlockBroken;
    }

    private void Load()
    {
        // The `almanac` asset category is registered by Illuminated (a hard dependency, so
        // it is always there before us); the same path convention its guide scanner uses.
        int files = 0, live = 0, pruned = 0;
        foreach (IAsset asset in sapi.Assets.GetMany("almanac/triggers/"))
        {
            files++;
            TriggerFileJson? file = null;
            try { file = asset.ToObject<TriggerFileJson>(); }
            catch (Exception e)
            {
                TcmLog.Warn(sapi, $"knowledge triggers: {asset.Location} unreadable ({e.Message}); skipped");
                continue;
            }
            if (file?.Triggers == null) continue;

            foreach (TriggerJson t in file.Triggers)
            {
                if (string.IsNullOrEmpty(t.Key))
                {
                    TcmLog.Warn(sapi, $"knowledge triggers: entry without a key in {asset.Location}; skipped");
                    continue;
                }
                bool missing = false;
                if (t.Requires != null)
                {
                    foreach (string modid in t.Requires)
                    {
                        if (!sapi.ModLoader.IsModEnabled(modid)) { missing = true; break; }
                    }
                }
                if (missing) { pruned++; continue; }

                var trigger = new Trigger
                {
                    Key = t.Key,
                    Toast = t.Toast,
                    Placed = ToLocations(t.OnBlockPlaced),
                    Used = ToLocations(t.OnBlockUsed),
                    Broken = ToLocations(t.OnBlockBroken),
                };
                if (trigger.Placed.Length == 0 && trigger.Used.Length == 0 && trigger.Broken.Length == 0)
                {
                    TcmLog.Warn(sapi, $"knowledge triggers: {t.Key} has no event patterns; skipped");
                    continue;
                }
                live++;
                if (trigger.Placed.Length > 0) placed.Add(trigger);
                if (trigger.Used.Length > 0) used.Add(trigger);
                if (trigger.Broken.Length > 0) broken.Add(trigger);
            }
        }
        TcmLog.Info(sapi, $"knowledge triggers: {live} live from {files} file(s), {pruned} pruned (missing mods)");
    }

    private static AssetLocation[] ToLocations(string[]? patterns)
    {
        if (patterns == null || patterns.Length == 0) return Array.Empty<AssetLocation>();
        var result = new AssetLocation[patterns.Length];
        for (int i = 0; i < patterns.Length; i++) result[i] = AssetLocation.Create(patterns[i]);
        return result;
    }

    // ----------------------------------------------------------------- event paths

    private void OnBlockPlaced(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        Block? block = sapi.World.BlockAccessor.GetBlock(blockSel.Position);
        Check(byPlayer, block, placed, t => t.Placed);
    }

    private void OnBlockUsed(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        Block? block = sapi.World.BlockAccessor.GetBlock(blockSel.Position);
        Check(byPlayer, block, used, t => t.Used);
    }

    private void OnBlockBroken(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel)
    {
        // The block is gone from the world; the OLD id names what was broken.
        Block? block = sapi.World.GetBlock(oldblockId);
        Check(byPlayer, block, broken, t => t.Broken);
    }

    private void Check(IServerPlayer byPlayer, Block? block, List<Trigger> triggers, System.Func<Trigger, AssetLocation[]> patternsOf)
    {
        if (block?.Code == null || byPlayer == null) return;
        var server = AlmanacTcmModSystem.ServerInstance?.Server;
        var domainSet = server?.GetDomainSet(byPlayer);
        if (server == null || domainSet == null) return;

        foreach (Trigger trigger in triggers)
        {
            if (domainSet.Knowledge.ContainsKey(trigger.Key)) continue;
            foreach (AssetLocation pattern in patternsOf(trigger))
            {
                if (!WildcardUtil.Match(pattern, block.Code)) continue;
                server.SetKnowledge(byPlayer, trigger.Key, 1, trigger.Toast);
                TcmLog.Cat(sapi, TcmLog.Ledger,
                    $"{byPlayer.PlayerName} knowledge {trigger.Key} (trigger: {block.Code})");
                break;
            }
        }
    }
}
