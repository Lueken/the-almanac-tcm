using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO Axis 4b — THE COLLIER'S MARK (the WOO provenance product; MET's Maker's-Mark twin).
/// A Grandmaster collier's charcoal burns hotter and longer FOR WHOEVER FEEDS IT to a firepit.
/// Tradeable premium fuel: a smith who wants his charcoal to stretch seeks out the GM collier.
///
/// **Why this needs a carrier at all.** The charcoal pile is `BlockCharcoalPile :
/// BlockLayeredSlowDig : Block` — a plain block with NO block entity — so there is nowhere on the
/// pile itself to write who burned it. This class is the carrier the design doc called "the one
/// real build cost of the axis": a small persisted pos→collier map written at ConvertPit and
/// spent at the pile's GetDrops.
///
/// **It only ever holds GM pits.** Non-GM burns write nothing, so the map stays tiny.
///
/// **Every failure mode falls toward UNMARKED, never toward wrongly-marked** (RULED, and the same
/// "adulterated fuel" fiction Jeffrey already accepted for stack-merge):
///   • a pile that falls loses its mark (we do not chase the falling entity)
///   • a pile merged into by another pile is adulterated → mark cleared
///   • a hand-placed pile clears any stale entry at that position
///   • a read validates the block at pos is still a charcoal pile
/// </summary>
public static class WooColliersMark
{
    /// <summary>Itemstack attribute holding the burner's name. Presence == marked.</summary>
    public const string MarkAttr = "colliersMark";

    private const string ByKey = "by";

    /// <summary>"x/y/z" → collier name. Only GM pits are ever recorded (see class docs), so this
    /// stays small; keyed by string because BlockPos is not a usable JSON dictionary key.</summary>
    private static Dictionary<string, string> marks = new();

    private static ICoreServerAPI? sapi;

