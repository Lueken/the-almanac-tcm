using System;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Engine;

/// <summary>
/// Provenance that survives the kitchen (docs/design/food-provenance-chain.md, RULED 2026-08-13).
///
/// THE RULING. A grower's mark carries through PROCESSING, so a Grandmaster's grain makes marked
/// flour and marked dough and those keep longer in a pantry. A COOK DISPLACES IT: the moment a COO
/// act produces something edible, FAR's mark is removed and the cook's replaces it. Jeffrey's
/// reasoning is the rule: regardless of how well cultivated an item is, a bad cook still ruins it
/// and a great chef gets more out of it. Provenance is a handoff, not an accumulation.
///
/// THE DISPLACEMENT TEST IS A CONJUNCTION, and both halves are load-bearing:
///
///     a COO act PRODUCED this output   AND   the output is DIRECTLY EDIBLE
///
/// Raw crops are edible. <c>grain</c>, vegetables and fruit all carry real nutrition. Drop the
/// first half and every harvest in the game would strip its own farmer's mark at the moment of
/// creation. That is why <see cref="IsDirectlyEdible"/> is only ever consulted from a COO stamp
/// path, and why this class exposes no free-standing "does COO own this food" helper: the first
/// caller to forget the first half would reintroduce the bug silently, and only on marked produce.
///
/// WHY EDIBILITY IS THE TEST AT ALL. Two earlier proposals were wrong. A heat test fails because
/// the ACA mixing bowl makes salads with no heat and a salad is plainly a cook's work. A per-seam
/// dish/processing table fails because it needs a row for every appliance in every food mod and
/// goes stale the first time one ships a new verb. Vanilla already draws the line we want and
/// maintains it for its own reasons (verified 1.22.5):
///
///     flour   no nutrition at all                 -> ingredient, FAR carries
///     dough   nutritionPropsWhenInMeal only       -> ingredient, FAR carries
///     bread   nutritionProps                      -> food, COO displaces
///     grain   nutritionProps + WhenInMeal         -> food, but a FAR harvest made it
///
/// In the API that distinction is exactly <see cref="CollectibleObject.NutritionProps"/> (the
/// top-level JSON field, eat it as it is) versus the <c>nutritionPropsWhenInMeal</c> entry under
/// <c>attributes</c>, which only contributes inside a dish.
/// </summary>
public static class FoodProvenance
{
    /// <summary>One domain's mark, as a set of attributes that travel together. The level attr
    /// decides which input wins; the whole key set moves with it or none of it does, because half
    /// a mark (a tier with no name) renders as nothing while still driving a spoilage factor.
    /// That exact split is what made the 0.4.38 perish bug so hard to see.</summary>
    public sealed class Mark
    {
        public readonly string Domain;
        public readonly string LevelAttr;
        public readonly string[] Keys;

        public Mark(string domain, string levelAttr, params string[] keys)
        {
            Domain = domain; LevelAttr = levelAttr; Keys = keys;
        }
    }

    /// <summary>
    /// The marks that ride a food through processing.
    ///
    /// FAR's heirloom generation is deliberately ABSENT from the carried key set. It is a SEED
    /// property (how many sowings the strain still out-yields for), and flour cannot be planted.
    /// Carrying it would print "Bred for the yield" on a bag of flour.
    ///
    /// POT and GLA are not here: they mark the VESSEL, which is not an input to the food. TAI and
    /// MET are not food at all.
    /// </summary>
    private static readonly Mark[] Carryable =
    {
        new Mark("FAR", Domains.FarBonusPatches.GrownTierAttr,
                 Domains.FarBonusPatches.GrownByAttr,
                 Domains.FarBonusPatches.GrownTierAttr),

        new Mark("COO", Domains.CooBonusPatches.CookTierAttr,
                 Domains.CooBonusPatches.CookByAttr,
                 Domains.CooBonusPatches.CookByNameAttr,
                 Domains.CooBonusPatches.CookTierAttr,
                 Domains.CooBonusPatches.CookCxAttr),

        new Mark("BRE", Domains.BrePatches.BreTierAttr,
                 Domains.BrePatches.BreByAttr,
                 Domains.BrePatches.BreByNameAttr,
                 Domains.BrePatches.BreTierAttr),
    };

    /// <summary>Can this collectible hold a food mark at all? Perishable OR nourishing: an
    /// ingredient like flour has no nutrition but does perish, and gating on nutrition alone would
    /// drop the carry exactly where the chain needs it most. Tools and blocks satisfy neither, so
    /// the carry cannot leak onto them.</summary>
    public static bool IsFood(CollectibleObject? coll)
    {
        if (coll == null) return false;
        if (coll.NutritionProps != null) return true;
        var props = coll.TransitionableProps;
        if (props == null) return false;
        foreach (var p in props)
            if (p?.Type == EnumTransitionType.Perish) return true;
        return false;
    }

