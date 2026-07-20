using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// RAN Phase 3 — the firearms layer (rank-bonus-design §RAN CO layer rungs 7 + 8, RULED
/// 2026-07-11). dependsOn firearmsfork; both seams sit on Firearms.MuzzleloaderServer, which
/// carries musket, matchlock, arquebus AND pistol (PistolServer.Shoot calls base.Shoot, FA:4687;
/// MusketServer overrides only Reload and falls to base for the powder stages). The revolver
/// (RevolverServer, its own chamber system) is deferred to tuning.
///
/// Misfire — an INTRODUCED failure, stated plainly: FA guns fire deterministically (the server
/// Shoot rolls no chance, FA:4337), so the Untrained flash-in-the-pan is Copybook's own, and it
/// is rank-reduced but NEVER eliminated (GM floor ruled 2026-07-11: period and modern firearms
/// misfire on bad luck). A flash cancels the CURRENT pull only — flintlock snap, no discharge,
/// piece stays primed, next pull re-rolls. The gun's loading stage is deliberately untouched
/// (see the prefix note: FA's client ignores server rejections, so any server-side stage
/// change strands the client in an unfireable-but-firing loop). Skipping the original is
/// safe: the OLC base Shoot only marks the stack (OLC:30419).
///
/// Powder/wadding thrift — the firearms MET-fuel twin: at reload/prime completion a
/// rank-weighted roll refunds what the reload just consumed (flask powder durability, wadding
/// count), captured before/after so requirement checks stay honest — a player with no wadding
/// can never lucky-roll past needing it. Zero through Apprentice I, capped below certainty.
/// </summary>
public static class RanFirearmsPatches
{
    private static readonly Random rand = new();
    private static MethodInfo? getFlask;
    private static MethodInfo? getWadding;

