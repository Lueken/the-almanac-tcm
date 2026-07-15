using System.Reflection;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// MET Grandmaster signature (rank-bonus-design.md §162 Axis 6, stage 2;
/// docs/design/met-makers-mark-stage2.md). A GM's finished work carries ONE of two
/// silent edge marks, by type:
///   • <b>Honed</b> — +1 effective armor-piercing on attack (weapons). Dual path:
///     CO's native ArmorPiercingTier when Combat Overhaul is present, else a vanilla
///     DamageSource.DamageTier bump at the attack seam only (never the shared mining
///     tier). Both built; runtime-detected. CO stays OPTIONAL.
///   • <b>Durable</b> — a deeper wear-resistance cut (tools), on top of the maker
///     quality pool, via a probabilistic skip in CollectibleObject.DamageItem.
///
/// The flags live on the tool HEAD and ride it for life: through Toolsmith disassembly
/// and onto the assembled tool (the head-for-life model, same as the maker quality).
/// Assigned only to Grandmaster work (MakerTierAttr == 4). Stripped on an under-ranked
/// REFORGE (Smithing+ OnSmithingFinished) — never on honing/sharpening (light
/// maintenance, not smith-work). This file holds the constants, the type classifier,
/// and the assignment helper; the consuming Harmony patches are added per build step.
/// </summary>
public static class MetSignature
{
    /// <summary>Weapon signature: +1 effective armor-piercing on attack. Set only on
    /// Grandmaster weapons.</summary>
    public const string HonedAttr = "almanactcm:honed";

    /// <summary>Tool signature: deeper wear resistance. Set only on Grandmaster tools.</summary>
    public const string DurableAttr = "almanactcm:durable";

    /// <summary>Grandmaster tier index (Leveling.Domain.TierOf: 0=Novice … 4=GM). The
    /// signature is GM-only; Master and below carry provenance + quality but no edge.</summary>
    public const int GrandmasterTier = 4;

    public enum SignatureKind { None, Weapon, Tool }

    /// <summary>Optional CO-native weapon test, installed at load only when Combat
    /// Overhaul is present (see MetConditionalPatches). Lets the classifier recognise CO
    /// weapons that don't carry a vanilla <see cref="EnumTool"/>. Null = CO absent, so the
    /// vanilla EnumTool classification is authoritative.</summary>
    public static System.Func<ItemStack, bool>? CoWeaponClassifier;

    /// <summary>Assign the GM signature to a freshly-marked stack, by type. GM-only
    /// (<paramref name="makerTier"/> &lt; 4 → nothing). A weapon gets Honed; a classifiable
    /// tool gets Durable; a bare tool HEAD (neither yet) gets nothing here and picks its
    /// signature up when it becomes a tool at assembly. Idempotent — never re-flags a
    /// stack that already carries a signature (so a copied head-flag is preserved).</summary>
    public static void Assign(ItemStack? stack, int makerTier)
    {
        if (stack?.Collectible == null || makerTier < GrandmasterTier) return;
        if (HasSignature(stack)) return;

        switch (Classify(stack))
        {
            case SignatureKind.Weapon: stack.Attributes.SetBool(HonedAttr, true); break;
            case SignatureKind.Tool: stack.Attributes.SetBool(DurableAttr, true); break;
            // None → a bare head: leave unmarked; assembly assigns by the finished tool.
        }
    }

    /// <summary>Copy an existing signature from a source stack (a marked head) onto a
    /// freshly assembled tool, preserving the head-for-life model through assembly. No-op
    /// if the source carries none. Returns true if a flag was copied.</summary>
    public static bool CopySignature(ItemStack? from, ItemStack? to)
    {
        if (from == null || to == null || HasSignature(to)) return false;
        if (from.Attributes.GetBool(HonedAttr)) { to.Attributes.SetBool(HonedAttr, true); return true; }
        if (from.Attributes.GetBool(DurableAttr)) { to.Attributes.SetBool(DurableAttr, true); return true; }
        return false;
    }

    /// <summary>Remove both signature flags (under-ranked reforge). The provenance mark
    /// and its quality are left intact — only the GM edge is lost.</summary>
    public static void Strip(ItemStack? stack)
    {
        if (stack == null) return;
        stack.Attributes.RemoveAttribute(HonedAttr);
        stack.Attributes.RemoveAttribute(DurableAttr);
    }

    public static bool IsHoned(ItemStack? stack) => stack?.Attributes.GetBool(HonedAttr) ?? false;
    public static bool IsDurable(ItemStack? stack) => stack?.Attributes.GetBool(DurableAttr) ?? false;
    public static bool HasSignature(ItemStack stack) =>
        stack.Attributes.HasAttribute(HonedAttr) || stack.Attributes.HasAttribute(DurableAttr);

