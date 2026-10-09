using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// ANI riding — practice for ground actually covered in the saddle (RULED 2026-10-08, LGD-176).
///
/// The ruling's condition is the whole design: riding pays ONLY against real displacement,
/// because the obvious farm is a weight on the W key with the horse's nose in a corner. This is
/// the Conjunction excursion ledger's shape (Tether.Refresh): sample the rider's position on a
/// timer, pay the horizontal distance actually crossed since the last sample, and discard any
/// single step no ride could take (a teleport or a lag snap moved the player, but they did not
/// TRAVEL it). A horse pushed against a wall produces zero displacement and therefore zero
/// practice, with no failure model needed — the world already refuses the cheese.
///
/// Only the CONTROLLING seat earns (seat.CanControl): handling is the handler's practice, and a
/// passenger in a cart is cargo. The mount must carry vanilla's EntityBehaviorRideable, which is
/// the saddle-break line this domain already pays — a boat is not an animal and a loom is not a
/// mount. Jaunt-line mounts that ride through their own behavior class are a known gap, listed
/// in PENDING rather than guessed at.
/// </summary>
public static class AniRiding
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    private const int TickMs = 1000;

    /// <summary>A step no ride could take in one sample (vanilla gallop is ~9 blocks/s):
    /// a teleport or lag snap, paid as zero — the Conjunction MaxStepPerSample posture.</summary>
    private const double MaxStepPerTick = 12.0;

    /// <summary>uid -> (last sampled x/z, blocks banked toward the next credit).</summary>
    private static readonly Dictionary<string, (double x, double z, double banked)> riders = new();

    /// <summary>Monotonic context salt (the 0.5.15 station convention): every earned credit is
    /// its own act; the K cap governs the day, never the dedup ring.</summary>
    private static int rideSeq;

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.RegisterGameTickListener(OnTick, TickMs);
        api.Event.PlayerDisconnect += p => { if (p?.PlayerUID != null) riders.Remove(p.PlayerUID); };
    }

    private static void OnTick(float dt)
    {
        if (sapi == null) return;
        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player?.Entity;
            string? uid = player?.PlayerUID;
            if (entity == null || uid == null) continue;

            var seat = entity.MountedOn;
            var mount = seat?.Entity;
            bool riding = seat?.CanControl == true
                && mount != null && mount != entity
                && mount.GetBehavior<EntityBehaviorRideable>() != null;
            if (!riding)
            {
                riders.Remove(uid); // dismount ends the leg; a new mount anchors fresh
                continue;
            }

            double x = entity.Pos.X, z = entity.Pos.Z;
            if (!riders.TryGetValue(uid, out var r))
            {
                riders[uid] = (x, z, 0); // first sample in the saddle: anchor, pay nothing
                continue;
            }

            double dx = x - r.x, dz = z - r.z;
            double step = Math.Sqrt(dx * dx + dz * dz);
            if (step > MaxStepPerTick) step = 0;

            double banked = r.banked + step;
            double per = AniDomain.Knob(AniDomain.RideCreditBlocks, 50);
            if (per > 0 && banked >= per)
            {
                int credits = (int)(banked / per);
                banked -= credits * per;
                Core?.Ledger?.Log(player!, AniDomain.Code, AniDomain.TechRiding,
                    HashCode.Combine("ride", System.Threading.Interlocked.Increment(ref rideSeq)),
                    credits);
            }
            riders[uid] = (x, z, banked);
        }
    }
}
