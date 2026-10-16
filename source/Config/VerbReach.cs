using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Config;

/// <summary>
/// Which verbs a pack can actually REACH, per domain (LGD-292). The counterpart to `M`: a domain's
/// daily value splits across M verbs, so M above the number of reachable verbs leaves part of every
/// day unearnable by any route. PAN carried that defect until 0.5.15, ENG until 2026-08-22, and FIS
/// until a player reported it on 2026-10-09. Three times, and nothing ever logged it.
///
/// WHAT A VERB'S GATE IS. A grant seam attaches at boot or it does not. Vanilla seams always
/// attach; a mod seam attaches only when its mod (or its type) is there. This table records, per
/// technique, the condition the patcher itself tests, transcribed from the patcher with the file and
/// line it came from. A verb is reachable when ANY ONE of its alternatives is satisfied, because
/// several verbs have a vanilla floor beside a mod seam (MEL blocking) or ride either of two mods
/// (WOO sawing).
///
/// WHY TRANSCRIPTION AND NOT INFERENCE. The audit must never cry wolf, so the gate here is the same
/// expression the patcher evaluates, not a guess about which mod supplies a verb. Guessing is how a
/// diagnostic ends up louder than the bug: MEL blocking LOOKS like a Combat Overhaul verb and is not
/// (MelPatches.cs:138-147 patches vanilla shield absorbs when CO is absent), and MET assembly looks
/// like a Toolsmith verb and is not (MetPatches.cs:1063 is a vanilla grid-craft).
///
/// CONSERVATIVE BY CONSTRUCTION. A technique with no entry here counts as REACHABLE and is reported
/// as unaudited. A newly shipped verb therefore cannot invent a shortfall that is not real; it shows
/// up as a gap in this table instead, which is the thing a person can fix.
///
/// NOT A TUNING SURFACE AND NEVER SERIALISED. This is a code-side fact about seams, not an operator
/// knob. It must not reach any config file: an operator editing it would be editing the audit's
/// eyesight.
/// </summary>
public static class VerbReach
{
    public enum GateKind
    {
        /// <summary>A vanilla seam. Always attaches, so the verb is always reachable.</summary>
        Vanilla,
        /// <summary>Reachable when this modid is enabled (the patcher's own IsModEnabled test).</summary>
        Mod,
        /// <summary>Reachable when this type resolves (the patcher's own AccessTools.TypeByName test).</summary>
        Type,
    }

    /// <summary>One alternative route to a verb. <paramref name="Value"/> is a modid, a
    /// fully-qualified type name, or the vanilla seam it rides (documentation only).</summary>
    public readonly record struct Gate(GateKind Kind, string Value);

    private static Gate V(string vanillaSeam) => new(GateKind.Vanilla, vanillaSeam);
    private static Gate M(string modid) => new(GateKind.Mod, modid);
    private static Gate T(string typeName) => new(GateKind.Type, typeName);