    /// <summary>Is this something a player eats as it is, rather than an ingredient that only
    /// counts inside a dish? See the conjunction warning on the class: consult this ONLY from a
    /// COO stamp path, never on its own.</summary>
    public static bool IsDirectlyEdible(CollectibleObject? coll) => coll?.NutritionProps != null;

    /// <summary>Read a mark's level off a stack, or -1 when the stack does not carry it.</summary>
    private static int LevelOf(Mark mark, ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs?.HasAttribute(mark.LevelAttr) != true) return -1;
        return attrs.GetInt(mark.LevelAttr, -1);
    }

    /// <summary>Copy one mark's whole key set from source to destination.</summary>
    private static void CopyMark(Mark mark, ItemStack from, ItemStack to)
    {
        foreach (string key in mark.Keys)
        {
            var v = from.Attributes[key];
            if (v != null) to.Attributes[key] = v.Clone();
        }
    }

    /// <summary>Remove one mark's whole key set.</summary>
    private static void StripMark(Mark mark, ItemStack stack)
    {
        foreach (string key in mark.Keys) stack.Attributes.RemoveAttribute(key);
    }

    // --------------------------------------------- merging into an already-marked output slot

    /// <summary>
    /// A machine that produces into a slot it may reuse (the quern is the first) has to be told
    /// how to merge, because ATTRIBUTES ARE PART OF STACK IDENTITY in Vintage Story.
    ///
    /// THE BUG THIS EXISTS FOR (found in play 2026-08-13, and it was ours). Vanilla creates the
    /// output and merges it into the slot INSIDE the method body, so a postfix that marks the slot
    /// afterwards guarantees the next merge fails: vanilla makes plain flour, the slot holds marked
    /// flour, the two are different items, and the new flour is ejected unmarked. That fires even
    /// when every grain came from one farmer, so simply putting a stack in and collecting later
    /// (the way anyone actually uses a quern) silently shed the mark from grind two onward.
    ///
    /// THE FIX IS ORDER, NOT POLICY. <see cref="TakeForMerge"/> lifts the mark off the slot BEFORE
    /// vanilla runs, so the merge always compares plain against plain and always succeeds.
    /// <see cref="RestoreAfterMerge"/> puts a mark back afterwards.
    ///
    /// ALL OR NOTHING on the restore, which is what keeps it honest. The mark survives only if the
    /// incoming batch matches what was already there. Grinding one Grandmaster grain and then
    /// sixty-three ordinary ones does NOT launder a full stack up to Grandmaster; it produces plain
    /// flour, because the batch was mixed. Realistic, self-explanatory in play, and it removes the
    /// only real exploit without costing the normal case anything.
    /// </summary>
    public readonly struct PendingMerge
    {
        public readonly ItemStack? SlotMarks;   // a clone carrying what the slot held, or null
        public readonly bool SlotWasEmpty;
        public PendingMerge(ItemStack? slotMarks, bool slotWasEmpty)
        {
            SlotMarks = slotMarks; SlotWasEmpty = slotWasEmpty;
        }
    }

    /// <summary>Lift every carried mark off the output slot so vanilla's merge sees plain goods.
    /// Returns what was lifted, for <see cref="RestoreAfterMerge"/>.</summary>
    public static PendingMerge TakeForMerge(ItemStack? slotStack)
    {
        if (slotStack?.Collectible == null) return new PendingMerge(null, true);

        ItemStack? held = null;
        foreach (Mark mark in Carryable)
        {
            if (!slotStack.Attributes.HasAttribute(mark.LevelAttr)) continue;
            held ??= slotStack.Clone();
            StripMark(mark, slotStack);
        }
        return new PendingMerge(held, false);
    }

    /// <summary>Re-apply a mark to the merged stack. The incoming source and what the slot held
    /// must agree, or the batch is mixed and comes out plain.</summary>
    public static void RestoreAfterMerge(PendingMerge pending, ItemStack? source, ItemStack? merged, ICoreAPI? api = null)
    {
        if (merged?.Collectible == null || !IsFood(merged.Collectible)) return;

        foreach (Mark mark in Carryable)
        {
            int incoming = LevelOf(mark, source);
            int existing = pending.SlotWasEmpty ? incoming : LevelOf(mark, pending.SlotMarks);

            if (incoming < 0 || incoming != existing)
            {
                // Mixed batch, or the new input carries nothing. Either way the stack is plain now.
                if (!pending.SlotWasEmpty && existing >= 0 && api != null)
                    TcmLog.Cat(api, "far", $"mixed batch on {merged.Collectible.Code}: {mark.Domain} mark dropped");
                continue;
            }

            ItemStack? donor = pending.SlotWasEmpty ? source : pending.SlotMarks;
            if (donor != null) CopyMark(mark, donor, merged);
        }
    }

    /// <summary>
    /// Carry provenance from a set of inputs onto a produced food.
    ///
    /// HIGHEST-RANKED INPUT PER DOMAIN (RULED): each domain contributes at most one mark, taken
    /// from whichever input holds the highest level for it. A stew built from four farmers' crops
    /// names one farmer, not four, and a tooltip never becomes a receipt.
    ///
    /// No-ops on a non-food output, and never overwrites a mark the output already carries.
    /// </summary>
    public static void Carry(ItemStack?[] inputs, ItemStack? output, ICoreAPI? api = null)
    {
        if (output?.Collectible == null || inputs == null) return;
        if (!IsFood(output.Collectible)) return;

        foreach (Mark mark in Carryable)
        {
            if (output.Attributes.HasAttribute(mark.LevelAttr)) continue; // already marked, leave it

            ItemStack? best = null;
            int bestLevel = -1;
            foreach (ItemStack? input in inputs)
            {
                if (input == null) continue;
                int level = LevelOf(mark, input);
                if (level > bestLevel) { bestLevel = level; best = input; }
            }

            if (best == null || bestLevel < 0) continue;
            CopyMark(mark, best, output);
        }
    }

    // ------------------------------------------------------------ the grid seam

    /// <summary>Install the widest carry: every grid recipe. Dough, sausage assembly and most
    /// food-mod crafts route through OnCreatedByCrafting, so one postfix covers them all. MET
    /// patches the same method for tool marks and returns immediately on non-tools; this one is
    /// guarded to food, so the two never see each other's outputs.
    ///
    /// Registered through Try(...) from the mod system rather than by attribute, per
    /// CONVENTIONS.md section 6: a bad seam must warn and skip, never abort Start.</summary>
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var target = AccessTools.Method(typeof(CollectibleObject),
            nameof(CollectibleObject.OnCreatedByCrafting));
        if (target == null)
        {
            TcmLog.Error(api, "food provenance: CollectibleObject.OnCreatedByCrafting not found; grid carry inactive");
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(FoodProvenance), nameof(GridCarryPostfix))));
        TcmLog.Info(api, $"food provenance carry hooked (grid recipes, {Carryable.Length} marks)");
    }

    public static void GridCarryPostfix(ItemSlot[] allInputSlots, ItemSlot outputSlot)
    {
        ItemStack? output = outputSlot?.Itemstack;
        if (output?.Collectible == null || allInputSlots == null) return;
        if (!IsFood(output.Collectible)) return;   // tools and blocks are MET's business, not ours

        var stacks = new ItemStack?[allInputSlots.Length];
        for (int i = 0; i < allInputSlots.Length; i++) stacks[i] = allInputSlots[i]?.Itemstack;
        Carry(stacks, output, outputSlot?.Inventory?.Api);
    }

    /// <summary>
    /// The displacement: a COO act has just stamped <paramref name="stack"/>, so the grower's mark
    /// gives way if the result is something a player eats.
    ///
    /// Call this from EVERY COO stamp path. There are four (StampCooked and the three propagation
    /// hops), and a mark that survives on one of them is a mark that shows up next to the cook's,
    /// which is the visible failure. FAR's own contributors additionally treat a COO mark as
    /// precedence, so a missed path degrades to a hidden mark rather than a doubled one.
    /// </summary>
    public static void CookDisplacesGrower(ItemStack? stack, ICoreAPI? api = null)
    {
        if (stack?.Collectible == null) return;
        if (!IsDirectlyEdible(stack.Collectible)) return;   // an ingredient: the farmer keeps it

        foreach (Mark mark in Carryable)
        {
            if (mark.Domain != "FAR") continue;
            if (!stack.Attributes.HasAttribute(mark.LevelAttr)) return;
            StripMark(mark, stack);

            // Logged because the edibility test reads mod-supplied data. A mod that gives a true
            // INGREDIENT real nutritionProps would displace where it should carry, and this line is
            // how that shows up in play rather than in a bug report six weeks later.
            if (api != null)
                TcmLog.Cat(api, "coo", $"cook displaces grower on {stack.Collectible.Code}: FAR mark removed");
            return;
        }
    }
}