    /// <summary>Weapon vs tool vs neither, for signature assignment. CO melee weapons win
    /// first (they may not carry a vanilla EnumTool). Ranged/thrown weapons are Hunter's
    /// domain, not MET (RULED 2026-07-14), so they take NO MET signature. Otherwise the
    /// vanilla EnumTool decides: a melee weapon is Honed, any other tool is Durable, and a
    /// collectible with no EnumTool is a bare head or non-tool → None.</summary>
    private static SignatureKind Classify(ItemStack stack)
    {
        if (CoWeaponClassifier != null && CoWeaponClassifier(stack)) return SignatureKind.Weapon;

        EnumTool? tool = stack.Collectible?.Tool;
        if (tool == null) return SignatureKind.None;
        if (IsRangedWeapon(tool.Value)) return SignatureKind.None;   // HUN's domain, not MET
        return IsMeleeWeapon(tool.Value) ? SignatureKind.Weapon : SignatureKind.Tool;
    }

    /// <summary>Melee weapons a MET smith forges (Honed). Knife is a harvesting tool
    /// (→ Durable); Spear/Pike are primarily melee polearms so they stay here.</summary>
    private static bool IsMeleeWeapon(EnumTool tool) => tool switch
    {
        EnumTool.Sword or EnumTool.Spear or EnumTool.Pike or EnumTool.Club
            or EnumTool.Mace or EnumTool.Warhammer or EnumTool.Poleaxe
            or EnumTool.Halberd or EnumTool.Polearm => true,
        _ => false,
    };

    /// <summary>Ranged / thrown weapons. Their edge is the Hunter domain's business, not
    /// MET's, and Honed's melee armor-pierce is inert on them — so MET assigns them nothing
    /// (RULED 2026-07-14). Javelin is the thrown polearm; the melee Pike stays a MET weapon.</summary>
    private static bool IsRangedWeapon(EnumTool tool) => tool switch
    {
        EnumTool.Bow or EnumTool.Sling or EnumTool.Firearm
            or EnumTool.Crossbow or EnumTool.Javelin => true,
        _ => false,
    };
}

/// <summary>
/// The Harmony patches that CONSUME the GM signature (rank-bonus-design.md §162 Axis 6,
/// stage 2). The universal wear-skip rides a vanilla seam and is annotation-registered;
/// the Honed dual path and the reforge-strip resolve their targets at runtime and are
/// installed by <see cref="PatchConditional"/> from the mod system, so a missing Combat
/// Overhaul / Smithing+ never breaks patch load.
/// </summary>
public static class MetSignaturePatches
{
    internal static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.Instance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(MetDomain.Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }

    /// <summary>The player's MET tier (0=Novice … 4=GM), server-side. Used by the
    /// reforge-gate to compare the reforging smith against the head's frozen maker tier.</summary>
    private static int MetTierOf(IPlayer player)
    {
        int lvl = AlmanacTcmModSystem.Instance?.Server?.GetDomainSet(player)?.FindDomain(MetDomain.Code)?.Level ?? 0;
        return Domain.TierOf(lvl);
    }

    // ---------------------------------------------------- Part 1: GM wear-skip (Durable)

    /// <summary>Universal GM wear cut + the deeper Durable cut, as a probabilistic skip of
    /// a single wear point (wear is an integer). Rides the vanilla DamageItem seam, so it
    /// covers every wear source — attack, block-break, Toolsmith sharpness (a skipped hit
    /// spares that too). Server-only: durability is authoritative there, and rolling on one
    /// side avoids a client/server RNG split. Both cuts sit on top of the maker quality
    /// pool, so a GM Durable tool ≈ 1.4× baseline lifespan (kept modest by design).</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.DamageItem))]
    public static class GmWearPatch
    {
        public static void Prefix(IWorldAccessor world, ItemSlot itemSlot, ref int amount)
        {
            if (world?.Side != EnumAppSide.Server || amount <= 0) return;
            ItemStack? stack = itemSlot?.Itemstack;
            if (stack?.Collectible == null) return;
            if (stack.Attributes.GetInt(MetPatches.MakerTierAttr, -1) < MetSignature.GrandmasterTier) return;

            double skip = MetSignature.IsDurable(stack)
                ? Knob(MetDomain.DurableWearSkip, 0.18)
                : Knob(MetDomain.GmWearSkip, 0.08);
            if (skip <= 0) return;
            if (world.Rand.NextDouble() < skip) amount = 0;
        }
    }

    // ------------------------------------------------- Part 2: Honed — vanilla fallback

