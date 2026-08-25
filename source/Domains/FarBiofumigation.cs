using System.Collections.Generic;
using System.Text;

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// Biofumigation: Tier 2 of the soil stabilisers (scope 2026-08-24, built 2026-08-24).
///
/// WHAT IT IS. Grow a brassica to the stage that carries its harvest, then turn it in with a hoe
/// instead of picking it. The plant goes into the ground and a large share of EVERY family's
/// sickness on that one tile goes with it. The price is the forfeited harvest: a whole rotation
/// slot spent healing rather than eating, which is a decision rather than busywork.
///
/// It is real. Brassica tissue releases isothiocyanates as it breaks down, and mustard green
/// manure is a standing practice against soil-borne disease for exactly this reason. Broad-
/// spectrum is why the clear is not limited to brassicas, and why a mustard slot is worth
/// spending at all.
///
/// THE RULE THE WHOLE STABILISER SCOPE HANGS ON. Time already heals for free, so a stabiliser is
/// never required. The moment one becomes mandatory it stops being a decision and becomes a
/// chore. Nothing here is needed by a farmer who rotates by family; it exists for the CONSTRAINED
/// rotation, the dedicated flax plot and the farmer short of land or seed.
///
/// GATE THE KNOWLEDGE, NEVER THE LABOUR (scope, ruled). The clear happens for an Untrained hand
/// exactly as it does for a Master, because the soil does not care who swung the hoe and because
/// a cure that silently does nothing is the same silent-punishment failure the sickness readout
/// already shipped once. What FAR rank buys is being TOLD: the Grower's Eye hint that says this
/// ground would take a turn-in, and the confirmation naming what eased and by how much.
///
/// A TURN-IN DOES BANK PRACTICE (RULED 2026-08-25, reversing the first build). It pays the tilling
/// verb, because working a green crop into the ground is real soil labour and the hand doing it
/// learns what a hoe teaches anywhere else. The first build read "teaches nothing" as "no XP" and
/// withheld it to close a plant-and-turn-in farming loop, but that loop costs a full grow, a
/// rotation slot and the whole harvest, which is a far worse rate than tilling new ground. All
/// withholding it achieved was making the cure feel like a penalty for using it.
///
/// STRICTLY ONE TILE, NO RADIUS. An earlier draft gave it a small area effect to save clicks;
/// that was wrong, and is recorded so it is not re-proposed. Turning in nine plants costs exactly
/// what harvesting nine plants costs, and the bed was going to be worked either way.
/// </summary>
public static class FarBiofumigation
{
    /// <summary>The taxonomy key from crop-families.json. Plural, because the file is.</summary>
    private const string Brassicas = "brassicas";

    private static Config.TcmGlobalConfig? Cfg => AlmanacTcmModSystem.ServerInstance?.GlobalConfig;

    /// <summary>Off leaves the hoe doing exactly what vanilla does. Sickness being off implies
    /// this is off too: there would be nothing to clear, and destroying a ripe crop for nothing
    /// is the worst outcome available.</summary>
    public static bool Enabled => FarSoilSickness.Enabled && (Cfg?.SickBiofumigation ?? true);

    public static double ClearShare => GameMath.Clamp(Cfg?.SickBiofumigationClearShare ?? 0.80, 0, 1);

    /// <summary>The level at which the turn-in starts reporting itself. Read on the client too,
    /// where the server config is not present and the default stands in — the same arrangement
    /// the tiring line already runs on.</summary>
    public static int ReadRank => Cfg?.SickBiofumigationReadRank ?? Rank.Novice;

    // ------------------------------------------------------------------ what qualifies

    /// <summary>
    /// True when this standing plant is one a hoe should turn in: a brassica the taxonomy knows,
    /// at or past the stage that carries its harvest.
    ///
    /// AT OR PAST, not exactly at. A bolted mustard has MORE biomass than a ripe one, not less,
    /// and a farmer who let the slot run long should not be told the cure has expired. It also
    /// keeps the test honest for the three lifecycle archetypes at once: grains peak at the last
    /// stage, roots and leaves peak mid-life and bolt, and DAR's herbs change crop entirely.
    ///
    /// Side-agnostic on purpose. The hint is composed on the client and the act runs on the
    /// server, and both must agree about what qualifies or the tooltip lies.
    /// </summary>
    public static bool IsCandidate(ICoreAPI api, Block? cropBlock)
    {
        if (!Enabled || cropBlock?.CropProps == null) return false;
        string? id = FarFamiliarity.CropIdOf(api, cropBlock);
        if (id == null || FarFamiliarity.FamilyOf(id) != Brassicas) return false;
        return AtOrPastHarvest(api, cropBlock);
    }

    /// <summary>The harvest stage, read from the live drop ladder rather than assumed to be the
    /// last one. A crop whose ladder cannot be walked falls back to the vanilla meaning of ripe,
    /// which is the conservative answer: it asks for MORE growth, never less.</summary>
    private static bool AtOrPastHarvest(ICoreAPI api, Block cropBlock)
    {
        if (!int.TryParse(cropBlock.LastCodePart(), out int stage) || stage <= 0) return false;

        var curve = FarYieldCurve.Of(api, cropBlock);
        if (curve != null && curve.PeakStage > 0) return stage >= curve.PeakStage;

        int stages = cropBlock.CropProps?.GrowthStages ?? 0;
        return stages > 0 && stage >= stages;
    }

