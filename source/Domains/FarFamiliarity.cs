using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// FAR crop familiarity — the Grower's Eye data layer (RULED 2026-08-22, brief:
/// docs/design/2026-08-22_far-pass-audit-and-familiarity.md).
///
/// Familiarity is INDEPENDENT of FAR rank: knowledge is earned per crop, not granted for
/// existing in VS. The store is the synced Knowledge dictionary (the TEM/MEL book pattern,
/// silent): <c>far-crop-(id)</c> bumped once per harvest of that crop, where (id) is the
/// canonical crop id from <c>almanactcm:config/crop-families.json</c>. The taxonomy asset is
/// the single source for family grouping (the Crops tab consumes the same file), and it bakes
/// the ruled taxonomy calls in: potato in roots, the bdcrop and DAR leeks aliased to ONE id,
/// the Apiaceae split accepted.
///
/// Effective familiarity with a crop = own counter + FamSpread x (summed counters of its
/// family-mates). Knowledge of one legume teaches you something about all legumes, never
/// everything. Tiers: Stranger below Acquainted, Acquainted at FamAcquaintedHarvests
/// (rough words), Versed at FamVersedHarvests (full figures). The family-wide Journeyman
/// read opens when the family's summed counters reach FamFamilyVersedSum.
///
/// Thresholds are server knobs (TcmGlobalConfig), mirrored to clients in ClientConfigPacket
/// at join, because the readout ladder is evaluated client-side from the synced counters.
/// </summary>
public static class FarFamiliarity
{
    public const string KeyPrefix = "far-crop-";

    private static bool loaded;
    private static readonly List<KeyValuePair<string, string>> prefixMap = new(); // code prefix -> crop id
    private static readonly Dictionary<string, string> familyOfId = new();        // crop id -> family
    private static readonly Dictionary<string, List<string>> familyMembers = new();
    private static readonly Dictionary<string, string> ripeBlockToId = new();     // exact block code -> crop id

    private class FamiliesFile
    {
        public Dictionary<string, Dictionary<string, List<string>>>? families { get; set; }
        public Dictionary<string, List<string>>? ripeBlocks { get; set; }
    }

    /// <summary>Lazy-loads the taxonomy on first use (the domains.json pattern); assets are
    /// ready by any gameplay call site on either side. Safe to call repeatedly.</summary>
    public static void EnsureLoaded(ICoreAPI api)
    {
        if (loaded || api == null) return;
        loaded = true;
        try
        {
            var asset = api.Assets.TryGet(new AssetLocation("almanactcm", "config/crop-families.json"));
            var file = asset?.ToObject<FamiliesFile>();
            if (file?.families == null)
            {
                TcmLog.Warn(api, "crop-families.json missing or empty — crop familiarity inert");
                return;
            }
            foreach (var (family, crops) in file.families)
            {
                var ids = new List<string>();
                foreach (var (id, prefixes) in crops)
                {
                    ids.Add(id);
                    familyOfId[id] = family;
                    foreach (string prefix in prefixes ?? new List<string>())
                        prefixMap.Add(new(prefix, id));
                }
                familyMembers[family] = ids;
            }
            // Longest prefix first, so game:crop-seed-carrot beats game:crop-carrot cleanly
            // whichever order the file lists them in.
            prefixMap.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));

            if (file.ripeBlocks != null)
                foreach (var (id, codes) in file.ripeBlocks)
                    foreach (string code in codes)
                        ripeBlockToId[code] = id;

