using System;
using System.Reflection;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// The TEM → Conjunction tether bridge (wired 2026-09-02, with the ring lock). When The
/// Marginalia: Conjunction is installed, a player's Anchorhold reach is their TEM level
/// run through Conjunction's ruling-34 radius ladder; without this bridge every player
/// stands at the Untrained floor no matter their training, which is the bug this file
/// exists to close.
///
/// OWNERSHIP SPLIT, deliberate: TCM knows LEVELS, Conjunction knows RADII. The ladder
/// itself lives in Conjunction (<c>Tether.RadiusForLevel</c>, next to the ring table it
/// feeds), so retuning tether balance is a Conjunction release and never a TCM one. All
/// TCM contributes is <see cref="TemDomain.LevelOf"/>.
///
/// Reflection, not a reference — the Marginalia suite and the Almanac keep their
/// soft-integration posture (neither assembly references the other), so this resolves
/// Conjunction's seam by name the same way <c>LevelingServer.IsLoginProtected</c> and
/// <c>TemStormShift.PatchConditional</c> resolve theirs: once, warn-and-skip on any
/// missing member, try/catch on the assignment. The seam was declared as
/// <c>System.Func&lt;string,int&gt;</c> — a BCL type both assemblies already share —
/// precisely so this assignment needs no common type of ours.
///
/// This is the second TEM↔Conjunction seam and it runs OPPOSITE to the first:
/// <see cref="TemManifestResist.TryShrugOff"/> is Conjunction-calls-TCM; this one is
/// TCM-writes-into-Conjunction, because Conjunction owns the tether and only asks who
/// the player is. Both directions stay optional at runtime.
///
/// Conjunction calls the provider on its 250ms refresh tick for ONLINE players only
/// (its own doc: "called on the refresh tick only, never from a handler"), and
/// <c>PlayerByUid</c> on an offline UID returns null, which <see cref="TemDomain.LevelOf"/>
/// answers with level 0 = Untrained — degraded, never thrown. Conjunction additionally
/// catches everything the provider might do and falls back to its Untrained constant,
/// so no failure mode here can break stability.
/// </summary>
public static class TemTetherBridge
{
    private const string ConjunctionModId = "themarginaliaconjunction";
    private const string TetherTypeName = "MarginaliaConjunction.Anchorhold.Tether";

    /// <summary>
    /// The wayfaring exchange rate: weighted frontier blocks (Conjunction's number —
    /// blocks walked outside the wall × the ring's weight, 1/2/4/8/16 by ring) per 1.0
    /// of the technique's Raw. At 4000, a two-league ring-1 forage loop earns ~0.25
    /// kills' worth of practice and a real ring-3 expedition lands around two — the
    /// walk pays like the work, never better than the fighting. [TUNE]
    /// </summary>
    public const double WayfaringWeightPerRaw = 4000.0;

    /// <summary>Multiplier ceiling per homecoming, so a ring-5 deathmarch cannot mint a
    /// week of practice in one crossing (the deep already pays in kills and finds).
    /// 12 × Raw 2 ≈ six temporal kills. [TUNE]</summary>
    public const double WayfaringMultCap = 12.0;

    private static FieldInfo? providerField;
    private static Func<string, int>? installed;
    private static FieldInfo? rewardField;
    private static Action<string, double>? installedReward;

