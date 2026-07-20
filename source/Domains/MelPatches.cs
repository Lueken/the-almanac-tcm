using System;
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

    public static void RegisterServer(ICoreServerAPI api) => sapi = api;

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
