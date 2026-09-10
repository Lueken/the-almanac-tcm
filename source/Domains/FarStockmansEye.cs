using System;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// The Stockman's Eye (FAR 0.5.8). The Grower's Eye applied to livestock: vanilla hands the
/// animal's temperament to everyone for free, so the gate takes it away and sells it back, and
/// returns more than vanilla gave.
///
/// WHY THIS EXISTS. Vanilla milking fails on one roll,
/// <c>aggroChance = Math.Min(1 - generation / 3f, 0.95f)</c>, so a wild-caught animal refuses
/// 95 times in 100 and a third-generation one never refuses at all. The whole difficulty is a
/// husbandry-progression signal, and vanilla renders it as three fuzzy words the player cannot
/// act on ("will" / "might" / silence). Players read that as a broken mechanic rather than as
/// "your herd is wild" — the community report that prompted this (Vinni, 2026-09-09) asked for
/// consolation XP for failing, which would have paid for nothing and drowned the one signal
/// telling them to breed up. The answer is to make the signal legible instead.
///
/// NOT A DIFFICULTY CHANGE. Nothing here touches <c>aggroChance</c>. FAR's shearing ruling
/// ("the beginner's shears wound": NERF-FIRST, no GM reduction, because the animal's generation
/// is what shears it clean) settled the posture for livestock handling verbs, and milking is the
/// same situation. Skill reads the animal; generation calms it.
///
/// TWO CHANNELS, both on RANK (the ground/plant split has no analogue here — an animal is one
/// thing, read at two depths):
///   • Untrained: the lactating window only. No temperament at all.
///   • Novice+:   THE ANIMAL — how she is likely to take being handled.
///   • Apprentice+: THE LINE — how far off a herd that stands quiet.
///
/// The species difference falls out on its own. The temperament line keys on the animal's real
/// aggro-state chance, not on generation alone, so a female goat (chanceByType "*": 0.0) reads
/// "she will refuse you, but she will not turn" while a ewe (0.75) does not, and no string ever
/// names a species.
/// </summary>
public static class FarStockmansEye
{
    /// <summary>Vanilla's own clean-line threshold: <c>1 - generation / 3f</c> reaches zero here.</summary>
    private const int SettledGeneration = 3;

    /// <summary>Vanilla refuses to begin milking while stress sits above this (BehaviorMilkable).</summary>
    private const float StressFloor = 0.1f;

    /// <summary>
    /// Rewrites what a milkable animal tells the player about herself.
    ///
    /// PREFIX + __state, NOT a bare postfix. Entity info text is one buffer shared by every
    /// behavior on the entity, so the farmland trick of <c>dsc.Clear()</c> and recomposing would
    /// erase whatever ran before us. The prefix records the length on entry; the postfix truncates
    /// back to it, which removes exactly this behavior's segment and nothing else. Safe because a
    /// postfix runs before any later behavior has appended, so our segment is always the tail.
    /// Translation-proof too: no string matching anywhere.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorMilkable), nameof(EntityBehaviorMilkable.GetInfoText))]
    public static class MilkableReadPatch
    {
        public static void Prefix(StringBuilder infotext, out int __state)
            => __state = infotext?.Length ?? 0;

