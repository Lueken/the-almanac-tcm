using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// ANI Phase 1a hooks (rank-bonus-design §ANI, ruled 2026-07-10; technique-maps §ANI). This build
/// delivers the marquee verb — gen-raising — end to end, which is the payoff of the trio's shared
/// attribution spine: a player feeds a penned animal at the trough (FAR), which stamps `raisedBy`
/// on the beast; when that animal later gives birth (unattended, no IPlayer in scope), the stamp
/// names who bred the line, and ANI banks to them scaled by the newborn's generation (ruled Q3 —
/// climbing lineages is the skill). The vanilla birth and the genelib override are both hooked
/// (genelib overrides GiveBirth, so the base patch would miss its animals).
///
/// Deferred to Phase 1b: taming (#2). Both completion hooks — petai's feed-to-domesticate
/// transition and the vanilla saddle-break convert — need before/after transition detection to
/// bank only at the WILD/feral -> TAME crossing (partial progress banks nothing, ruled), which is
/// a state-capture prefix/postfix pair worth verifying in-game rather than guessing here.
///
/// Every seam warns-and-skips on a miss (the 0.3.85 isolation lesson).
/// </summary>
public static class AniPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Vanilla birth (fires for non-genelib animals, or all animals where genelib is absent).
        Hook(api, harmony, "Vintagestory.GameContent.EntityBehaviorMultiply", "GiveBirth", nameof(BirthPostfix), "ANI gen-raising (vanilla)");

        // Taming, saddle-break half — hooked at DoSaddleBreak, NOT ConvertToTamedAnimal, for two
        // verified 1.22.3 reasons: (a) DoSaddleBreak unmounts the rider BEFORE converting
        // (:102271 TryUnmount precedes :102283), so a convert-time hook can never see who broke
        // the animal (the 0.3.135 hole — that hook was dead on arrival); (b) the convert REPLACES
        // the entity, cloning WatchedAttributes (:102296+), so the raisedBy stamp must land before
        // the clone to survive onto the tamed animal. Prefix captures rider + count and stamps;
        // postfix credits when the count crossed to zero (the convert fired).
        HookPair(api, harmony, "Vintagestory.GameContent.EntityBehaviorRideable", "DoSaddleBreak",
            nameof(SaddleBreakPrefix), nameof(SaddleBreakPostfix), "ANI taming (saddle-break)");

        // Taming, feed half (petai wolves/foxes) — the DOMESTICATED crossing happens inside
        // OnInteract with the feeding player in scope (verified petai 5.1.1 :1540-1542:
        // DomesticationProgress >= 1 flips the level). Prefix captures the level; postfix banks
        // once on the crossing. Partial progress banks nothing (ruled).
        if (api.ModLoader.IsModEnabled("petai"))
            HookPair(api, harmony, "PetAI.EntityBehaviorTameable", "OnInteract",
                nameof(TamePrefix), nameof(TamePostfix), "ANI taming (petai feed)");

        // genelib overrides GiveBirth, so its animals never reach the base patch — hook the
        // override too. The namespace is best-effort; a miss just leaves genelib births uncredited
        // (warned) until the exact type name is confirmed in-game.
        if (api.ModLoader.IsModEnabled("genelib"))
        {
            foreach (string tn in new[] { "Genelib.GeneticMultiply", "Genelib.Entities.GeneticMultiply", "GeneticMultiply" })
            {
                var t = AccessTools.TypeByName(tn);
                var m = t == null ? null : AccessTools.Method(t, "GiveBirth");
                if (m != null)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(AniPatches), nameof(BirthPostfix))));
                    TcmLog.Info(api, $"ANI gen-raising (genelib) hooked ({tn}.GiveBirth)");
                    return;
                }
            }
            TcmLog.Warn(api, "genelib present but GeneticMultiply.GiveBirth not found; genelib births uncredited this build");
        }
    }

    private static void Hook(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.Method(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive this build"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(AniPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    private static void HookPair(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.Method(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(AniPatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AniPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    /// <summary>EntityBehavior.entity is protected; read it by reflection (the HunPatches pattern).</summary>
    private static Entity? BehaviorEntity(EntityBehavior beh) =>
        AccessTools.Field(typeof(EntityBehavior), "entity")?.GetValue(beh) as Entity;

    /// <summary>The mounted player on a rideable behaviour (EntityBehaviorRideable :
    /// EntityBehaviorSeatable). Reads Seats -> each seat's Passenger by reflection, returning the
    /// first player rider — who tamed the animal by breaking it.</summary>
    private static IPlayer? RiderOf(EntityBehavior beh)
    {
        if (Traverse.Create(beh).Property("Seats").GetValue() is not System.Collections.IEnumerable seats) return null;
        foreach (object? seat in seats)
        {
            if (seat == null) continue;
            if (Traverse.Create(seat).Property("Passenger").GetValue() is EntityPlayer ep) return ep.Player;
        }
        return null;
    }

    // ------------------------------------------------------------ taming (saddle-break)

    public readonly record struct BreakState(string? RiderUid, int BreaksBefore);

    private static int RemainingBreaks(EntityBehavior beh)
    {
        var tv = Traverse.Create(beh);
        if (tv.Property("RemainingSaddleBreaks").PropertyExists()) return tv.Property("RemainingSaddleBreaks").GetValue<int>();
        if (tv.Field("RemainingSaddleBreaks").FieldExists()) return tv.Field("RemainingSaddleBreaks").GetValue<int>();
        return int.MaxValue;
    }

    /// <summary>Capture the rider while they are STILL MOUNTED (DoSaddleBreak unmounts them
    /// mid-method), and stamp them as raiser now — every break attempt restamps, and on the final
    /// one the convert's WatchedAttributes clone carries the stamp onto the tamed entity.</summary>
    public static void SaddleBreakPrefix(EntityBehavior __instance, out BreakState __state)
    {
        IPlayer? rider = RiderOf(__instance);
        __state = new BreakState(rider?.PlayerUID, RemainingBreaks(__instance));
        Entity? animal = BehaviorEntity(__instance);
        if (rider != null && animal?.World?.Side == EnumAppSide.Server)
            animal.WatchedAttributes?.SetString(AniDomain.RaisedByAttr, rider.PlayerUID);
    }

    /// <summary>Bank taming only when THIS break took the count to zero (the convert fired) —
    /// the WILD/feral -> TAME crossing. A mid-course throw banks nothing (partial progress rule).</summary>
    public static void SaddleBreakPostfix(EntityBehavior __instance, BreakState __state)
    {
        Entity? animal = __instance == null ? null : BehaviorEntity(__instance);
        if (animal?.World?.Side != EnumAppSide.Server || __state.RiderUid == null) return;
        if (__state.BreaksBefore <= 0 || RemainingBreaks(__instance!) > 0) return; // not the final break
        IPlayer? rider = animal.World.PlayerByUid(__state.RiderUid);
        if (rider == null) return;
        Core?.Ledger?.Log(rider, AniDomain.Code, AniDomain.TechTaming,
            HashCode.Combine("break", animal.EntityId, animal.World.ElapsedMilliseconds / 1000));
        TcmLog.Cat(animal.World.Api, "ani", $"saddle-break tamed: {animal.Code?.FirstCodePart()} #{animal.EntityId} -> {rider.PlayerName}");
    }

    // ------------------------------------------------------------ taming (petai feed)

    private static bool IsDomesticated(EntityBehavior beh) =>
        Traverse.Create(beh).Property("DomesticationLevel").GetValue()?.ToString() == "DOMESTICATED";

    public static void TamePrefix(EntityBehavior __instance, out bool __state)
    {
        __state = IsDomesticated(__instance);
    }

    /// <summary>The feed-path completion: this interact pushed DomesticationProgress past 1 and
    /// the level flipped to DOMESTICATED. The feeder is in scope — credit them, and stamp raiser
    /// (petai's own owner is stored separately; the almanac stamp keeps the birth read uniform).</summary>
    public static void TamePostfix(EntityBehavior __instance, EntityAgent byEntity, bool __state)
    {
        if (__state || !IsDomesticated(__instance)) return; // no crossing this interact
        Entity? animal = BehaviorEntity(__instance);
        IPlayer? feeder = (byEntity as EntityPlayer)?.Player;
        if (animal?.World?.Side != EnumAppSide.Server || feeder == null) return;
        animal.WatchedAttributes?.SetString(AniDomain.RaisedByAttr, feeder.PlayerUID);
        Core?.Ledger?.Log(feeder, AniDomain.Code, AniDomain.TechTaming,
            HashCode.Combine("feed", animal.EntityId, animal.World.ElapsedMilliseconds / 1000));
        TcmLog.Cat(animal.World.Api, "ani", $"feed tamed: {animal.Code?.FirstCodePart()} #{animal.EntityId} -> {feeder.PlayerName}");
    }

    // ------------------------------------------------------------ gen-raising (the birth)

    /// <summary>One birth event: read the dam's `raisedBy` stamp (written by FAR's trough feed or
    /// the taming hooks), falling back to petai's own owner (WatchedAttributes
    /// domesticationstatus/owner, verified petai 5.1.1 :1291 — the ruled attribution chain).
    /// Credit that breeder ANI gen-raising scaled by the newborn's generation. A truly feral pair
    /// banks nothing. The stamp lives on the DAM; a newborn earns its own stamp when it is later
    /// fed or tamed (the land-claim fallback link is still pending). One credit per GiveBirth
    /// call, contextHash-deduped on the dam so a litter banks once.</summary>
    public static void BirthPostfix(EntityBehavior __instance)
    {
        Entity? dam = __instance == null ? null : BehaviorEntity(__instance);
        if (dam?.World?.Side != EnumAppSide.Server) return;

        string? uid = dam.WatchedAttributes?.GetString(AniDomain.RaisedByAttr);
        if (string.IsNullOrEmpty(uid))
            uid = dam.WatchedAttributes?.GetTreeAttribute("domesticationstatus")?.GetString("owner");
        if (string.IsNullOrEmpty(uid)) return; // no husbandry stamp — feral, nobody's practice yet
        IPlayer? owner = dam.World.PlayerByUid(uid);
        if (owner == null) return; // breeder offline; their birth waits for them

        int newbornGen = dam.WatchedAttributes!.GetInt("generation", 0) + 1;
        double mult = AniDomain.GenRaiseMult(newbornGen);
        Core?.Ledger?.Log(owner, AniDomain.Code, AniDomain.TechGenRaising,
            HashCode.Combine("birth", dam.EntityId, dam.World.ElapsedMilliseconds / 1000), mult);
    }
}