    /// <summary>Honed on a CO-absent server: +1 effective tool tier, which the melee attack
    /// reads as its damage tier (EntityAgent attack seam → DamageSource.DamageTier → armor
    /// mitigation). GetToolTier is shared with the mining-tier check, but this is MINING-SAFE
    /// because Honed is only ever assigned to WEAPONS, and weapons carry no miningSpeeds — so
    /// the vanilla break check fails them regardless of tier. Only registered when Combat
    /// Overhaul is absent (CO replaces the combat path and takes the native AP route instead).</summary>
    public static class HonedVanillaPatch
    {
        public static void Postfix(ItemSlot slot, ref int __result)
        {
            ItemStack? stack = slot?.Itemstack;
            if (stack == null || !MetSignature.IsHoned(stack)) return;
            __result += (int)Knob(MetDomain.HonedArmorPierce, 1);
        }
    }

    // ---------------------------------------------- Part 4: strip on under-ranked reforge

    /// <summary>Repair-gate: reforging a head is smith-work, so a smith who can't match the
    /// head's maker tier loses the GM edge when they reforge it. Postfix on Smithing+'s
    /// OnSmithingFinished (the reforge seam that already re-stamps sp:smithingQuality — the
    /// durability half). Honing/sharpening is light maintenance and never fires here, by
    /// design. Provenance + quality are untouched; only Honed/Durable strip. Fires on ALL
    /// smithing finishes but at ORIGINAL creation the head has no maker tier yet (-1), so an
    /// unranked smith forging fresh work never trips it.</summary>
    public static class ReforgeStripPatch
    {
        public static void Postfix(BlockEntityAnvil instance, ItemStack itemstack, IPlayer byPlayer)
        {
            if (instance?.Api?.Side != EnumAppSide.Server || itemstack == null || byPlayer == null) return;
            if (!MetSignature.HasSignature(itemstack)) return;

            int headTier = itemstack.Attributes.GetInt(MetPatches.MakerTierAttr, -1);
            if (headTier < MetSignature.GrandmasterTier) return;   // signature only exists on GM heads
            if (MetTierOf(byPlayer) >= headTier) return;           // equal-or-greater smith preserves it

            MetSignature.Strip(itemstack);
            TcmLog.Cat(instance.Api, TcmLog.Hooks,
                $"{byPlayer.PlayerName} reforged a GM head under-ranked; signature stripped");
        }
    }

    // ------------------------------------------------------------------ registration

    /// <summary>Install the runtime-resolved signature patches (Honed dual path +
    /// reforge-strip). Called from the mod system after PatchAll. The universal wear-skip
    /// is annotation-registered separately.</summary>
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        bool coPresent = api.ModLoader.IsModEnabled("combatoverhaulfork")
            || api.ModLoader.IsModEnabled("combatoverhaul");

        if (coPresent && TryPatchHonedCombatOverhaul(api, harmony))
        {
            TcmLog.Info(api, "Axis 6 Honed hooked to Combat Overhaul (native armor-piercing)");
        }
        else
        {
            PatchHonedVanilla(api, harmony);
            if (coPresent)
                TcmLog.Warn(api, "Combat Overhaul present but Honed CO seam not found; vanilla tier fallback active");
        }

