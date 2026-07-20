using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// MEL Phase 1 hooks — the blocking verb (technique-maps §MEL #2, ADOPTED by ruling).
///
/// Under CO (The Quire): a postfix on MeleeBlockSystemServer.EmitDamageBlocked — the one
/// server-side event every successful block AND parry funnels through (fired from
/// PlayerDamageModelBehavior.ApplyBlock after the zone/cone/tier gates passed, OLC:35767).
/// Reading, never touching, per gotcha #14: CO weapon/block state stays untouched.
///
/// Vanilla floor (CO absent): a postfix on ModSystemWearableStats.applyShieldProtection,
/// granting when the shield actually absorbed damage.
///
/// Both fenced the ruled way: the attacker must be a hostile creature (never a player,
/// never nothing), and the dedup context is the ATTACKER entity — tanking one caged
/// drifter collapses to a single context inside the window.
/// </summary>
public static class MelPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    private static PropertyInfo? argPlayer;
    private static PropertyInfo? argSource;
    private static PropertyInfo? argBlocked;
    private static PropertyInfo? argKind;

    private static bool coPresent;
    private static Type? meleeWeaponBehavior; // CombatOverhaul MeleeWeaponBehavior (stamp gate)

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        coPresent = api.ModLoader.IsModEnabled("combatoverhaulfork");
        if (coPresent)
            meleeWeaponBehavior = AccessTools.TypeByName("CombatOverhaul.Implementations.MeleeWeaponBehavior");
        api.Event.RegisterGameTickListener(ReconcileMelStats, 2000);
    }

    // ------------------------------------------------------------ Phase 2 stat reconcile

    private static readonly Dictionary<string, (double dmg, double armor)> lastStats = new();

    /// <summary>The penalty dock (meleeWeaponsDamage, Untrained only) and Master-at-Arms armor
    /// familiarity (armorWalkSpeedAffectedness + CO's manipulation/hunger set), reconciled on a
    /// slow tick like HUN/RAN. Zero-Harmony stat writes; our rank delta stacks on CO class
    /// traits, never re-scaling them (principle 4, the steadyAim posture).</summary>
    private static void ReconcileMelStats(float dt)
    {
        if (sapi == null) return;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player.Entity;
            if (entity == null) continue;
            int level = MelDomain.LevelOf(player);

            // The defensive tier stamp rides the HELD guard, which changes without the level
            // changing, so it stamps every tick (outside the change-gate). CO composes it into
            // the block/parry tier at block start (OLC:19809/:19825).
            if (coPresent) StampGuard(player, MelDomain.TierBonus(level));

            double dmg = MelDomain.NoviceFactor(level,
                MelDomain.Knob(MelDomain.DamageUntrained, 0.85), 1.0);
            double armor = MelDomain.NoviceDelta(level,
                MelDomain.Knob(MelDomain.ArmorUntrained, 0.30),
                MelDomain.Knob(MelDomain.ArmorGm, -0.50));

            if (lastStats.TryGetValue(player.PlayerUID, out var prev)
                && Math.Abs(prev.dmg - dmg) < 0.001 && Math.Abs(prev.armor - armor) < 0.001) continue;

            entity.Stats.Set("meleeWeaponsDamage", "almanactcm", (float)(dmg - 1.0), false);
            // The affectedness stat multiplies armor's own walkspeed penalty (blended default 1;
            // lower = less drag, 0 = the unarmored baseline). Our delta shifts it; a CO class
            // trait, if any, stacks. The GM delta (-0.5) alone leaves blended at 0.5 — still a
            // real penalty, never inverted into a speed buff; only a stacked class trait reaches
            // the baseline, which is that class's own identity (a blackguard shrugs off plate).
            entity.Stats.Set("armorWalkSpeedAffectedness", "almanactcm", (float)armor, false);
            if (coPresent)
            {
                entity.Stats.Set("armorManipulationSpeedAffectedness", "almanactcm", (float)armor, false);
                entity.Stats.Set("armorHungerRateAffectedness", "almanactcm", (float)armor, false);
            }
            lastStats[player.PlayerUID] = (dmg, armor);
        }
    }

    /// <summary>Stamps the defensive block/parry tier bonus on the held CO melee weapon's stack
    /// (the reloadSpeed-stamp pattern). CO reads these per-stack int attributes when composing the
    /// block/parry tier (ItemStackMeleeWeaponStats, OLC:22266). DEFENSIVE only — never touches
    /// GetToolTier, damage, or armor tier. Gated to CO melee weapons so no other item is polluted.</summary>
    private static void StampGuard(IServerPlayer player, int tier)
    {
        var slot = player.InventoryManager?.ActiveHotbarSlot;
        var stack = slot?.Itemstack;
        if (stack?.Collectible == null) return;
        if (meleeWeaponBehavior == null || !HasBehavior(stack, meleeWeaponBehavior))
        {
            return; // not a CO melee weapon: nothing parries with it
        }
        if (stack.Attributes.GetInt("blockTierBonus", 0) == tier
            && stack.Attributes.GetInt("parryTierBonus", 0) == tier) return;
        stack.Attributes.SetInt("blockTierBonus", tier);
        stack.Attributes.SetInt("parryTierBonus", tier);
        slot!.MarkDirty();
    }

    private static bool HasBehavior(ItemStack stack, Type behaviorType)
    {
        foreach (var b in stack.Collectible.CollectibleBehaviors)
            if (behaviorType.IsInstanceOfType(b)) return true;
        return false;
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (api.ModLoader.IsModEnabled("combatoverhaulfork"))
        {
            var sys = AccessTools.TypeByName("CombatOverhaul.DamageSystems.MeleeBlockSystemServer")
                ?? FindType("MeleeBlockSystemServer"); // scan fallback if the fork moves it
            var m = sys == null ? null : AccessTools.Method(sys, "EmitDamageBlocked");
            if (m == null)
            {
                TcmLog.Warn(api, "CO present but MeleeBlockSystemServer.EmitDamageBlocked not found; MEL blocking verb inactive");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CoBlockPatch), "Postfix")));
            TcmLog.Info(api, "MEL blocking verb hooked to CO block/parry events");
        }
        else
        {
            var t = AccessTools.TypeByName("Vintagestory.GameContent.ModSystemWearableStats");
            var m = t == null ? null : AccessTools.Method(t, "applyShieldProtection");
            if (m == null)
            {
                TcmLog.Warn(api, "vanilla shield seam not found; MEL blocking verb inactive");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(VanillaShieldPatch), "Postfix")));
            TcmLog.Info(api, "MEL blocking verb hooked to vanilla shield absorbs (CO absent)");
        }
    }

    private static Type? FindType(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asm.GetTypes())
                    if (t.Name == shortName) return t;
            }
            catch (ReflectionTypeLoadException) { }
        }
        return null;
    }

    /// <summary>The hostile-aggressor fence + the grant, shared by both seams.</summary>
    private static void GrantBlock(IPlayer defender, Entity? attackerSource, bool parry)
    {
        if (sapi == null || defender == null) return;
        Entity? attacker = attackerSource;
        if (attacker is not EntityAgent || attacker is EntityPlayer) return; // hostile creatures only

        double mult = parry ? MelDomain.Knob(MelDomain.RawParryMul, 1.5) : 1.0;
        int ctx = HashCode.Combine("block", attacker.EntityId);
        Core?.Ledger?.Log(defender, MelDomain.Code, MelDomain.TechBlocking, ctx, mult);
    }

    // ------------------------------------------------------------ CO seam

    public static class CoBlockPatch
    {
        public static void Postfix(object args)
        {
            if (args == null || sapi == null) return;
            var t = args.GetType();
            argPlayer ??= t.GetProperty("Player");
            argSource ??= t.GetProperty("DamageSource");
            argBlocked ??= t.GetProperty("DamageBlocked");
            argKind ??= t.GetProperty("Kind");
            if (argPlayer == null || argSource == null) return;

            float blocked = argBlocked == null ? 0f : Convert.ToSingle(argBlocked.GetValue(args));
            if (blocked <= 0f) return; // a zero-damage tap teaches nothing

            var defender = (argPlayer.GetValue(args) as EntityPlayer)?.Player;
            var source = argSource.GetValue(args) as DamageSource;
            Entity? attacker = source?.GetCauseEntity() ?? source?.SourceEntity;
            bool parry = argKind != null
                && string.Equals(argKind.GetValue(args)?.ToString(), "Parry", StringComparison.OrdinalIgnoreCase);

            if (defender != null) GrantBlock(defender, attacker, parry);
        }
    }

    // ------------------------------------------------------------ vanilla floor

    public static class VanillaShieldPatch
    {
        public static void Postfix(IPlayer player, float damage, DamageSource dmgSource, float __result)
        {
            if (sapi == null || player?.Entity?.World?.Side != EnumAppSide.Server) return;
            if (__result >= damage) return; // nothing absorbed, no block happened
            Entity? attacker = dmgSource?.GetCauseEntity() ?? dmgSource?.SourceEntity;
            GrantBlock(player, attacker, parry: false);
        }
    }
}