    /// <summary>Stage attribute written by FA's SetLoadingStage (FA:4447). Values are the
    /// MuzzleloaderLoadingStage enum: 0 Unloaded, 1 Loading, 2 Priming (= ready to fire;
    /// the musket's extended enum shares the first three values, FA:1675/:2461).</summary>
    private const string StageAttr = "CombatOverhaul:loading-stage";
    private const int StagePrimed = 2;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("firearmsfork")) return;

        var t = AccessTools.TypeByName("Firearms.MuzzleloaderServer");
        var shotPacket = AccessTools.TypeByName("CombatOverhaul.RangedSystems.ShotPacket");
        var reloadPacket = AccessTools.TypeByName("CombatOverhaul.RangedSystems.ReloadPacket");
        // Signatures spelled out: the OLC base carries (ICoreAPI, ...) overloads of both
        // methods, and a name-only lookup is an ambiguous match (the 0.3.117 failure that
        // left the whole layer inactive).
        var shoot = t == null || shotPacket == null ? null
            : AccessTools.DeclaredMethod(t, "Shoot",
                new[] { typeof(IServerPlayer), typeof(ItemSlot), shotPacket, typeof(Entity) });
        var reload = t == null || reloadPacket == null ? null
            : AccessTools.DeclaredMethod(t, "Reload",
                new[] { typeof(IServerPlayer), typeof(ItemSlot), typeof(ItemSlot), reloadPacket });
        if (shoot == null || reload == null)
        {
            TcmLog.Warn(api, "firearmsfork present but MuzzleloaderServer.Shoot/Reload not found; RAN firearms layer inactive");
            return;
        }
        getFlask = AccessTools.Method(t, "GetFlask");
        getWadding = AccessTools.Method(t, "GetWadding");

        harmony.Patch(shoot, prefix: new HarmonyMethod(AccessTools.Method(typeof(MisfirePatch), "Prefix")));
        harmony.Patch(reload,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ThriftPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ThriftPatch), "Postfix")));
        TcmLog.Info(api, "RAN firearms layer hooked (muzzleloader misfire + powder/wadding thrift; revolver deferred)");
    }

    // ------------------------------------------------------------ the flash in the pan

    public static class MisfirePatch
    {
        public static bool Prefix(IServerPlayer player, ItemSlot slot, ref bool __result)
        {
            var sapi = (player?.Entity?.World?.Api) as ICoreServerAPI;
            if (sapi == null || slot?.Itemstack == null) return true;

            // Only a primed piece can flash; anything else falls through to the original's
            // own stage rejection.
            if (slot.Itemstack.Attributes.GetInt(StageAttr, 0) != StagePrimed) return true;

            int level = RanDomain.LevelOf(player);
            double chance = RanDomain.MisfireChance(level);
            if (rand.NextDouble() >= chance) return true;

            // The flash cancels THIS pull only; the piece stays primed and the next pull
            // re-rolls. The stage must NOT be knocked back: FA's client ignores server
            // rejections entirely (ShootServerCallback is empty, FA:3760) and its in-memory
            // MuzzleloaderState decides fire-eligibility from the magazine, not the loading
            // stage - a server-side stage change strands the client hammering a trigger the
            // server always refuses (the 0.3.118 infinite-blank-fire bug).
            sapi.World.PlaySoundAt(new AssetLocation("maltiezfirearms", "sounds/musket/flintlock-strike"),
                player!.Entity, null, randomizePitch: true, 24f);
            player.SendMessage(GlobalConstants.InfoLogChatGroup,
                Lang.GetL(player.LanguageCode, "almanactcm:misfire-flash"), EnumChatType.Notification);
            TcmLog.Cat(sapi, "ran", $"{player.PlayerName}: flash in the pan (RAN {level}, chance {chance:P1})");

            __result = false;
            return false;
        }
    }

    // ------------------------------------------------------------ powder / wadding thrift

    public class ThriftState
    {
        public ItemSlot? Flask;
        public int FlaskDur;
        public ItemSlot? Wadding;
        public int WaddingSize;
    }

    public static class ThriftPatch
    {
        public static void Prefix(object __instance, IServerPlayer player, out ThriftState __state)
        {
            __state = new ThriftState();
            try
            {
                if (getFlask != null
                    && getFlask.Invoke(__instance, new object[] { player, 1 }) is ItemSlot f
                    && f.Itemstack != null)
                {
                    __state.Flask = f;
                    __state.FlaskDur = f.Itemstack.Collectible.GetRemainingDurability(f.Itemstack);
                }
                if (getWadding != null
                    && getWadding.Invoke(__instance, new object[] { player }) is ItemSlot w
                    && w.Itemstack != null)
                {
                    __state.Wadding = w;
                    __state.WaddingSize = w.Itemstack.StackSize;
                }
            }
            catch (Exception)
            {
                // A capture failure only means no refund this reload; never block the reload.
                __state.Flask = null;
                __state.Wadding = null;
            }
        }

        public static void Postfix(IServerPlayer player, bool __result, ThriftState __state)
        {
            if (!__result || __state == null) return;
            var sapi = (player?.Entity?.World?.Api) as ICoreServerAPI;
            if (sapi == null) return;

            int level = RanDomain.LevelOf(player);
            var flaskStack = __state.Flask?.Itemstack;
            bool powderSpent = flaskStack != null
                && flaskStack.Collectible.GetRemainingDurability(flaskStack) < __state.FlaskDur;

            // The penalty half (below Apprentice I): the clumsy overpour spills an extra
            // unit on top of what the reload consumed. Only fires when powder was actually
            // poured this stage, and never empties past zero.
            double spill = RanDomain.SpillChance(level);
            if (spill > 0)
            {
                if (powderSpent && rand.NextDouble() < spill)
                {
                    int now = flaskStack!.Collectible.GetRemainingDurability(flaskStack);
                    if (now > 0)
                    {
                        flaskStack.Attributes.SetInt("durability", now - 1);
                        __state.Flask!.MarkDirty();
                        player!.SendMessage(GlobalConstants.InfoLogChatGroup,
                            Lang.GetL(player.LanguageCode, "almanactcm:powder-spilled"), EnumChatType.Notification);
                        TcmLog.Cat(sapi, "ran", $"{player.PlayerName}: clumsy pour spilled powder (RAN {level}, chance {spill:P1})");
                    }
                }
                return; // the bands never overlap: no thrift below Apprentice I
            }

            // The bonus half (above Apprentice I): the measured charge spends nothing.
            double chance = RanDomain.ThriftChance(level);
            if (chance <= 0 || rand.NextDouble() >= chance) return;

            bool spared = false;
            if (powderSpent)
            {
                flaskStack!.Attributes.SetInt("durability", __state.FlaskDur);
                __state.Flask!.MarkDirty();
                spared = true;
            }
            var wadStack = __state.Wadding?.Itemstack;
            if (wadStack != null && wadStack.StackSize < __state.WaddingSize)
            {
                wadStack.StackSize = __state.WaddingSize;
                __state.Wadding!.MarkDirty();
                spared = true;
            }
            if (!spared) return; // this reload stage consumed nothing capturable

            player!.SendMessage(GlobalConstants.InfoLogChatGroup,
                Lang.GetL(player.LanguageCode, "almanactcm:powder-spared"), EnumChatType.Notification);
            TcmLog.Cat(sapi, "ran", $"{player.PlayerName}: thrift spared the reload (RAN {level}, chance {chance:P1})");
        }
    }
}
