using System;
using System.Text;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The barrel read (0.5 BRE ruling 2026-08-22, first of the pair; the Brewmaster's measure in
/// BrePatches is the second): rank reads the sealed cask. Vanilla's sealed barrel is a black
/// box: the BE renders no block info of its own and the dialog only previews recipes before
/// the seal, so this ladder is purely ADDITIVE and takes nothing away. Scaled by the VIEWER's
/// BRE rank from synced data only (seal state, recipe, timestamps): an Apprentice reads
/// roughly how long the dark still needs, a Journeyman reads what it is turning toward and
/// the time to the day, a Master reads the count it should give. Below Apprentice the dark
/// keeps its counsel. The tagline was always the spec: what the barrel does in the dark
/// depends on who closed it, and now what you can SEE of it depends on who is looking.
///
/// Vanilla barrel only in v1. The Fermentaria clay fermenter shares the verb but carries its
/// own mod-side display; extending the read there is a recorded follow-up, not an oversight.
/// </summary>
public static class BreBarrelRead
{
    private static int breDomainId = -2;

    private static int BreDomainId()
    {
        if (breDomainId != -2) return breDomainId;
        breDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == BreDomain.Code) { breDomainId = i; break; }
        return breDomainId;
    }

    /// <summary>The viewer's BRE level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player.</summary>
    public static int BreLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(BreDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = BreDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    [HarmonyPatch(typeof(BlockBarrel), nameof(BlockBarrel.GetPlacedBlockInfo))]
    public static class BarrelReadPatch
    {
        public static void Postfix(IWorldAccessor world, BlockPos pos, IPlayer forPlayer, ref string __result)
        {
            if (world?.Api == null || forPlayer == null) return;
            if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBarrel be) return;
            if (!be.Sealed || be.CurrentRecipe == null || be.CurrentRecipe.SealHours <= 0) return;

            int level = BreLevelOf(world.Api, forPlayer);
            if (level < Rank.Apprentice) return;

            double remainHours = Math.Max(0.0,
                be.CurrentRecipe.SealHours - (world.Calendar.TotalHours - be.SealedSinceTotalHours));
            double hoursPerDay = Math.Max(1.0, world.Calendar.HoursPerDay);

            var sb = new StringBuilder(__result ?? "");
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');

            if (level < Rank.Journeyman)
            {
                double days = remainHours / hoursPerDay;
                string rough = days < 1.0
                    ? Lang.Get("almanactcm:bre-read-lessday")
                    : Lang.Get("almanactcm:bre-read-days", (int)Math.Ceiling(days));
                sb.AppendLine(Lang.Get("almanactcm:bre-read-apprentice", rough));
            }
            else
            {
                string outName = be.CurrentRecipe.Output?.ResolvedItemStack?.GetName() ?? "?";
                string timeText = remainHours >= hoursPerDay
                    ? Lang.Get("almanactcm:bre-read-days-precise", Math.Round(remainHours / hoursPerDay, 1))
                    : Lang.Get("almanactcm:bre-read-hours", (int)Math.Ceiling(remainHours));
                sb.AppendLine(Lang.Get("almanactcm:bre-read-journeyman", outName, timeText));

                if (level >= Rank.Master && be.Inventory != null && be.Inventory.Count >= 2
                    && be.CurrentRecipe.Matches(new ItemSlot[] { be.Inventory[0], be.Inventory[1] }, out int outSize)
                    && outSize > 0)
                {
                    var res = be.CurrentRecipe.Output?.ResolvedItemStack;
                    var props = res == null ? null : BlockLiquidContainerBase.GetContainableProps(res);
                    string amount = props != null && props.ItemsPerLitre > 0
                        ? Lang.Get("almanactcm:bre-read-litres", Math.Round(outSize / props.ItemsPerLitre, 1))
                        : Lang.Get("almanactcm:bre-read-portions", outSize);
                    sb.AppendLine(Lang.Get("almanactcm:bre-read-master", amount));
                }
            }
            __result = sb.ToString();
        }
    }
}
