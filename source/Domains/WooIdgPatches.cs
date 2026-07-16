using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO Phase 2 — the IndappledGroves processing verbs (technique-maps §WOO #2–#5): chopping,
/// sawing, hewing, pounding. RULED DISTINCT 2026-07-08 ("definitely" — different tools, outputs
/// and skill expressions), with planing merged into hewing (ruling 2).
///
/// Razor 1 (ruled): field-processing and workstation-processing are ONE verb with two paths. IDG
/// honours that in its own data — both paths carry the same `ToolMode` string — so this is four
/// verbs behind two seams, not eight:
///   • ground / in-world log  → BehaviorIDGTool.SpawnOutput(GroundRecipe, BlockPos, EntityAgent)
///   • built workstation      → RecipeHandler.SpawnOutput(EntityAgent, BlockPos)
/// Both fire once, server-side, exactly at completion. A third IDG path exists
/// (ALCMYCollectibleBehaviorGroundStoredProcessable.OnContainedInteractStop) but it drives the
/// generic ALCMY process system, carries no ToolMode, and is used by exactly one live asset
/// (bark.json) — deliberately out of scope, not an oversight.
///
/// IDG-conditional; an install without it loads clean and the verbs are simply inert.
/// </summary>
public static class WooIdgPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    /// <summary>IDG tool mode → WOO technique. Verified against the live asset tree: only
    /// chopping (37 recipes), sawing (17), hewing (15) and pounding (7) appear. **Planing ships
    /// zero recipes**, which is why folding it into hewing costs nothing — mapped anyway so a
    /// future IDG that adds planing recipes credits the shaping verb rather than silently dropping.</summary>
    private static readonly Dictionary<string, string> ModeToTechnique = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chopping"] = WooDomain.TechChopping,
        ["sawing"] = WooDomain.TechSawing,
        ["hewing"] = WooDomain.TechHewing,
        ["planing"] = WooDomain.TechHewing,
        ["pounding"] = WooDomain.TechPounding,
    };

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("indappledgroves")) return;

        var recipeHandler = AccessTools.TypeByName("InDappledGroves.Util.Handlers.RecipeHandler");
        var idgTool = AccessTools.TypeByName("InDappledGroves.CollectibleBehaviors.BehaviorIDGTool");
        var groundRecipe = AccessTools.TypeByName("InDappledGroves.Util.RecipeTools.IDGRecipeNames+GroundRecipe");

        // Overload-exact: RecipeHandler has one SpawnOutput(EntityAgent, BlockPos); BehaviorIDGTool's
        // takes the recipe first. Resolve by signature so a future overload can't silently rebind us.
        var wsSpawn = recipeHandler == null ? null : AccessTools.Method(recipeHandler, "SpawnOutput",
            new[] { typeof(EntityAgent), typeof(BlockPos) });
        var groundSpawn = (idgTool == null || groundRecipe == null) ? null
            : AccessTools.Method(idgTool, "SpawnOutput", new[] { groundRecipe, typeof(BlockPos), typeof(EntityAgent) });
        // Fallback: the nested-type name ("...IDGRecipeNames+GroundRecipe") is the one brittle
        // string here. If it ever fails to resolve, take the only 3-arg SpawnOutput by shape
        // instead of losing the whole ground path to a naming detail.
        if (groundSpawn == null && idgTool != null)
        {
            foreach (var m in AccessTools.GetDeclaredMethods(idgTool))
            {
                if (m.Name != "SpawnOutput") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[1].ParameterType == typeof(BlockPos)
                    && ps[2].ParameterType == typeof(EntityAgent)) { groundSpawn = m; break; }
            }
            if (groundSpawn != null) TcmLog.Warn(api, "IDG GroundRecipe type name did not resolve; matched SpawnOutput by shape");
        }

        if (wsSpawn == null && groundSpawn == null)
        {
            TcmLog.Warn(api, "indappledgroves present but neither SpawnOutput seam was found; WOO processing verbs inactive");
            return;
        }

        if (wsSpawn != null)
        {
            // PREFIX, not postfix: SpawnOutput calls clearRecipe() on its way out, so by the time a
            // postfix ran the ToolMode would be gone.
            harmony.Patch(wsSpawn, prefix: new HarmonyMethod(AccessTools.Method(typeof(WorkstationPatch), "Prefix")));
        }
        else
        {
            TcmLog.Warn(api, "IDG RecipeHandler.SpawnOutput not found; WOO workstation processing inactive (ground path still live)");
        }

        if (groundSpawn != null)
        {
            harmony.Patch(groundSpawn, prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundPatch), "Prefix")));
        }
        else
        {
            TcmLog.Warn(api, "IDG BehaviorIDGTool.SpawnOutput not found; WOO ground processing inactive (workstation path still live)");
        }

        TcmLog.Info(api, "WOO processing verbs hooked to IndappledGroves (chopping/sawing/hewing/pounding)");
    }

    /// <summary>Built-workstation completion (sawbuck, chopping block, log splitter).</summary>
    public static class WorkstationPatch
    {
        public static void Prefix(object __instance, EntityAgent byEntity)
        {
            string? mode = null;
            try { mode = Traverse.Create(__instance).Property("recipe").Property("ToolMode").GetValue<string>(); }
            catch { return; }
            Credit(byEntity, mode);
        }
    }

    /// <summary>Ground / in-world log completion. The recipe arrives as an argument here, so no
    /// instance state to read.</summary>
    public static class GroundPatch
    {
        public static void Prefix(object __0, EntityAgent __2)
        {
            string? mode = null;
            try { mode = Traverse.Create(__0).Field("ToolMode").GetValue<string>(); }
            catch { return; }
            Credit(__2, mode);
        }
    }

    /// <summary>The one credit path both seams share.</summary>
    private static void Credit(EntityAgent? byEntity, string? mode)
    {
        if (byEntity?.World?.Side != EnumAppSide.Server || string.IsNullOrEmpty(mode)) return;
        IPlayer? player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;
        if (!ModeToTechnique.TryGetValue(mode, out string? technique)) return;

        // contextHash = mode + a 1s bucket (the MET assembly precedent), deliberately NOT position.
        // Processing is legitimately repeated at ONE fixed workstation, so a position key would
        // dedup the entire verb away after the first log. There is no exploit to guard: every
        // completion consumes a real log, and logs cost a felled tree. K is the grind ceiling.
        // The bucket only swallows a genuine double-fire of the same action.
        Core?.Ledger?.Log(player, WooDomain.Code, technique,
            HashCode.Combine(technique, byEntity.World.ElapsedMilliseconds / 1000));
    }
}