            TcmLog.Cat(api, TcmLog.Config,
                $"crop families loaded: {familyOfId.Count} crops in {familyMembers.Count} families, {ripeBlockToId.Count} vine fruit blocks");
        }
        catch (System.Exception e)
        {
            TcmLog.Error(api, $"crop-families.json unreadable ({e.Message}) — crop familiarity inert");
        }
    }

    // ------------------------------------------------------------ identity

    // The AoG Breeding Addon's varietal sizes (verified 1.2.2: game:crop-(size)-(type)-(stage)).
    // A varietal IS its species for familiarity: growing wild carrots teaches you carrots.
    private static readonly string[] varietalSizes =
        { "wild", "small", "medium", "decent", "large", "hefty", "gigantic" };

    /// <summary>Canonical crop id for a block, by longest code-prefix match; null when the
    /// block is no known crop (unknown crops honestly stay strangers). Breeding-Addon
    /// varietal codes normalize onto their species before matching.</summary>
    public static string? CropIdOf(ICoreAPI api, Block? block)
    {
        if (block?.Code == null) return null;
        EnsureLoaded(api);
        string code = block.Code.Domain + ":" + block.Code.Path;

        foreach (var (prefix, id) in prefixMap)
            if (code.StartsWith(prefix, System.StringComparison.Ordinal)) return id;

        const string varietalHead = "game:crop-";
        if (code.StartsWith(varietalHead, System.StringComparison.Ordinal))
        {
            string rest = code.Substring(varietalHead.Length);
            foreach (string size in varietalSizes)
            {
                if (!rest.StartsWith(size + "-", System.StringComparison.Ordinal)) continue;
                string normalized = varietalHead + rest.Substring(size.Length + 1);
                foreach (var (prefix, id) in prefixMap)
                    if (normalized.StartsWith(prefix, System.StringComparison.Ordinal)) return id;
                break;
            }
        }
        return null;
    }

    /// <summary>Crop id when this exact block is a registered RIPE vine fruit (pumpkin and the
    /// bdcrop melons/squash, whose harvest never reaches the BlockCrop seam); null otherwise.</summary>
    public static string? RipeFruitIdOf(ICoreAPI api, Block? block)
    {
        if (block?.Code == null) return null;
        EnsureLoaded(api);
        return ripeBlockToId.TryGetValue(block.Code.Domain + ":" + block.Code.Path, out string? id) ? id : null;
    }

    public static string? FamilyOf(string cropId) =>
        familyOfId.TryGetValue(cropId, out string? fam) ? fam : null;

    /// <summary>All registered ripe-fruit block codes with their crop ids (the vine hook's
    /// build list). Call EnsureLoaded first.</summary>
    public static IEnumerable<KeyValuePair<string, string>> RipeBlockCodes() => ripeBlockToId;

    /// <summary>Every (family, cropId) pair in the taxonomy (the yield table's row source).
    /// Call EnsureLoaded first.</summary>
    public static IEnumerable<KeyValuePair<string, string>> AllCropIds()
    {
        foreach (var (family, ids) in familyMembers)
            foreach (string id in ids) yield return new(family, id);
    }

    // ------------------------------------------------------------ thresholds (side-aware)

    public static bool EyeEnabled(ICoreAPI api) => api.Side == EnumAppSide.Server
        ? AlmanacTcmModSystem.ServerInstance?.GlobalConfig.GrowerEyeFAR ?? true
        : AlmanacTcmModSystem.ClientInstance?.GrowerEyeFar ?? true;

    private static (int Acq, int Versed, int FamilySum, double Spread) Thresholds(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
        {
            var g = AlmanacTcmModSystem.ServerInstance?.GlobalConfig;
            return (g?.FamAcquaintedHarvests ?? 5, g?.FamVersedHarvests ?? 25,
                    g?.FamFamilyVersedSum ?? 50, g?.FamSpread ?? 0.5);
        }
        var c = AlmanacTcmModSystem.ClientInstance;
        return (c?.FamAcquainted ?? 5, c?.FamVersed ?? 25, c?.FamFamilyVersed ?? 50, c?.FamSpread ?? 0.5);
    }

    // ------------------------------------------------------------ counts and tiers

    /// <summary>The viewer's synced Knowledge dictionary from whichever side is live, or null.</summary>
    public static IReadOnlyDictionary<string, int>? KnowledgeOf(ICoreAPI api, IPlayer? player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.Knowledge;
        return AlmanacTcmModSystem.ClientInstance?.Client?.Knowledge;
    }

    public static int OwnCount(IReadOnlyDictionary<string, int>? know, string cropId) =>
        know != null && know.TryGetValue(KeyPrefix + cropId, out int n) ? n : 0;

    /// <summary>Own counter + spread x summed family-mate counters (the some-but-not-all rule).</summary>
    public static double EffectiveCount(ICoreAPI api, IReadOnlyDictionary<string, int>? know, string cropId)
    {
        int own = OwnCount(know, cropId);
        string? family = FamilyOf(cropId);
        if (family == null || !familyMembers.TryGetValue(family, out var mates)) return own;
        int mateSum = 0;
        foreach (string mate in mates)
            if (mate != cropId) mateSum += OwnCount(know, mate);
        return own + Thresholds(api).Spread * mateSum;
    }

    public static bool IsAcquainted(ICoreAPI api, IReadOnlyDictionary<string, int>? know, string cropId) =>
        EffectiveCount(api, know, cropId) >= Thresholds(api).Acq;

    public static bool IsVersed(ICoreAPI api, IReadOnlyDictionary<string, int>? know, string cropId) =>
        EffectiveCount(api, know, cropId) >= Thresholds(api).Versed;

    /// <summary>The Journeyman family-wide read: the family's summed counters, own included.</summary>
    public static bool IsFamilyVersed(ICoreAPI api, IReadOnlyDictionary<string, int>? know, string family)
    {
        if (!familyMembers.TryGetValue(family, out var ids)) return false;
        int sum = 0;
        foreach (string id in ids) sum += OwnCount(know, id);
        return sum >= Thresholds(api).FamilySum;
    }

    // ------------------------------------------------------------ the harvest bump (server)

    /// <summary>One harvest of one crop = one count, silent, capped only to bound the synced
    /// store. SetKnowledge persists and syncs in the same call (the ARC school pattern).</summary>
    public static void BumpHarvest(ICoreServerAPI sapi, IPlayer player, string cropId)
    {
        var server = AlmanacTcmModSystem.ServerInstance?.Server;
        var set = server?.GetDomainSet(player);
        if (server == null || set == null) return;

        string key = KeyPrefix + cropId;
        int cur = set.Knowledge.TryGetValue(key, out int n) ? n : 0;
        int cap = AlmanacTcmModSystem.ServerInstance?.GlobalConfig.FamCountCap ?? 500;
        if (cur >= cap) return;
        server.SetKnowledge(player, key, cur + 1);
    }
}
