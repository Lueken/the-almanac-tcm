using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// ALC — the READ side of the Alchemist's Brand (rank-bonus-design.md §ALC Axis 6, RULED 2026-07-11;
/// AMENDMENT 2026-07-22 for the 2.1.11 potion seams). The Brand minted at creation (AlcPatches) is read
/// at DELIVERY to scale what the remedy does, NERF-FIRST, on the two verified fields per family:
///
///   • Poultices/bandages [vanilla] — Health + EffectDurationSec, scaled by a transient field swap
///     around CollectibleBehaviorHealingItem.OnHeldInteractStop (the delivery point). Unbranded stacks
///     (loot/pre-update) read as vanilla — only a stamped Brand scales.
///   • Revive HP [vanilla] — a downed player wakes on a Brand-scaled fraction of MaxHealth (unbranded
///     remedy ~vanilla-full; a branded remedy ~22% climbing to a HARD 0.80 cap, held even at GM), on the
///     verified ApplyHealing Source==Revive seam. Plus exhausted-on-revive: the revive zeroes Vigor's
///     stamina, and Vigor's own exhaustion/recovery takes over (soft — no-op without Vigor).
///   • Potions [alchemy, conditional] — StrengthMul + Duration, read from PotionData.SourceStack at
///     PotionConsumableLogic.TryProcessPotionEffects; potency scales GetStrengthMultiplier, duration
///     scales the context built by the potion registry (PotionContext/BuildPotionDef on alchemy 2.1.11,
///     EffectContext/Build on 2.1.17). Reflection-only (TCM never references Alchemy.dll).
///
/// The GM emphasis (Potent = deeper strength / Lasting = longer duration) rides the same read: Potent
/// adds its bump to potency, Lasting to duration. Provenance is a bottom-of-tooltip maker line (J up).
/// </summary>
public static class AlcBrandPatches
{
    private static ICoreServerAPI? sapi;

    /// <summary>Set in the healing-item OnHeldInteractStop prefix, consumed in the ApplyHealing prefix
    /// (the revive fires synchronously inside that call). null = not a remedy revive; -1 = unbranded
    /// remedy (exhaust, no HP scale); &gt;=0 = the branded remedy's level (exhaust + HP scale).</summary>
    private static int? pendingReviveLevel;

    /// <summary>Set in the potion TryProcessPotionEffects prefix (from SourceStack), consumed by the
    /// GetStrengthMultiplier + BuildPotionDef postfixes that run synchronously inside it. null = not a
    /// branded potion apply.</summary>
    private static int? pendingPotionLevel;
    private static bool pendingPotionPotent;

    public static void RegisterServer(ICoreServerAPI api) => sapi = api;