    /// <summary>True when the player is holding something that can turn a crop in. The tag test
    /// comes first because vanilla's hoe.json declares no tool type at all (it carries the
    /// <c>tool-hoe</c> TAG instead), and the class test catches every modded hoe that subclasses
    /// the vanilla one, Primitive Survival's included.</summary>
    public static bool HoeInHand(IPlayer? byPlayer)
    {
        var held = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible;
        if (held == null) return false;
        if (held is ItemHoe) return true;
        if (held.Tool == EnumTool.Hoe) return true;
        return held.Code?.Path?.Contains("hoe") == true;
    }

    /// <summary>The ground under a standing crop, when that ground is farmland. Null otherwise:
    /// a brassica growing wild on plain soil has no sickness record to clear.</summary>
    public static BlockEntityFarmland? FarmlandUnder(IWorldAccessor world, BlockPos cropPos) =>
        world.BlockAccessor.GetBlockEntity(cropPos.DownCopy()) as BlockEntityFarmland;

    // ------------------------------------------------------------------ the act

    /// <summary>
    /// Turns the crop in. Returns false when this was not a turn-in at all, which is the signal
    /// for the hoe seam to let vanilla have its swing back.
    ///
    /// Order matters: the crop comes out FIRST, so the tile is genuinely bare at the moment the
    /// clear is charged and its remembered occupancy is true rather than a second stale. The
    /// farmland is told the crop is gone the same way a break tells it, so its own state does not
    /// go on believing something is growing there.
    /// </summary>
    public static bool TurnIn(ICoreServerAPI sapi, IPlayer byPlayer, BlockPos cropPos)
    {
        if (!Enabled || byPlayer == null || cropPos == null) return false;

        Block? crop = sapi.World.BlockAccessor.GetBlock(cropPos);
        if (!IsCandidate(sapi, crop)) return false;
        if (!HoeInHand(byPlayer)) return false;

        var farmland = FarmlandUnder(sapi.World, cropPos);
        if (farmland == null) return false;

        string cropName = crop!.GetPlacedBlockName(sapi.World, cropPos).ToLowerInvariant();

        if (crop!.Sounds?.Break != null) sapi.World.PlaySoundAt(crop.Sounds.Break, cropPos, 0, null);
        sapi.World.BlockAccessor.SetBlock(0, cropPos);
        farmland.OnCropBlockBroken();

        var soil = sapi.World.BlockAccessor.GetBlock(farmland.Pos);
        if (soil?.Sounds?.Place != null) sapi.World.PlaySoundAt(soil.Sounds.Place, farmland.Pos, 0.4, null);

        // The hoe pays for the swing exactly as a till does, and against the same slot vanilla
        // damages: DoTill reads its slot argument for the stack but damages the active hotbar slot.
        var hand = byPlayer.InventoryManager?.ActiveHotbarSlot;
        hand?.Itemstack?.Collectible?.DamageItem(sapi.World, byPlayer.Entity, hand);

        var cleared = FarSoilSickness.Biofumigate(sapi, farmland.Pos, ClearShare);

        // A turn-in banks the tilling verb (RULED 2026-08-25, reversing the first build's reading
        // of "teaches nothing"). Working a green crop into the ground is real soil labour and the
        // hand that does it is learning the same thing a hoe teaches anywhere else, so refusing
        // the credit made the cure feel like a punishment for using it. The exploit the old
        // reading was guarding against does not survive contact: the plant-and-turn-in loop costs
        // a full grow, a rotation slot and the harvest, which is a far worse XP rate than simply
        // tilling new ground, and every other farming verb already pays for work with a cost.
        //
        // Exact position plus a 30-second bucket, matching the fertilizing seam's shape: a bed
        // turned in plant by plant pays per tile, and a mis-swing on the same tile cannot.
        AlmanacTcmModSystem.ServerInstance?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechTilling,
            System.HashCode.Combine("biofum", farmland.Pos.X, farmland.Pos.Y, farmland.Pos.Z,
                sapi.World.ElapsedMilliseconds / 30000));

        Report(sapi, byPlayer, cropName, cleared);
        return true;
    }

    /// <summary>The confirmation. Everyone is told the crop went into the ground, because an
    /// unexplained vanished harvest reads as a bug in any language; only a ranked hand is told
    /// what it bought.</summary>
    private static void Report(ICoreServerAPI sapi, IPlayer byPlayer, string cropName,
                               List<FarSoilSickness.Cleared>? cleared)
    {
        var to = byPlayer as IServerPlayer;
        if (to == null) return;

        int level = FarDomain.LevelOf(byPlayer);
        if (level < ReadRank)
        {
            to.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("almanactcm:far-biofum-plain", cropName), EnumChatType.Notification);
            return;
        }

        if (cleared == null || cleared.Count == 0)
        {
            to.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("almanactcm:far-biofum-clean", cropName), EnumChatType.Notification);
            return;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < cleared.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Lang.Get("almanactcm:far-biofum-eased",
                FamilyName(cleared[i].Family), (int)cleared[i].Before, (int)cleared[i].After));
        }
        to.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.Get("almanactcm:far-biofum-done", cropName, sb.ToString()), EnumChatType.Notification);
    }

    /// <summary>The family's own word where the pack has one, its taxonomy key otherwise. Same
    /// resolution the sickness readout uses, so the two surfaces cannot disagree.</summary>
    public static string FamilyName(string family) =>
        Lang.HasTranslation("almanactcm:far-family-" + family)
            ? Lang.Get("almanactcm:far-family-" + family).ToLowerInvariant()
            : family;
}