        PatchReforgeStrip(api, harmony);
    }

    private static void PatchHonedVanilla(ICoreAPI api, Harmony harmony)
    {
        var method = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetToolTier), new[] { typeof(ItemSlot) });
        if (method == null)
        {
            TcmLog.Warn(api, "CollectibleObject.GetToolTier(ItemSlot) not found; vanilla Honed inactive");
            return;
        }
        harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(HonedVanillaPatch), nameof(HonedVanillaPatch.Postfix))));
        TcmLog.Info(api, "Axis 6 Honed hooked to vanilla tool tier");
    }

    private static void PatchReforgeStrip(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("smithingplus")) return;   // hard dep, but guard anyway
        var method = AccessTools.Method(
            AccessTools.TypeByName("SmithingPlus.ToolRecovery.ToolHeadRepairPatches"), "OnSmithingFinished");
        if (method == null)
        {
            TcmLog.Warn(api, "smithingplus present but ToolHeadRepairPatches.OnSmithingFinished not found; reforge-strip inactive");
            return;
        }
        harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(ReforgeStripPatch), nameof(ReforgeStripPatch.Postfix))));
        TcmLog.Info(api, "Axis 6 reforge-strip hooked to Smithing+");
    }

    // -------------------------------------------- Part 2: Honed — Combat Overhaul path

    // Reflection handles for CO's readonly DamageData struct (CombatOverhaul.MeleeSystems).
    // TCM never references the CO assembly at build time, so the whole CO integration is
    // reflection-only — CO stays a runtime-optional dependency and the build stays lean.
    private static ConstructorInfo? _ddCtor;
    private static FieldInfo? _ddType, _ddTier, _ddAp;

    /// <summary>Honed via Combat Overhaul's native armor-piercing. CO resolves each melee
    /// hit's DamageData in MeleeDamageType.ResolveDamageTypeData(attacker, mainHand, …); its
    /// ArmorPiercingTier is what the DirectionalTypedDamageSource exposes through IArmorPiercing
    /// and what armor mitigation reads. A postfix there rebuilds the readonly struct with +1
    /// AP when the attacker's in-hand weapon is Honed — native, thrown/mounted-safe, and it
    /// affects the real ReceiveDamage path (the source carries no Weapon ref, so the hand slot
    /// is the only place the weapon identity is in scope). Returns false → vanilla fallback.</summary>
    private static bool TryPatchHonedCombatOverhaul(ICoreAPI api, Harmony harmony)
    {
        var mdt = AccessTools.TypeByName("CombatOverhaul.MeleeSystems.MeleeDamageType");
        var dd = AccessTools.TypeByName("CombatOverhaul.MeleeSystems.DamageData");
        if (mdt == null || dd == null)
        {
            TcmLog.Warn(api, $"Honed CO: type lookup MeleeDamageType={mdt != null} DamageData={dd != null}; scanning loaded assemblies for the real names...");
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                    if (t.Name is "DamageData" or "MeleeDamageType" or "DirectionalTypedDamageSource")
                        TcmLog.Warn(api, $"Honed CO: candidate {t.FullName} in {asm.GetName().Name}");
            }
            return false;
        }

        var resolve = AccessTools.Method(mdt, "ResolveDamageTypeData");
        _ddCtor = AccessTools.Constructor(dd, new[] { typeof(EnumDamageType), typeof(int), typeof(int) });
        _ddType = AccessTools.Field(dd, "DamageType");
        _ddTier = AccessTools.Field(dd, "Tier");
        _ddAp = AccessTools.Field(dd, "ArmorPiercingTier");
        if (resolve == null || _ddCtor == null || _ddType == null || _ddTier == null || _ddAp == null)
        {
            TcmLog.Warn(api, $"Honed CO: member lookup resolve={resolve != null} ctor={_ddCtor != null} type={_ddType != null} tier={_ddTier != null} ap={_ddAp != null}");
            return false;
        }

        InstallCoWeaponClassifier(api);

        try
        {
            harmony.Patch(resolve, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(HonedCombatOverhaulPatch), nameof(HonedCombatOverhaulPatch.Postfix))));
        }
        catch (System.Exception e)
        {
            TcmLog.Warn(api, $"Honed CO patch failed to bind ({e.Message}); vanilla tier fallback active");
            return false;
        }
        return true;
    }

    /// <summary>Teach the signature classifier CO's own "is a melee weapon" test, so CO
    /// weapons that carry no vanilla EnumTool still get Honed at creation. CO marks melee
    /// weapons with the IHasMeleeWeaponActions collectible interface.</summary>
    private static void InstallCoWeaponClassifier(ICoreAPI api)
    {
        var iface = AccessTools.TypeByName("CombatOverhaul.Implementations.IHasMeleeWeaponActions");
        var gci = AccessTools.Method(typeof(CollectibleObject), "GetCollectibleInterface");
        if (iface == null || gci == null) return;
        var bound = gci.MakeGenericMethod(iface);
        MetSignature.CoWeaponClassifier = stack =>
        {
            var coll = stack?.Collectible;
            if (coll == null) return false;
            try { return bound.Invoke(coll, null) != null; }
            catch { return false; }
        };
        TcmLog.Info(api, "CO weapon classifier installed (IHasMeleeWeaponActions)");
    }

    public static class HonedCombatOverhaulPatch
    {
        public static void Postfix(Entity attacker, bool mainHand, ref object __result)
        {
            if (__result == null || _ddCtor == null) return;
            ItemSlot? slot = mainHand
                ? (attacker as EntityAgent)?.RightHandItemSlot
                : (attacker as EntityAgent)?.LeftHandItemSlot;
            if (!MetSignature.IsHoned(slot?.Itemstack)) return;

            int bonus = (int)Knob(MetDomain.HonedArmorPierce, 1);
            if (bonus <= 0) return;

            // Rebuild the readonly DamageData with +bonus armor-piercing (struct is immutable).
            object dmgType = _ddType!.GetValue(__result)!;
            int tier = (int)_ddTier!.GetValue(__result)!;
            int ap = (int)_ddAp!.GetValue(__result)!;
            __result = _ddCtor.Invoke(new object[] { dmgType, tier, ap + bonus });
        }
    }
}