    /// <summary>Domain code → technique → the alternative routes to that verb. Any one satisfied
    /// means reachable. Citations are to the patcher the gate was read from.</summary>
    private static readonly Dictionary<string, Dictionary<string, Gate[]>> Table = new()
    {
        // MIN. MinPatches.cs:100,125 (vanilla Block/BlockOre.OnBlockBroken), :89 (knapping, the
        // zero-Harmony server hook). Quarrying is StoneQuarry only, either modid
        // (MinConditionalPatches.cs:126, the Repacked fork first).
        ["MIN"] = new()
        {
            ["mining"] = new[] { V("Block.OnBlockBroken") },
            ["knapping"] = new[] { V("knapping server hook") },
            ["quarrying"] = new[] { M("stonequarryrepckfipil"), M("stonequarry") },
        },

        // WOO. WooPatches.cs:68,92 felling, :173 planting (vanilla ItemTreeSeed),
        // WooColliderPatches burning (vanilla charcoal pit). The station verbs come from either
        // woodworking mod: WooIdgPatches.cs:46 (indappledgroves, all four) and WooIwPatches.cs:36
        // (immersivewoodworking, three of them; pounding is IDG only).
        ["WOO"] = new()
        {
            ["felling"] = new[] { V("ItemAxe.OnBlockBrokenWith") },
            ["planting"] = new[] { V("ItemTreeSeed.OnHeldInteractStart") },
            ["burning"] = new[] { V("charcoal pit conversion") },
            ["chopping"] = new[] { M("indappledgroves"), M("immersivewoodworking") },
            ["sawing"] = new[] { M("indappledgroves"), M("immersivewoodworking") },
            ["hewing"] = new[] { M("indappledgroves"), M("immersivewoodworking") },
            ["pounding"] = new[] { M("indappledgroves") },
        },

        // FAR. FarPatches.cs:116-181, all vanilla seams (hoe, farmland, crop, soil nutrition,
        // milkable, hen box, fruit-tree part, branch graft, bush cutting, trough). Milling is the
        // vanilla quern (CooPatches.cs:99). Butchery is the vanilla carcass harvest split with HUN
        // (HunPatches.cs:304,327). Beekeeping is the vanilla skep (FarPatches.cs:167), and stands
        // down to BEE when a beekeeping mod is present rather than becoming unreachable.
        // Conditionals: shearing (shearlib, :189), furrow (primitivesurvival, :197),
        // vermiculture (ithaniaexpandedfishing, :244).
        ["FAR"] = new()
        {
            ["tilling"] = new[] { V("ItemHoe.DoTill") },
            ["planting"] = new[] { V("BlockEntityFarmland.TryPlant") },
            ["harvesting"] = new[] { V("BlockCrop.OnBlockBroken") },
            ["fertilizing"] = new[] { V("BlockEntitySoilNutrition.OnBlockInteract") },
            ["milking"] = new[] { V("EntityBehaviorMilkable.MilkingComplete") },
            ["eggs"] = new[] { V("BlockEntityHenBox.OnInteract") },
            ["grafting"] = new[] { V("BlockFruitTreeBranch.TryPlaceBlock") },
            ["cuttings"] = new[] { V("BEBehaviorFruitingBush.OnGrownFromCutting") },
            ["orchard"] = new[] { V("BlockEntityFruitTreePart.OnBlockInteractStop") },
            ["beekeeping"] = new[] { V("BlockSkep.OnBlockBroken") },
            ["butchery"] = new[] { V("EntityBehaviorHarvestable.GenerateDrops") },
            ["milling"] = new[] { V("BlockEntityQuern.grindInput") },
            ["shearing"] = new[] { M("shearlib") },
            ["furrow"] = new[] { M("primitivesurvival") },
            ["vermiculture"] = new[] { M("ithaniaexpandedfishing") },
        },

        // FIS. FisPatches.cs:54 angling (vanilla EntityBobber.TryCatchFish) is the only verb a
        // vanilla pack reaches. FisPsPatches.cs:28 spearing and FisEcologyPatches.cs:94 processing
        // are primitivesurvival; trapping rides either trap mod (FisTrapPatches.cs:136-137).
        // FisTrapPatches.ClampFisM already narrows M to this same count at boot.
        ["FIS"] = new()
        {
            ["angling"] = new[] { V("EntityBobber.TryCatchFish") },
            ["spearing"] = new[] { M("primitivesurvival") },
            ["processing"] = new[] { M("primitivesurvival") },
            ["trapping"] = new[] { M("primitivesurvival"), M("ithaniaexpandedfishing") },
        },

        // COO. CooPatches.cs:70-107, six vanilla sinks. The station verbs split by mod:
        // seafarer (:110) prep, griddling, salting; aculinaryartillery (:126) mixing, simmering.
        // Drying has two racks, one per mod (:119 seafarer frame, :142 ACA meat hooks).
        ["COO"] = new()
        {
            ["mealpot"] = new[] { V("BlockCookingContainer.DoSmelt") },
            ["directheat"] = new[] { V("CollectibleObject.DoSmelt") },
            ["milling"] = new[] { V("BlockEntityQuern.grindInput") },
            ["baking"] = new[] { V("BlockEntityOven.OnInteract") },
            ["juicing"] = new[] { V("BlockEntityFruitPress.OnBlockInteractStop") },
            ["pastry"] = new[] { V("BlockEntityPie.OnInteract") },
            ["prep"] = new[] { M("seafarer") },
            ["griddling"] = new[] { M("seafarer") },
            ["salting"] = new[] { M("seafarer") },
            ["mixing"] = new[] { M("aculinaryartillery") },
            ["simmering"] = new[] { M("aculinaryartillery") },
            ["drying"] = new[] { M("seafarer"), M("aculinaryartillery") },
        },

        // MET. MetPatches.cs:441,572 smithing, :957 casting, :837 quenching, :800 smelting,
        // :1017 alloying, :1063 assembly. All six are vanilla; Toolsmith and Smithing Plus add
        // further routes to the same verbs (MetConditionalPatches.cs:395,438) rather than owning any.
        ["MET"] = new()
        {
            ["smithing"] = new[] { V("BlockEntityAnvil.OnUseOver") },
            ["casting"] = new[] { V("BlockEntityToolMold.ReceiveLiquidMetal") },
            ["quenching"] = new[] { V("CollectibleBehaviorQuenchable") },
            ["smelting"] = new[] { V("BlockSmeltingContainer.DoSmelt") },
            ["alloying"] = new[] { V("CollectibleObject.OnCreatedByCrafting") },
            ["assembly"] = new[] { V("GridRecipe.ConsumeInput") },
        },

        // POT. PotPatches.PatchConditional, both verbs on vanilla types (BlockEntityClayForm,
        // BlockEntityPitKiln). SimplePotteryWheel adds a second route to clayforming.
        ["POT"] = new()
        {
            ["clayforming"] = new[] { V("BlockEntityClayForm.CheckIfFinished") },
            ["firing"] = new[] { V("BlockEntityPitKiln.OnFired") },
        },

        // GLA. the domain is dormant without glassmakingfork (roster RequiredMod), and every verb
        // is a GlassMaking type (GlaPatches.PatchConditional). Typed rather than modid-gated so the
        // audit tests exactly what the patcher tested.
        ["GLA"] = new()
        {
            ["melting"] = new[] { T("GlassMaking.Blocks.BlockEntityGlassSmeltery"), T("GlassMaking.Blocks.BlockEntityLargeSmelteryHearth") },
            ["blowing"] = new[] { T("GlassMaking.Items.Behavior.GlasspipeRecipeBehavior"), T("GlassMaking.Blocks.BlockEntityGlassBlowingMold") },
            ["casting"] = new[] { T("GlassMaking.Blocks.BlockEntityGlassCastingMold") },
            ["workbench"] = new[] { T("GlassMaking.Blocks.BlockEntityWorkbench") },
            ["annealing"] = new[] { T("GlassMaking.Blocks.BlockEntityAnnealer") },
        },

        // PAN. PanPatches.cs:114 panning (vanilla BlockPan), :136 prospecting (vanilla
        // ModSystemOreMap.DidProbe). Both vanilla; bettererprospecting and wstonebound add routes.
        ["PAN"] = new()
        {
            ["panning"] = new[] { V("BlockPan.OnHeldInteractStop") },
            ["prospecting"] = new[] { V("ModSystemOreMap.DidProbe") },
        },

        // HUN. HunPatches.cs:263 hunting (the vanilla kill seam), :304 dressing (vanilla
        // EntityBehaviorHarvestable), tanning at the vanilla barrel seal (BrePatches.cs:219).
        // Conditionals: trapping (primitivesurvival, :410), butchery (butchering, :348).
        ["HUN"] = new()
        {
            ["hunting"] = new[] { V("Entity.ReceiveDamage kill attribution") },
            ["dressing"] = new[] { V("EntityBehaviorHarvestable.GenerateDrops") },
            ["tanning"] = new[] { V("BlockEntityBarrel seal") },
            ["trapping"] = new[] { M("primitivesurvival") },
            ["butchery"] = new[] { M("butchering") },
        },

        // ANI. AniPatches.cs:37 gen-raising and :51 taming are both vanilla behaviours
        // (EntityBehaviorMultiply.GiveBirth, EntityBehaviorRideable.DoSaddleBreak); petai and
        // genelib add routes to the same two. Feeding is the vanilla trough (FarPatches.cs:178),
        // riding the vanilla rideable behaviour on a tick (AniRiding.cs:47,64).
        ["ANI"] = new()
        {
            ["genraising"] = new[] { V("EntityBehaviorMultiply.GiveBirth") },
            ["taming"] = new[] { V("EntityBehaviorRideable.DoSaddleBreak") },
            ["feeding"] = new[] { V("BlockEntityTrough.ConsumeOnePortion") },
            ["riding"] = new[] { V("EntityBehaviorRideable, ridden-distance tick") },
        },

        // FOR. ForPatches.cs:250 harvesting (vanilla BlockBehaviorHarvestable), :380 gathering
        // (vanilla Block.OnBlockBroken). Both sap verbs are the ACA spile and nothing else
        // (ForAcaPatches.cs:59).
        ["FOR"] = new()
        {
            ["harvesting"] = new[] { V("BlockBehaviorHarvestable.OnBlockInteractStop") },
            ["gathering"] = new[] { V("Block.OnBlockBroken") },
            ["tapping"] = new[] { M("aculinaryartillery") },
            ["sapcollecting"] = new[] { M("aculinaryartillery") },
        },

        // ALC. remedy is the vanilla healing-item grid craft (AlcPatches.cs:435). Reagent work
        // rides vanilla processing of alchemical matter: the quern (CooPatches.cs:837), the pot
        // (:655) and the barrel (BrePatches.cs:240). Chemistry is the IndustrialStory reaction
        // vessel and nothing else (AlcPatches.cs:134).
        //
        // Reagent work is seam-reachable on any pack but has NOTHING TO PROCESS without a mod
        // supplying alchemical matter. This table measures seams, so it counts that verb reachable;
        // the audit therefore understates ALC's shortfall rather than overstating it.
        ["ALC"] = new()
        {
            ["remedy"] = new[] { V("GridRecipe.ConsumeInput, healing output") },
            ["reagentwork"] = new[] { V("quern / pot / barrel, alchemical matter") },
            ["chemistry"] = new[] { T("IndustrialStory.ApparatusReactionVesselEntity"), T("Industrialstory.ApparatusReactionVesselEntity") },
        },

        // BRE. BrePatches.cs:77 fermenting (vanilla BlockEntityBarrel), :92 distilling (vanilla
        // BlockEntityBoiler). Fermentaria adds a second fermenting vessel (:136).
        ["BRE"] = new()
        {
            ["fermenting"] = new[] { V("BlockEntityBarrel seal") },
            ["distilling"] = new[] { V("BlockEntityBoiler.OnInteract") },
        },

        // TAI. sew is the vanilla clothing-repair grid craft and dye the vanilla barrel seal
        // (BrePatches routes it, TaiDomain.cs:45). The three fibre verbs are mod stations:
        // spinningwheel for spin and weave, the knitting mod for knit (TaiPatches.PatchConditional).
        ["TAI"] = new()
        {
            ["sew"] = new[] { V("GridRecipe.ConsumeInput, clothing repair") },
            ["dye"] = new[] { V("BlockEntityBarrel seal, dye recipe") },
            ["spin"] = new[] { T("SpinningWheel.Items.ItemDropSpindle"), T("SpinningWheel.BlockEntities.BlockEntitySpinningWheel") },
            ["weave"] = new[] { T("SpinningWheel.BlockEntities.BlockEntityFlyShuttleLoom") },
            ["knit"] = new[] { T("Knitting.Items.ItemKnittingNeedles") },
        },

        // MAS. MasPatches.cs:149 chisel (vanilla BlockEntityChisel.SetVoxel) is the only verb a
        // vanilla pack reaches. Mortar is MedievalArchitecture (:52) and dressing is StoneQuarry
        // (:67,:76), both probed by type.
        ["MAS"] = new()
        {
            ["chisel"] = new[] { V("BlockEntityChisel.SetVoxel") },
            ["mortar"] = new[] { T("MedievalArchitecture.BlockBehaviorArchway"), T("MedievalArchitecture.BlockBehaviorArchwayFrame") },
            ["dress"] = new[] { T("StoneQuarry.StoneSlabInventory"), T("StoneQuarry.ItemRubbleHammer") },
        },

        // ENG. assembly is the vanilla windmill rotor (EngPatches.cs:139), with the Millwright
        // pool as a second route (:73). The milestone verb pays at first power delivered by a
        // vanilla machine type (EngMilestones: windmill, waterwheel, helve, pulverizer, mechanized
        // quern), so it is vanilla. Maintenance is wearandtear and nothing else (:86).
        //
        // EngMilestones.cs:73 clamps M to 1 when wearandtear is absent. That clamp predates the
        // milestone verb and is now one verb tighter than this table; see the 0.5.18 note. Left
        // alone deliberately (the clamps stay), and the clamp only ever lowers M, so it cannot
        // make this audit fire.
        ["ENG"] = new()
        {
            ["assembly"] = new[] { V("BEBehaviorWindmillRotor.OnInteract") },
            ["milestone"] = new[] { V("first power delivered, vanilla machine types") },
            ["maintenance"] = new[] { M("wearandtear") },
        },

        // MEL. fighting is the vanilla kill seam (MelRanKillPatches.cs:105). Blocking has a
        // VANILLA FLOOR: with Combat Overhaul it rides CO's block events, and without it the
        // vanilla shield absorb (MelPatches.cs:138-147). Both verbs are always reachable, which is
        // exactly the inference a modid guess would have got wrong.
        ["MEL"] = new()
        {
            ["fighting"] = new[] { V("Entity.ReceiveDamage kill attribution") },
            ["blocking"] = new[] { V("ModSystemWearableStats.applyShieldProtection"), M("combatoverhaulfork") },
        },

        // RAN. the vanilla kill seam, ranged branch (MelRanKillPatches.cs:228).
        ["RAN"] = new()
        {
            ["shooting"] = new[] { V("Entity.ReceiveDamage kill attribution, ranged") },
        },

        // TEM. warding is the vanilla rift ward (TemPatches.cs:232), repair the vanilla
        // translocator (:263) with betterjonas as a second route (:190), temporal kill the vanilla
        // kill seam (MelRanKillPatches.cs:249). Wayfaring is the Conjunction tether bridge
        // (TemTetherBridge.cs:41,67).
        ["TEM"] = new()
        {
            ["warding"] = new[] { V("BlockEntityRiftWard.OnInteract") },
            ["repair"] = new[] { V("BlockEntityStaticTranslocator.DoRepair") },
            ["temporalkill"] = new[] { V("Entity.ReceiveDamage kill attribution, temporal") },
            ["wayfaring"] = new[] { M("themarginaliaconjunction") },
        },

        // ARC. the domain is dormant without rustboundmagic (roster RequiredMod). The five school
        // keys all ride ONE cast seam (ArcPatches.cs:324, SpellBase.ConsumeManaForSpell), so they
        // stand or fall together. The rest: meditation (:54, the rank trickle), inscription (:352),
        // laboratory (:387 Spellforge, :570 rituals, :621 foundry), arcane kill (RBM mobs only).
        ["ARC"] = new()
        {
            ["foundational"] = new[] { T("rustboundmagic.src.system.abstracts.SpellBase"), T("rustboundmagic.src.system.interfaces.SpellBase") },
            ["evocation"] = new[] { T("rustboundmagic.src.system.abstracts.SpellBase"), T("rustboundmagic.src.system.interfaces.SpellBase") },
            ["alteration"] = new[] { T("rustboundmagic.src.system.abstracts.SpellBase"), T("rustboundmagic.src.system.interfaces.SpellBase") },
            ["incantation"] = new[] { T("rustboundmagic.src.system.abstracts.SpellBase"), T("rustboundmagic.src.system.interfaces.SpellBase") },
            ["conjuration"] = new[] { T("rustboundmagic.src.system.abstracts.SpellBase"), T("rustboundmagic.src.system.interfaces.SpellBase") },
            ["meditation"] = new[] { M("rustboundmagic") },
            ["inscription"] = new[] { T("rustboundmagic.src.common.item.tool.ItemToolSpellcastingRM") },
            ["laboratory"] = new[] { T("rustboundmagic.src.common.blockentity.station.BlockEntityStationSpellforgeRM"), T("rustboundmagic.src.system.modsystems.ModSystemRitualsRM") },
            ["arcanekill"] = new[] { M("rustboundmagic") },
        },

        // BEE. the domain is dormant without a beehive mod (roster RequiredAnyMods). Comb work
        // rides the vanilla skep, which FGC's populated skep inherits (BeePatches.cs:58), and
        // rendering the vanilla honeycomb press, so both are reachable whenever the domain is live.
        // Hiving arrives through either mod (:77 oreki interact, :95 FGC brood pot); wintering is
        // oreki's frame feeding alone.
        ["BEE"] = new()
        {
            ["combwork"] = new[] { V("BlockSkep.GetDrops") },
            ["rendering"] = new[] { V("honeycomb press") },
            ["hiving"] = new[] { M("orekiwoofsbeehives"), M("fromgoldencombs") },
            ["wintering"] = new[] { M("orekiwoofsbeehives") },
        },
    };

