using System;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// ANI Phase 2, the genetics half (rank-bonus-design §ANI RULED 2026-07-10; seams verified
/// against the LIVE genelib 3.1.2 decompile 2026-07-21).
///
/// • Bloodline hygiene (the headline, ani-line study rec 1): a ranked breeder's newborns purge
///   deleterious alleles harder. genelib's FinalizeSpawn reads GenelibConfig.Instance
///   .InbreedingResistance LIVE per copy (:2885), so the lever is a prefix/postfix pair around
///   the newborn's genetics finalize (EntityBehaviorGenetics.AfterInitialized :1079) that
///   raises and restores the config value for exactly that one spawn. The breeder context is
///   set by the GiveBirth prefix (the finalize runs synchronously inside the birth's spawn
///   call stack); worldgen and wild spawns see level 0 and stay pure vanilla. Hard-capped
///   below 1: even a GM's line sheds load imperfectly (principle 3).
/// • Litter depth (rec 3): ChooseLitterSize (:606) postfix — a rank-weighted chance of one
///   extra offspring, capped at the species' own SpawnQuantityMax.
/// Both dependsOn genelib: without it, vanilla breeding cannot fail and these levers are inert.
/// </summary>
public static class AniBonusPatches
{
    /// <summary>The ANI level of the breeder whose birth is currently executing (0 = none).
    /// Set by the GiveBirth prefix, cleared by its postfix; births run on the server main
    /// thread, so a single static context is safe.</summary>
    public static int BreederLevel;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("genelib")) return;

        var tg = AccessTools.TypeByName("Genelib.EntityBehaviorGenetics");
        var mg = tg == null ? null : AccessTools.DeclaredMethod(tg, "AfterInitialized");
        if (mg == null) TcmLog.Warn(api, "ANI bloodline seam not found (EntityBehaviorGenetics.AfterInitialized); hygiene lever inactive");
        else
        {
            harmony.Patch(mg,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(AniBonusPatches), nameof(GeneticsPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(AniBonusPatches), nameof(GeneticsPostfix))));
            TcmLog.Info(api, "ANI bloodline hygiene hooked (AfterInitialized: per-birth InbreedingResistance bump)");
        }

        var tm = AccessTools.TypeByName("Genelib.GeneticMultiply");
        var mm = tm == null ? null : AccessTools.DeclaredMethod(tm, "ChooseLitterSize");
        if (mm == null) TcmLog.Warn(api, "ANI litter seam not found (GeneticMultiply.ChooseLitterSize); litter lever inactive");
        else
        {
            harmony.Patch(mm, postfix: new HarmonyMethod(AccessTools.Method(typeof(AniBonusPatches), nameof(LitterPostfix))));
            TcmLog.Info(api, "ANI litter depth hooked (ChooseLitterSize)");
        }
    }

    // ------------------------------------------------------------ the Master's Line (provenance)

    /// <summary>Append the tiered Master's Line mark to an animal's hover info (Entity.GetInfoText
    /// aggregates behaviour info, vsapi :156477). Reads the synced provenance stamp, so this runs
    /// client-side off WatchedAttributes. Journeyman -> Raised by, Master -> Bred by, GM ->
    /// Master's Line of. Applied by the Start PatchAll pass (attribute patch, both sides).</summary>
    [HarmonyPatch(typeof(Entity), nameof(Entity.GetInfoText))]
    public static class ProvenancePatch
    {
        public static void Postfix(Entity __instance, ref string __result)
        {
            var wa = __instance?.WatchedAttributes;
            string? name = wa?.GetString(AniDomain.ProvNameAttr);
            if (string.IsNullOrEmpty(name)) return;
            int tier = wa!.GetInt(AniDomain.ProvTierAttr, 0);
            string? line =
                tier >= Rank.Grandmaster ? Lang.Get("almanactcm:mastersline-of", name)
                : tier >= Rank.Master ? Lang.Get("almanactcm:bred-by", name)
                : tier >= Rank.Journeyman ? Lang.Get("almanactcm:raised-by", name)
                : null;
            if (line != null) __result = (__result ?? "").TrimEnd() + "\n" + line;
        }
    }

    // ------------------------------------------------------------ bloodline hygiene

    public static void GeneticsPrefix(out float __state)
    {
        var cfgType = AccessTools.TypeByName("Genelib.GenelibConfig");
        object? inst = cfgType == null ? null : AccessTools.Field(cfgType, "Instance")?.GetValue(null);
        var field = inst == null ? null : AccessTools.Field(inst.GetType(), "InbreedingResistance");
        float baseVal = field == null ? 0.6f : (float)field.GetValue(inst)!;
        __state = baseVal;
        if (BreederLevel <= 0 || field == null || inst == null) return;

        double bump = AniDomain.BonusT(BreederLevel) * AniDomain.Knob(AniDomain.PurgeBonusGm, 0.30);
        if (bump <= 0) return;
        float raised = (float)Math.Min(0.95, baseVal + bump); // never a total purge (principle 3)
        field.SetValue(inst, raised);
    }

    public static void GeneticsPostfix(float __state)
    {
        var cfgType = AccessTools.TypeByName("Genelib.GenelibConfig");
        object? inst = cfgType == null ? null : AccessTools.Field(cfgType, "Instance")?.GetValue(null);
        var field = inst == null ? null : AccessTools.Field(inst.GetType(), "InbreedingResistance");
        field?.SetValue(inst, __state);
    }

    // ------------------------------------------------------------ litter depth

    /// <summary>One extra offspring on a rank-weighted roll, inside genelib's own range. The dam
    /// is in scope (conception time), so attribution reads her raisedBy stamp directly.</summary>
    public static void LitterPostfix(EntityBehavior __instance, ref int __result)
    {
        var dam = AccessTools.Field(typeof(EntityBehavior), "entity")?.GetValue(__instance) as Entity;
        if (dam?.World?.Side != EnumAppSide.Server) return;

        string? uid = dam.WatchedAttributes?.GetString(AniDomain.RaisedByAttr);
        if (string.IsNullOrEmpty(uid))
            uid = dam.WatchedAttributes?.GetTreeAttribute("domesticationstatus")?.GetString("owner");
        if (string.IsNullOrEmpty(uid)) return;
        IPlayer? breeder = dam.World.PlayerByUid(uid);
        if (breeder == null) return;

        double chance = AniDomain.BonusT(AniDomain.LevelOf(breeder)) * AniDomain.Knob(AniDomain.LitterProcGm, 0.35);
        if (chance <= 0 || dam.World.Rand.NextDouble() >= chance) return;

        float max = Traverse.Create(__instance).Property("SpawnQuantityMax").PropertyExists()
            ? Traverse.Create(__instance).Property("SpawnQuantityMax").GetValue<float>()
            : Traverse.Create(__instance).Field("SpawnQuantityMax").FieldExists()
                ? Traverse.Create(__instance).Field("SpawnQuantityMax").GetValue<float>() : 0f;
        if (max > 0 && __result < (int)max)
        {
            __result += 1;
            TcmLog.Cat(dam.World.Api, "ani", $"litter depth proc: {dam.Code?.FirstCodePart()} dam #{dam.EntityId} litter +1 -> {__result} (breeder {breeder.PlayerName})");
        }
    }
}
