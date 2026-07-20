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

    /// <summary>EntityBehavior.entity is protected; read it by reflection (the HunPatches pattern).</summary>
    private static Entity? BehaviorEntity(EntityBehavior beh) =>
        AccessTools.Field(typeof(EntityBehavior), "entity")?.GetValue(beh) as Entity;

    // ------------------------------------------------------------ gen-raising (the birth)

    /// <summary>One birth event: read the dam's `raisedBy` stamp (written by FAR's trough feed),
    /// credit that breeder ANI gen-raising scaled by the newborn's generation. A feral pair with
    /// no stamp banks nothing (the claim-owner / petai-owner fallback links are Phase 1b). One
    /// credit per GiveBirth call, contextHash-deduped on the dam so a litter banks once.</summary>
    public static void BirthPostfix(EntityBehavior __instance)
    {
        Entity? dam = __instance == null ? null : BehaviorEntity(__instance);
        if (dam?.World?.Side != EnumAppSide.Server) return;

        string? uid = dam.WatchedAttributes?.GetString(AniDomain.RaisedByAttr);
        if (string.IsNullOrEmpty(uid)) return; // no husbandry stamp — feral, nobody's practice yet
        IPlayer? owner = dam.World.PlayerByUid(uid);
        if (owner == null) return; // breeder offline; their birth waits for them

        int newbornGen = dam.WatchedAttributes!.GetInt("generation", 0) + 1;
        double mult = AniDomain.GenRaiseMult(newbornGen);
        Core?.Ledger?.Log(owner, AniDomain.Code, AniDomain.TechGenRaising,
            HashCode.Combine("birth", dam.EntityId, dam.World.ElapsedMilliseconds / 1000), mult);
    }
}