        public static void Postfix(EntityBehaviorMilkable __instance, StringBuilder infotext, int __state)
        {
            Entity? entity = __instance?.entity;
            ICoreAPI? api = entity?.Api;
            if (api == null || infotext == null) return;
            if (api.Side != EnumAppSide.Client) return;
            if (!FarFamiliarity.EyeEnabled(api)) return;

            // Vanilla appended nothing: she is dead, has no multiply behavior, or the lactating
            // window has closed. There is nothing to gate, so leave the hover alone.
            if (infotext.Length <= __state) return;

            IPlayer? player = (api as ICoreClientAPI)?.World?.Player;
            if (player == null) return;

            // Only step in while she is actually milkable today. Otherwise vanilla printed the
            // bare "Lactating for N days." with no temperament in it, and there is nothing to
            // take away. Read the attribute rather than the private property behind it.
            IGameCalendar cal = entity!.World.Calendar;
            double lastMilked = entity.WatchedAttributes.GetFloat("lastMilkedTotalHours");
            if (cal.TotalHours - lastMilked < cal.HoursPerDay) return;

            int daysLeft = LactatingDaysLeft(__instance!, entity, cal);
            if (daysLeft <= 0) return;

            int level = FarGrowerEye.FarLevelOf(api, player);

            infotext.Length = __state;   // drop vanilla's segment, keep every other behavior's
            infotext.AppendLine(Lang.Get("almanactcm:far-stock-lactating", daysLeft));

            // Riled reads before rank, because it is the only state that is about right now
            // rather than about the animal. It also clears in about a second, which vanilla
            // never says and which is the whole reason the wording sends them back in.
            if (entity.WatchedAttributes.GetFloat("stressLevel") > StressFloor)
            {
                infotext.AppendLine(Lang.Get("almanactcm:far-stock-stressed"));
                return;
            }

            if (level < Rank.Novice) return;   // the takeaway: days only, no read of the animal

            int generation = entity.WatchedAttributes.GetInt("generation");
            float refusal = Math.Min(1f - generation / 3f, 0.95f);
            float rage = RageChance(entity);

            // Ordered so the kinder fact wins: a settled animal never refuses at all, and an
            // animal that refuses without turning is a different problem from one that fights.
            if (refusal <= 0f) infotext.AppendLine(Lang.Get("almanactcm:far-stock-settled"));
            else if (rage <= 0f) infotext.AppendLine(Lang.Get("almanactcm:far-stock-norage"));
            else if (refusal >= 0.9f) infotext.AppendLine(Lang.Get("almanactcm:far-stock-wild"));
            else infotext.AppendLine(Lang.Get("almanactcm:far-stock-part"));

            if (refusal > 0f)
                infotext.AppendLine(Lang.Get("almanactcm:far-stock-refusal"));

            if (level < Rank.Apprentice) return;

            int births = Math.Max(0, SettledGeneration - generation);
            infotext.AppendLine(births switch
            {
                0 => Lang.Get("almanactcm:far-stock-line-done"),
                1 => Lang.Get("almanactcm:far-stock-line-one"),
                _ => Lang.Get("almanactcm:far-stock-line-many", births),
            });
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Whole days of lactation left, the same arithmetic vanilla prints. <c>lactatingDaysAfterBirth</c>
    /// is private and per-entity-JSON, so it is read reflectively and falls back to vanilla's own
    /// default of 21 rather than guessing; <c>TotalDaysLastBirth</c> is public on the multiply
    /// behavior, so that half needs no reflection.
    /// </summary>
    private static int LactatingDaysLeft(EntityBehaviorMilkable milkable, Entity entity, IGameCalendar cal)
    {
        float window;
        try { window = Traverse.Create(milkable).Field("lactatingDaysAfterBirth").GetValue<float>(); }
        catch { window = 21f; }
        if (window <= 0f) window = 21f;

        var mul = entity.GetBehavior<EntityBehaviorMultiply>();
        if (mul == null) return 0;

        double elapsed = Math.Max(0.0, cal.TotalDays - mul.TotalDaysLastBirth);
        return (int)(window - elapsed);
    }

    /// <summary>
    /// The chance a refusal also turns her on you, which is what separates a ewe from a doe.
    /// It lives on the entity's own resolved <c>aggressiveondamage</c> emotion state (chanceByType
    /// is baked per entity type at load), inside a private array on the behavior.
    ///
    /// Returns 1 when it cannot be read, so an unknown animal is described as dangerous rather
    /// than falsely safe. Being wrong in the "she will turn" direction costs a player nothing;
    /// being wrong the other way gets them headbutted on our advice.
    /// </summary>
    private static float RageChance(Entity entity)
    {
        var states = entity.GetBehavior<EntityBehaviorEmotionStates>();
        if (states == null) return 0f;   // no emotion behavior at all: she genuinely cannot turn

        try
        {
            var available = Traverse.Create(states).Field("availableStates").GetValue<EmotionState[]>();
            if (available == null) return 1f;

            foreach (EmotionState s in available)
                if (s != null && s.Code == "aggressiveondamage") return s.Chance;

            return 0f;   // she has emotion states, but no aggressive one to enter
        }
        catch { return 1f; }
    }
}