    // ------------------------------------------------------------ potion read (alchemy, conditional)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var logic = AccessTools.TypeByName("Alchemy.PotionConsumableLogic");
        var apply = logic == null ? null : AccessTools.Method(logic, "TryProcessPotionEffects");
        var strength = logic == null ? null : AccessTools.Method(logic, "GetStrengthMultiplier");
        // alchemy 2.1.17 renamed the potion pipeline: PotionRegistry.BuildPotionDef -> EffectRegistry.Build,
        // PotionContext -> EffectContext. Same signature (string, float) and the same int Duration, so the
        // postfix below is untouched and only the lookup is dual-version. 2.1.11's PotionRegistry has no
        // method named "Build", so the fallback cannot resolve the wrong seam on either version.
        var registry = AccessTools.TypeByName("Alchemy.EffectRegistry")
                    ?? AccessTools.TypeByName("Alchemy.PotionRegistry");
        var build = registry == null ? null
                  : AccessTools.Method(registry, "Build") ?? AccessTools.Method(registry, "BuildPotionDef");
        if (apply != null && strength != null && build != null)
        {
            harmony.Patch(apply,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(AlcBrandPatches), nameof(PotionApplyPrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(AlcBrandPatches), nameof(PotionApplyFinalizer))));
            harmony.Patch(strength, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcBrandPatches), nameof(StrengthPostfix))));
            harmony.Patch(build, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcBrandPatches), nameof(BuildPotionDefPostfix))));
            TcmLog.Info(api, $"ALC potion Brand read hooked via {registry!.Name}.{build.Name} (potency + duration scaling at drink)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ALC potion read seams not found (alchemy); potion Brand scaling inactive (poultices unaffected)");
    }

    /// <summary>Read the batch Brand off the potion being drunk (PotionData.SourceStack), so the two
    /// scaling postfixes that run synchronously inside can apply it. Reflection: TCM never links Alchemy.</summary>
    public static void PotionApplyPrefix(object data)
    {
        pendingPotionLevel = null;
        var src = Traverse.Create(data)?.Field("SourceStack")?.GetValue() as ItemStack;
        if (!AlcBrand.HasBrand(src)) return;
        pendingPotionLevel = AlcBrand.LevelOf(src);
        pendingPotionPotent = AlcBrand.IsPotent(src);
    }

    public static void PotionApplyFinalizer() => pendingPotionLevel = null;

    /// <summary>Potency: scale the potion's strength multiplier by the maker's Brand (Potent emphasis
    /// included). Only inside a branded TryProcessPotionEffects (pendingPotionLevel set) — other callers
    /// of GetStrengthMultiplier see no pending brand and are untouched.</summary>
    public static void StrengthPostfix(ref float __result)
    {
        if (pendingPotionLevel is not int level) return;
        __result *= (float)AlcDomain.PotencyMul(level, pendingPotionPotent);
    }

    /// <summary>Duration: scale the built PotionContext.Duration by the maker's Brand (Lasting emphasis
    /// included). Reflection on the returned PotionContext.</summary>
    public static void BuildPotionDefPostfix(object __result)
    {
        if (__result == null || pendingPotionLevel is not int level) return;
        var t = Traverse.Create(__result).Property("Duration");
        int dur = t.GetValue<int>();
        if (dur > 0) t.SetValue((int)(dur * AlcDomain.DurationMul(level, pendingPotionPotent)));
    }

    // ------------------------------------------------------------ poultice/bandage read (vanilla)

    /// <summary>Scale the healing item's Health + EffectDurationSec by the Brand for the delivery, via a
    /// transient field swap (the behavior instance is shared per collectible, but server game logic is
    /// single-threaded, so the swap is safe and always restored in the postfix). Also arm the revive HP
    /// scale for the revive branch. Server-only — never mutates the client-side behavior instance.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorHealingItem), nameof(CollectibleBehaviorHealingItem.OnHeldInteractStop))]
    public static class HealingDeliveryPatch
    {
        public static void Prefix(CollectibleBehaviorHealingItem __instance, ItemSlot slot, EntityAgent byEntity, out float[]? __state)
        {
            __state = null;
            if (byEntity?.World?.Side != EnumAppSide.Server) return;
            var stack = slot?.Itemstack;

            // Arm the revive-HP scale + exhaustion for the revive branch (fires synchronously below).
            pendingReviveLevel = AlcBrand.HasBrand(stack) ? AlcBrand.LevelOf(stack) : -1;

            if (!AlcBrand.HasBrand(stack)) return; // unbranded: vanilla numbers
            int level = AlcBrand.LevelOf(stack);
            bool potent = AlcBrand.IsPotent(stack);
            __state = new[] { __instance.Health, __instance.EffectDurationSec };
            __instance.Health *= (float)AlcDomain.PotencyMul(level, potent);
            __instance.EffectDurationSec *= (float)AlcDomain.DurationMul(level, potent);
        }

        public static void Postfix(CollectibleBehaviorHealingItem __instance, float[]? __state)
        {
            if (__state != null)
            {
                __instance.Health = __state[0];
                __instance.EffectDurationSec = __state[1];
            }
            pendingReviveLevel = null;
        }
    }

    // ------------------------------------------------------------ revive HP + exhaustion (vanilla)

    /// <summary>Revive HP scale + exhausted-on-revive. Vanilla's ApplyHealing special-cases
    /// Source==Revive to SET Health to the passed amount (9999 -&gt; full); we replace that amount with a
    /// Brand-scaled fraction of MaxHealth (hard 0.80 cap) and zero Vigor stamina so the player wakes
    /// exhausted. Only fires inside a remedy revive (pendingReviveLevel armed above).</summary>
    [HarmonyPatch(typeof(EntityBehaviorHealth), "ApplyHealing")]
    public static class ReviveScalePatch
    {
        public static void Prefix(EntityBehaviorHealth __instance, DamageSource damageSource, ref float damage)
        {
            if ((int)damageSource.Source != 4) return;        // EnumDamageSource.Revive
            if (pendingReviveLevel is not int level) return;  // only remedy-triggered revives

            var entity = Traverse.Create(__instance).Field("entity").GetValue<Entity>();
            if (entity == null) return;

            if (level >= 0)
                damage = (float)(AlcDomain.ReviveFraction(level) * __instance.MaxHealth);

            // Exhausted-on-revive (soft, Vigor): zero the stamina tree; Vigor's own exhaustion + recovery
            // curve then takes over. No-op without Vigor (the tree is absent).
            var tree = entity.WatchedAttributes.GetTreeAttribute("vigorstamina");
            if (tree != null)
            {
                tree.SetFloat("currentStamina", 0f);
                entity.WatchedAttributes.MarkPathDirty("vigorstamina");
            }
        }
    }

    // ------------------------------------------------------------ truthful numbers tooltip

    /// <summary>The Brand's numbers, visible on the shelf (RULED 2026-08-01, refined same day: the
    /// leading number is the TRUE delivered value, with the maker's contribution beside it as a
    /// delta, green for a lift, red for the Untrained penalty). "3.7 (+0.7)" reads as one truth;
    /// base-plus-footnote made the player do arithmetic. Vanilla renders the whole info as one
    /// localized line with three formatted numbers, so for a branded stack we skip the original
    /// and emit the SAME Lang key with augmented number strings. Application time carries no brand
    /// effect and stays bare; deltas that round to zero are suppressed, so a Novice-band brand
    /// renders exactly like vanilla. Runs both sides off synced stack attributes, so shelf and
    /// effect agree; the delivery-time scaling itself is unchanged.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorHealingItem), nameof(CollectibleBehaviorHealingItem.GetHeldItemInfo))]
    public static class HealingTooltipPatch
    {
        public static bool Prefix(CollectibleBehaviorHealingItem __instance, ItemSlot inSlot, System.Text.StringBuilder dsc)
        {
            var stack = inSlot?.Itemstack;
            if (!AlcBrand.HasBrand(stack)) return true;   // unbranded: vanilla line, untouched

            int level = AlcBrand.LevelOf(stack);
            bool potent = AlcBrand.IsPotent(stack);
            float h = __instance.Health, d = __instance.EffectDurationSec, a = __instance.ApplicationTimeSec;

            string hs = Engine.TcmTooltip.TrueValue(h, AlcDomain.PotencyMul(level, potent));
            string ds = Engine.TcmTooltip.TrueValue(d, AlcDomain.DurationMul(level, potent));
            dsc.AppendLine(Lang.Get("healing-item-info", hs, ds, $"{a:F1}"));
            return false;
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Alchemist's Brand maker line (Journeyman up). Reads the alcBy tag written on
    /// branded remedies and potions. Placement, order and spacing belong to
    /// <see cref="Engine.ProvenanceLine"/>; this only decides what ALC has to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(AlcBrand.ByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        int level = attrs!.GetInt(AlcBrand.LevelAttr);
        return
            level >= Rank.Grandmaster ? Lang.Get("almanactcm:alc-master-by", name)
            : level >= Rank.Master ? Lang.Get("almanactcm:alc-compounded-by", name)
            : level >= Rank.Journeyman ? Lang.Get("almanactcm:alc-prepared-by", name)
            : null;
    }
}