    /// <summary>Server-side only, from StartServerSide after the LevelingServer exists
    /// (the provider dereferences it through <see cref="TemDomain.LevelOf"/>).</summary>
    public static void Wire(ICoreServerAPI sapi)
    {
        if (!sapi.ModLoader.IsModEnabled(ConjunctionModId)) return;

        try
        {
            Type? tether = HarmonyLib.AccessTools.TypeByName(TetherTypeName);
            // GetField, not GetProperty: RadiusProvider is a public static FIELD, and
            // GetProperty would return null silently — the exact quiet failure this
            // resolve-once block is written to catch and name.
            FieldInfo? field = tether?.GetField("RadiusProvider", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? radiusFor = tether?.GetMethod("RadiusForLevel", BindingFlags.Public | BindingFlags.Static);

            if (tether == null || field == null || radiusFor == null
                || field.FieldType != typeof(Func<string, int>))
            {
                TcmLog.Warn(sapi,
                    "TEM tether bridge: Conjunction is enabled but its Tether seam is not as verified " +
                    "(0.3.0-dev); the bridge is inactive and every player keeps the Untrained reach");
                return;
            }

            var radiusForLevel = (Func<int, int>)radiusFor.CreateDelegate(typeof(Func<int, int>));
            installed = uid => radiusForLevel(TemDomain.LevelOf(sapi.World.PlayerByUid(uid)));
            providerField = field;
            field.SetValue(null, installed);

            // The homecoming listener (ruled 2026-09-02): Conjunction reports each
            // survived excursion's depth-weighted frontier distance the tick the player
            // crosses back inside their wall; TCM prices it as wayfaring practice.
            // Optional member — an older Conjunction without the seam still gets the
            // radius half above, which is why this resolve does not fail the wire.
            FieldInfo? reward = tether.GetField("HomecomingReward", BindingFlags.Public | BindingFlags.Static);
            if (reward != null && reward.FieldType == typeof(Action<string, double>))
            {
                installedReward = (uid, weighted) => AwardHomecoming(sapi, uid, weighted);
                rewardField = reward;
                reward.SetValue(null, installedReward);
            }

            TcmLog.Info(sapi,
                $"TEM tether bridge wired: reach = Conjunction's ladder over TEM level " +
                $"(0 -> {radiusForLevel(0)}, 17 -> {radiusForLevel(17)}); " +
                $"homecoming wayfaring {(rewardField != null ? $"listening (1.0 raw per {WayfaringWeightPerRaw:F0} weighted blocks, cap {WayfaringMultCap:F0})" : "seam absent")}");
        }
        catch (Exception e)
        {
            providerField = null;
            installed = null;
            rewardField = null;
            installedReward = null;
            TcmLog.Error(sapi, $"TEM tether bridge failed to wire ({e.Message}); players keep the Untrained reach");
        }
    }

    /// <summary>
    /// One homecoming, priced. The multiplier is the weighted distance over the
    /// exchange rate, capped; the ledger's own saturation (K) and the daily
    /// consolidation do the rest, exactly as they do for kills. The context hash is
    /// deliberately unique per event — dedup exists for spam-clicking a block, and an
    /// excursion is self-limited by the walk itself.
    /// </summary>
    private static void AwardHomecoming(ICoreServerAPI sapi, string uid, double weighted)
    {
        try
        {
            var player = sapi.World.PlayerByUid(uid);
            if (player == null || weighted <= 0) return;

            double mult = Math.Min(WayfaringMultCap, weighted / WayfaringWeightPerRaw);
            if (mult <= 0) return;

            AlmanacTcmModSystem.ServerInstance?.Ledger?.Log(player, TemDomain.Code, TemDomain.TechWayfaring,
                HashCode.Combine("temwayfaring", uid, sapi.World.ElapsedMilliseconds), mult);
        }
        catch (Exception e)
        {
            // Conjunction's caller already shields its refresh tick; this shield keeps
            // the failure named on OUR side of the seam.
            TcmLog.Warn(sapi, $"wayfaring homecoming grant failed ({e.Message})");
        }
    }

    /// <summary>
    /// From Dispose, server side. Clears the provider ONLY if it is still ours — a
    /// stale delegate here would capture this world's dead ICoreServerAPI and keep
    /// answering radius questions in the next singleplayer world, which is the exact
    /// class of cross-world leak TCM's side-split statics exist to prevent. The
    /// ReferenceEquals guard means that if some future load order let another wiring
    /// land first, we do not blank it.
    /// </summary>
    public static void Unwire()
    {
        try
        {
            if (providerField != null && installed != null
                && ReferenceEquals(providerField.GetValue(null), installed))
            {
                providerField.SetValue(null, null);
            }
            if (rewardField != null && installedReward != null
                && ReferenceEquals(rewardField.GetValue(null), installedReward))
            {
                rewardField.SetValue(null, null);
            }
        }
        catch
        {
            // Disposal must never throw; a failed clear degrades to Conjunction's own
            // catch-and-fall-back posture.
        }
        providerField = null;
        installed = null;
        rewardField = null;
        installedReward = null;
    }
}