    private static string Key(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    private static string MarkFileName
    {
        get
        {
            string name = sapi?.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi?.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", name + "-colliersmarks.json");
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        Load();
        api.Event.GameWorldSave += Save;
    }

    private static void Load()
    {
        try
        {
            string file = MarkFileName;
            if (!File.Exists(file)) return;
            marks = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            TcmLog.Cat(sapi, TcmLog.Config, $"collier's marks loaded: {marks.Count} pending pile(s)");
        }
        catch (System.Exception e)
        {
            // Never let a broken side-file take the server down; an unmarked world is a safe world.
            TcmLog.Error(sapi, $"colliersmarks.json unreadable ({e.Message}); starting empty, NOT overwriting");
            marks = new();
        }
    }

    private static void Save()
    {
        try
        {
            Prune();
            string file = MarkFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonConvert.SerializeObject(marks, Formatting.Indented));
        }
        catch (System.Exception e) { TcmLog.Error(sapi, $"could not save collier's marks: {e.Message}"); }
    }

    /// <summary>Drops entries whose pile no longer exists — the backstop against a mark going
    /// stale after a pile falls away, which is the one way a position key could later attach to
    /// something it did not burn.</summary>
    private static void Prune()
    {
        if (sapi == null || marks.Count == 0) return;
        var dead = new List<string>();
        foreach (var kv in marks)
        {
            string[] p = kv.Key.Split('/');
            if (p.Length != 3) { dead.Add(kv.Key); continue; }
            var pos = new BlockPos(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
            // Only judge loaded chunks: an unloaded pile reads as null and is NOT dead.
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(pos) == null) continue;
            if (sapi.World.BlockAccessor.GetBlock(pos) is not BlockCharcoalPile) dead.Add(kv.Key);
        }
        foreach (string k in dead) marks.Remove(k);
        if (dead.Count > 0) TcmLog.Cat(sapi, TcmLog.Config, $"collier's marks pruned: {dead.Count} stale");
    }

    // ------------------------------------------------------------------ write / read

    /// <summary>Called from ConvertPit once the pile blocks are placed. GM only.</summary>
    public static void Remember(BlockPos pos, string collierName) => marks[Key(pos)] = collierName;

    public static void Forget(BlockPos pos) => marks.Remove(Key(pos));

    private static bool TryGet(IWorldAccessor world, BlockPos pos, out string name)
    {
        name = "";
        if (!marks.TryGetValue(Key(pos), out string? n) || n == null) return false;
        // Validate: only a live charcoal pile can carry a mark.
        if (world.BlockAccessor.GetBlock(pos) is not BlockCharcoalPile) { marks.Remove(Key(pos)); return false; }
        name = n;
        return true;
    }

    // ------------------------------------------------------------------ patches

    public static void PatchAll(ICoreAPI api, Harmony harmony)
    {
        var getDrops = AccessTools.Method(typeof(Block), nameof(Block.GetDrops));
        var combustible = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetCombustibleProperties));
        var fallOnto = AccessTools.Method(typeof(BlockCharcoalPile), nameof(BlockCharcoalPile.OnFallOnto));
        var heldInfo = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo));

        if (getDrops == null || combustible == null)
        {
            TcmLog.Warn(api, "charcoal drop/combustible seams not found; Collier's Mark inactive");
            return;
        }

        harmony.Patch(getDrops, postfix: new HarmonyMethod(AccessTools.Method(typeof(StampPatch), "Postfix")));
        harmony.Patch(combustible, postfix: new HarmonyMethod(AccessTools.Method(typeof(HonorPatch), "Postfix")));
        if (fallOnto != null)
        {
            harmony.Patch(fallOnto, prefix: new HarmonyMethod(AccessTools.Method(typeof(AdulteratePatch), "Prefix")));
        }
        if (heldInfo != null)
        {
            harmony.Patch(heldInfo, postfix: new HarmonyMethod(AccessTools.Method(typeof(TooltipPatch), "Postfix")));
        }
        TcmLog.Info(api, "Collier's Mark hooked (pile drops -> stamp, firepit -> honor)");
    }

    /// <summary>Stamps the burner onto charcoal as it leaves the pile. NOTE the pile breaks ONE
    /// LAYER PER BREAK (BlockLayeredSlowDig.OnBlockBroken → GetDrops → SetBlock(prevLayer)), so an
    /// 8-layer pile drops eight times at the same position: the entry must survive every one of
    /// them. It is retired by Prune once the pile is actually gone, never here.</summary>
    public static class StampPatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, ItemStack[] __result)
        {
            if (world?.Side != EnumAppSide.Server || __result == null) return;
            if (__instance is not BlockCharcoalPile) return;
            if (!TryGet(world, pos, out string name)) return;

            foreach (ItemStack stack in __result)
            {
                if (stack?.Collectible == null) continue;
                stack.Attributes.SetString(MarkAttr, name);
            }
        }
    }

    /// <summary>Honors the Mark at burn time. The firepit is the one site that reads BOTH heat and
    /// duration stack-aware (`igniteWithFuel`: maxTemperature = BurnTemperature × HeatModifier,
    /// fuelBurnTime = BurnDuration × BurnDurationModifier) — forge/bloomery/coal-pile are gate-only
    /// or time-fixed, so the Mark is deliberately a firepit premium.
    ///
    /// **CLONE FIRST.** The base returns the collectible's SHARED CombustibleProps instance;
    /// mutating it would permanently boost every piece of charcoal in the world for every player
    /// until restart.</summary>
    public static class HonorPatch
    {
        public static void Postfix(ItemStack itemstack, ref CombustibleProperties __result)
        {
            if (__result == null || itemstack == null) return;
            if (!itemstack.Attributes.HasAttribute(MarkAttr)) return;

            CombustibleProperties boosted = __result.Clone();
            boosted.BurnTemperature += (int)WooDomain.Knob(WooDomain.MarkBurnTempBonus, 100);
            boosted.BurnDuration *= (float)WooDomain.Knob(WooDomain.MarkBurnDurationMul, 1.2);
            __result = boosted;
        }
    }

    /// <summary>A pile falling onto a marked pile adulterates it. We do not chase the mark onto the
    /// falling entity, so this fails toward unmarked by design.</summary>
    public static class AdulteratePatch
    {
        public static void Prefix(IWorldAccessor world, BlockPos pos)
        {
            if (world?.Side == EnumAppSide.Server && pos != null) Forget(pos);
        }
    }

    public static class TooltipPatch
    {
        public static void Postfix(ItemSlot inSlot, System.Text.StringBuilder dsc)
        {
            string? name = inSlot?.Itemstack?.Attributes?.GetString(MarkAttr);
            if (!string.IsNullOrEmpty(name)) dsc.AppendLine(Lang.Get("almanactcm:colliers-mark", name));
        }
    }
}
