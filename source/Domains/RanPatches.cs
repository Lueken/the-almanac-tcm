using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// RAN Phase 2 — the rank levers (rank-bonus-design §RAN CO layer, RULED 2026-07-11; curve
/// anchor ruled 2026-07-18: vanilla parity at APPRENTICE I, see RanDomain.ApprenticeAnchored).
///
/// With Combat Overhaul present (The Quire):
///   • steadyAim — the CO accuracy spine, written CLIENT-side (RegisterClient below): CO
///     registers and reads the stat in its client aiming behavior, and that Register call
///     wipes server-synced values. ClientAimingSystem divides drift/twitch by steadyAim
///     squared, clamped 0.25-4 by the engine so sway never vanishes (OLC:30716); the 0.50
///     Untrained dock rides the clamp floor for the full 4x wobble.
///   • reloadSpeed — the nock/draw/reload handling lever (2026-07-10 amendment). Stamped as
///     a per-stack attribute on the HELD launcher (the seam ItemStackRangedStats reads,
///     OLC:24840); ranged-only by construction (melee swing rides a separate multiplier the
///     rank never writes). A traded launcher self-corrects the moment its new holder holds
///     it through a reconcile tick. Covers bow nock/draw AND firearm reload alike.
///   • ammo recovery — postfix on ProjectileSystemServer.SpawnProjectile scaling the freshly
///     spawned projectile's DropOnImpactChance by the shooter's rank. Multiplies the
///     projectile's OWN material-derived chance (flint still breaks more than steel at every
///     rank) and is absolute-capped below certainty — some arrows always shatter.
///
/// Without CO (public-release floor): zero-Harmony stat writes — rangedWeaponsAcc (kept
/// conservative; its aimingAccuracy read site is client-core, unverified in-assembly),
/// a tiny bowDrawingStrength climb, and rangedWeaponsSpeed on the same reload curve. The
/// vanilla arrow-recovery seam (ItemBow's DropOnImpactChance) is deferred: under CO the
/// vanilla bow path does not run, and The Quire runs CO.
/// </summary>
public static class RanPatches
{
    private static ICoreServerAPI? sapi;
    private static bool coPresent;
    private static Type? rangedWeaponIface; // CombatOverhaul.RangedSystems.IHasRangedWeaponLogic

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        coPresent = api.ModLoader.IsModEnabled("combatoverhaulfork");
        if (coPresent)
        {
            rangedWeaponIface = AccessTools.TypeByName("CombatOverhaul.RangedSystems.IHasRangedWeaponLogic");
            if (rangedWeaponIface == null)
                TcmLog.Warn(api, "CO present but IHasRangedWeaponLogic not found; RAN reloadSpeed stamp inactive");
        }
        api.Event.RegisterGameTickListener(ReconcileRanStats, 2000);
    }

    // ------------------------------------------------------------ stat reconcile

    private static readonly Dictionary<string, double> lastSteady = new();

    private static void ReconcileRanStats(float dt)
    {
        if (sapi == null) return;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player.Entity;
            if (entity == null) continue;
            int level = RanDomain.LevelOf(player);

            double reload = RanDomain.ApprenticeAnchored(level,
                RanDomain.Knob(RanDomain.ReloadUntrained, 0.75),
                RanDomain.Knob(RanDomain.ReloadGm, 1.12));

            // The held-launcher stamp must run every tick (the held ITEM changes without the
            // level changing); the vanilla-floor stat writes are change-gated.
            if (coPresent)
            {
                StampHeldLauncher(player, (float)reload);
                continue; // steadyAim is written CLIENT-side under CO (see RegisterClient)
            }

            if (!lastSteady.TryGetValue(player.PlayerUID, out double prev)
                || Math.Abs(prev - reload) >= 0.001)
            {
                double acc = RanDomain.ApprenticeAnchored(level,
                    RanDomain.Knob(RanDomain.VanAccUntrained, 0.90),
                    RanDomain.Knob(RanDomain.VanAccGm, 1.05));
                double draw = RanDomain.ApprenticeAnchored(level,
                    1.0, RanDomain.Knob(RanDomain.VanDrawGm, 1.05));
                entity.Stats.Set("rangedWeaponsAcc", "almanactcm", (float)(acc - 1.0), false);
                entity.Stats.Set("bowDrawingStrength", "almanactcm", (float)(draw - 1.0), false);
                entity.Stats.Set("rangedWeaponsSpeed", "almanactcm", (float)(reload - 1.0), false);
                lastSteady[player.PlayerUID] = reload;
            }
        }
    }

    // ------------------------------------------------------------ client steadyAim write

    private static float lastClientSteady = float.NaN;

    /// <summary>The CO accuracy spine runs on the CLIENT: CO registers steadyAim in its
    /// client aiming behavior and reads it there every aim tick — and that Register call
    /// REPLACES the stat category, wiping anything the server synced in before it (why the
    /// 0.3.113 server-side write showed no sway difference). So the client writes its own
    /// rank factor every second, unconditionally: a wipe heals within a tick, and a client
    /// Stats.Set is purely local (no network traffic). Curve endpoints are the compile
    /// defaults — the client cannot read RAN.json (server-side by design).</summary>
    public static void RegisterClient(Vintagestory.API.Client.ICoreClientAPI capi)
    {
        if (!capi.ModLoader.IsModEnabled("combatoverhaulfork")) return;
        capi.Event.RegisterGameTickListener(_ =>
        {
            var entity = capi.World.Player?.Entity;
            if (entity == null) return;
            float steady = (float)RanDomain.ApprenticeAnchored(RanDomain.ClientLevel(),
                RanDomain.Knob(RanDomain.SteadyAimUntrained, 0.50),
                RanDomain.Knob(RanDomain.SteadyAimGm, 1.35));
            entity.Stats.Set("steadyAim", "almanactcm", steady - 1f, false);
            if (Math.Abs(steady - lastClientSteady) >= 0.001f)
            {
                lastClientSteady = steady;
                TcmLog.Cat(capi, "ran", $"steadyAim {steady:0.###} (RAN level {RanDomain.ClientLevel()}, " +
                    $"drift x{1f / Math.Clamp(steady * steady, 0.25f, 4f):0.##})");
            }
        }, 1000);
    }

    /// <summary>Writes the rank reload factor onto the held CO launcher's stack. The stack
    /// attribute is the composition base ItemStackRangedStats reads; CO's own weapon buffs
    /// apply on top of it, untouched.</summary>
    private static void StampHeldLauncher(IServerPlayer player, float reload)
    {
        if (rangedWeaponIface == null) return;
        var slot = player.InventoryManager?.ActiveHotbarSlot;
        var stack = slot?.Itemstack;
        if (stack?.Collectible == null || !rangedWeaponIface.IsInstanceOfType(stack.Collectible)) return;
        if (Math.Abs(stack.Attributes.GetFloat("reloadSpeed", 1f) - reload) < 0.001f) return;
        stack.Attributes.SetFloat("reloadSpeed", reload);
        slot!.MarkDirty();
        TcmLog.Cat(sapi!, "ran", $"{player.PlayerName}: reloadSpeed {reload:0.###} stamped on {stack.Collectible.Code}");
    }

    // ------------------------------------------------------------ CO ammo recovery

    private static PropertyInfo? dropChanceProp;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("combatoverhaulfork")) return;

        var t = AccessTools.TypeByName("CombatOverhaul.RangedSystems.ProjectileSystemServer");
        var m = t == null ? null : AccessTools.Method(t, "SpawnProjectile");
        if (m == null)
        {
            TcmLog.Warn(api, "CO present but ProjectileSystemServer.SpawnProjectile not found; RAN ammo recovery inactive");
            return;
        }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(RecoveryPatch), "Postfix")));
        TcmLog.Info(api, "RAN ammo recovery hooked to CO projectile spawn");
    }

    /// <summary>Scales the spawned projectile's recovery chance by the shooter's RAN rank.
    /// Runs server-side after spawn, before any impact consumes DropOnImpactChance. The
    /// projectile parameter is CO's ProjectileEntity, held as object (soft dep) and touched
    /// through a cached PropertyInfo.</summary>
    public static class RecoveryPatch
    {
        public static void Postfix(Entity shooter, Entity owner, object? projectile)
        {
            if (projectile == null || sapi == null) return;
            IPlayer? player = (owner as EntityPlayer)?.Player ?? (shooter as EntityPlayer)?.Player;
            if (player == null) return;

            dropChanceProp ??= projectile.GetType().GetProperty("DropOnImpactChance");
            if (dropChanceProp == null) return;

            int level = RanDomain.LevelOf(player);
            double factor = RanDomain.ApprenticeAnchored(level,
                RanDomain.Knob(RanDomain.RecoveryUntrained, 0.80),
                RanDomain.Knob(RanDomain.RecoveryGm, 1.50));
            if (Math.Abs(factor - 1.0) < 0.001) return;

            float cur = (float)dropChanceProp.GetValue(projectile)!;
            float next = Math.Max(0f, cur * (float)factor);
            if (factor > 1.0)
            {
                // The cap binds only the rank BONUS: an arrow whose material chance already
                // exceeds it keeps its vanilla value, it is never nerfed down to the cap.
                float cap = (float)RanDomain.Knob(RanDomain.RecoveryCap, 0.90);
                next = Math.Min(next, Math.Max(cur, cap));
            }
            dropChanceProp.SetValue(projectile, next);
        }
    }
}