    /// <summary>The gates for one verb, or null when this table has no entry for it.</summary>
    public static Gate[]? GatesFor(string domainCode, string technique) =>
        Table.TryGetValue(domainCode, out var verbs) && verbs.TryGetValue(technique, out var gates)
            ? gates
            : null;

    /// <summary>Whether one alternative is satisfied in THIS session. Each arm is the same
    /// expression the patcher evaluates, so the audit cannot disagree with the patcher about
    /// whether a seam could attach.</summary>
    private static bool Satisfied(ICoreAPI api, Gate gate) => gate.Kind switch
    {
        GateKind.Vanilla => true,
        GateKind.Mod => api.ModLoader.IsModEnabled(gate.Value),
        GateKind.Type => AccessTools.TypeByName(gate.Value) != null,
        _ => true,
    };

    /// <summary>Reachable when any one alternative is satisfied. An empty gate list means
    /// reachable: absence of knowledge is never evidence of a shortfall.</summary>
    public static bool IsReachable(ICoreAPI api, Gate[] gates)
    {
        if (gates.Length == 0) return true;
        foreach (Gate g in gates) if (Satisfied(api, g)) return true;
        return false;
    }

    /// <summary>What is missing, for the warning line: "needs primitivesurvival" or "needs one of
    /// indappledgroves, immersivewoodworking". Vanilla alternatives are never listed, because a
    /// verb with one is never unreachable.</summary>
    public static string Missing(Gate[] gates)
    {
        var names = new List<string>();
        foreach (Gate g in gates)
        {
            if (g.Kind == GateKind.Vanilla) continue;
            string name = g.Kind == GateKind.Mod ? g.Value : ShortType(g.Value);
            if (!names.Contains(name)) names.Add(name);
        }
        if (names.Count == 0) return "no recorded requirement";
        return names.Count == 1 ? $"needs {names[0]}" : $"needs one of {string.Join(", ", names)}";
    }

    /// <summary>Last namespace segment of a type name, so a warning line stays readable.</summary>
    private static string ShortType(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        return dot < 0 ? typeName : typeName.Substring(dot + 1);
    }
}
