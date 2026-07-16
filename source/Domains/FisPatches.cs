using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FIS vanilla hooks — the rod-and-line catch moment, all on one seam:
/// <c>EntityBobber.TryCatchFish(EntityAgent)</c> (the xSkills BobberCatchPatch precedent), which
/// vanilla calls exactly once per reel-in with a bite on. Three catch paths inside it: a live
/// caught EntityFish, the bait-abundance roll (where vanilla rolls fish SIZE: adult if
/// rand > abundance, else juvenile — and where the depletion counter increments), and junk.
///
/// What the prefix does, in order (2026-07-16 rulings):
///   1. THE ONE THAT GOT AWAY — Untrained only: a chance the bite escapes with the bait. The
///      ruled Axis 1 fumble spent as a loud story moment instead of a quiet yield shave.
///   2. The bait path is taken over (skip-original) to apply the RANK-SKEWED SIZE ROLL — a
///      master lands the adult — and the RANK-SCALED DEPLETION step (Untrained overfishes at
///      x1.5, GM light-touch at x0.5, probabilistically rounded, always at least possible:
///      a spot never becomes free). Vanilla's live-fish and junk paths run unmodified and are
///      credited by the postfix.
///
/// The takeover replicates ~20 stable lines of the bait branch (verified against 1.22 source);
/// if the private lookup it needs ever breaks, the patch logs once and drops back to vanilla
/// behaviour with plain credit, never a crash.
/// </summary>
public static class FisPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    private static double Knob(string key, double fallback) => FisDomain.Knob(key, fallback);

    private static readonly AccessTools.FieldRef<EntityBobber, EnumBobberState> bobberStateRef =
        AccessTools.FieldRefAccess<EntityBobber, EnumBobberState>("bobberState");

    private static readonly MethodInfo? getRandomFish =
        AccessTools.Method(typeof(EntityBobber), "getRandomFishEntityProperties");

    private static bool warnedFallback;

    public struct CatchState
    {
        public bool HandledInPrefix;
        public bool LiveBefore;
        public bool BaitBefore;
    }

    [HarmonyPatch(typeof(EntityBobber), nameof(EntityBobber.TryCatchFish))]
    public static class BobberCatchPatch
    {
        public static bool Prefix(EntityBobber __instance, EntityAgent entityCatcher, out CatchState __state)
        {
            __state = default;
            if (__instance.Api?.Side != EnumAppSide.Server) return true;
            IPlayer? player = (entityCatcher as EntityPlayer)?.Player;
            if (player == null) return true; // non-player catcher: vanilla untouched

            bool live = __instance.caughtFish != null && __instance.caughtFish.Alive;
            EnumBobberState state = bobberStateRef(__instance);
            bool biteOn = live || state == EnumBobberState.NoEntityFishCatch || state == EnumBobberState.JunkCatch;
            __state.LiveBefore = live;
            __state.BaitBefore = __instance.BaitStack != null;
            if (!biteOn) return true; // empty reel: nothing to fumble, nothing to credit

            int level = FisDomain.LevelOf(player);
            var rand = __instance.Api.World.Rand;

            // 1. The one that got away (Untrained only, ruled). The bait goes with it.
            if (level <= 0 && rand.NextDouble() < Knob(FisDomain.EscapeChanceUntrained, 0.25))
            {
                __instance.BaitStack = null;
                __instance.WatchedAttributes.MarkPathDirty("baitStack");
                if (live) __instance.caughtFish!.Die(EnumDespawnReason.Expire); // it swims off; no drops
                (player as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL((player as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:fish-escaped"),
                    EnumChatType.Notification);
                __state.HandledInPrefix = true;
                return false;
            }

            // 2. Bait-path takeover: size skew + scaled depletion. Live/junk stay vanilla.
            if (live || state != EnumBobberState.NoEntityFishCatch) return true;
            if (getRandomFish == null) return FallBack(__instance.Api, "getRandomFishEntityProperties not found");

            EntityProperties? etype;
            float abundance;
            try
            {
                object?[] args = { __instance.BaitStack, null, false };
                etype = (EntityProperties?)getRandomFish.Invoke(__instance, args);
                abundance = (float)args[1]!;
            }
            catch (Exception e) { return FallBack(__instance.Api, e.Message); }

            __state.HandledInPrefix = true;
            if (etype == null) return false; // vanilla would do nothing here either (bait kept)

            __instance.BaitStack = null;
            __instance.WatchedAttributes.MarkPathDirty("baitStack");

            // Vanilla: P(adult) = 1 - abundance. Ruled: rank skews it — Untrained leans juvenile,
            // a GM lands the adult. Clamped so neither size is ever guaranteed.
            double adultChance = GameMath.Clamp(
                (1.0 - abundance) + FisDomain.SkewFor(level,
                    Knob(FisDomain.SizeSkewUntrained, -0.15), Knob(FisDomain.SizeSkewGm, 0.35)),
                0.05, 0.95);
            string age = rand.NextDouble() < adultChance ? "adult" : "juvenile";

            var collObj = etype.Drops[0].ResolvedItemstack.Collectible;
            CollectibleObject fishItem = __instance.Api.World.GetItem(collObj.CodeWithVariant("age", age)) ?? collObj;
            ItemStack dropStack = new(fishItem);
            dropStack.ResolveBlockOrItem(__instance.Api.World);
            if (!entityCatcher.TryGiveItemStack(dropStack))
            {
                __instance.World.SpawnItemEntity(dropStack, entityCatcher.Pos.XYZ);
            }

            // Rank-scaled depletion on vanilla's own counter (public AddHarvest). Probabilistic
            // rounding keeps fractional factors honest; the GM floor still depletes on the roll,
            // so no spot is ever free (principle 3).
            double factor = FisDomain.RankLinear(level,
                Knob(FisDomain.DepletionUntrained, 1.5), Knob(FisDomain.DepletionGm, 0.5));
            int amount = (int)factor;
            if (rand.NextDouble() < factor - amount) amount++;
            if (amount > 0)
            {
                __instance.Api.ModLoader.GetModSystem<ModSystemFishDepletion>()
                    ?.AddHarvest(__instance.Pos.XYZ.AsBlockPos, amount);
            }

            Credit(player, __instance);
            return false;
        }

        /// <summary>Credits the vanilla-handled paths: a live fish taken (it died in the call) or
        /// a junk pull (the bait went from set to gone). The bait path credits in the prefix.</summary>
        public static void Postfix(EntityBobber __instance, EntityAgent entityCatcher, CatchState __state)
        {
            if (__state.HandledInPrefix || __instance.Api?.Side != EnumAppSide.Server) return;
            IPlayer? player = (entityCatcher as EntityPlayer)?.Player;
            if (player == null) return;

            bool liveTaken = __state.LiveBefore && (__instance.caughtFish == null || !__instance.caughtFish.Alive);
            bool junkTaken = !__state.LiveBefore && __state.BaitBefore && __instance.BaitStack == null;
            if (liveTaken || junkTaken) Credit(player, __instance);
        }

        private static bool FallBack(ICoreAPI api, string why)
        {
            if (!warnedFallback)
            {
                warnedFallback = true;
                TcmLog.Warn(api, $"FIS bait-path takeover unavailable ({why}); size skew + scaled depletion inactive, vanilla catch + credit only");
            }
            return true; // run vanilla; the postfix still credits
        }
    }

    private static void Credit(IPlayer player, EntityBobber bobber)
    {
        Core?.Ledger?.Log(player, FisDomain.Code, FisDomain.TechAngling,
            HashCode.Combine(FisDomain.TechAngling, bobber.World.ElapsedMilliseconds / 1000));
    }
}
