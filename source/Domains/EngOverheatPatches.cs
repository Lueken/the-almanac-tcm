using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// ENG Axis 3 (reliability), the fire Tyron staged and left unloaded (eng-overheat-design.md,
/// RULED 2026-08-02). Vanilla 1.22 already runs the whole warning apparatus live: smoke above
/// effective node speed 4.5, an OverheatValue accumulator above 5.5, and a 3% trigger at
/// overheat &gt; 1 whose payload is a discarded GetPosition(). TCM supplies only the payload,
/// riding vanilla's own accumulator untouched (additive rule):
///
///   • IGNITION: a server tick companion walks the same networks vanilla ticks; a node above
///     overheat 1.0 rolls per check, and on success its block ignites with ordinary vanilla
///     fire (BEBehaviorBurning.OnFirePlaced into an adjacent air cell). Only blocks vanilla
///     marks combustible burn: a bronze fitting smokes and survives, physics not rank.
///   • THE KEEPER LENS: the roll scales by the machine's keeper: the LAST SERVICER where a
///     wearandtear stamp stands (EngPatches, already persisted), else the RIGGER (a new
///     placement stamp, stamp-only, no XP), else vanilla-parity. Untrained 6%, parity 3%,
///     Journeyman 2%, Master 1.2%.
///   • THE GM RANK (AMENDED 2026-08-05, supersedes "THE GM TRAIT: smokes, never burns" of
///     2026-08-02): a Grandmaster-kept machine now rolls at igniteGm, half of Master, rather
///     than never rolling. Absolute immunity made the signature a physics exemption instead
///     of a quality of work, and it would have made the sellable service an exemption too.
///     GM is the best point on a monotonic, tunable curve. Last-servicer-wins still resets
///     the contract under a lesser hand.
///   • GM-ASSEMBLED BASELINE (the orphaned optional from ENG ruling 7, 2026-07-09): a
///     machine RIGGED by a Grandmaster starts with a lower part decay baseline, held until
///     the first service overwrites it. The placement stamp is the substrate that ruling
///     was waiting for.
///   • THE READING OF THE MACHINE (widened 2026-08-05): block info, viewer-rank-gated (the K1
///     posture). Journeyman reads the heat in words, from vanilla's real accumulation floor of
///     2.5 rather than its smoke edge of 4.5 (the old floor left the whole silent band
///     unwarned, so a part could burn with nothing ever shown). Master adds the effective-speed
///     number behind the words. Grandmaster gets the full instrument panel unconditionally,
///     including network torque, resistance and the surplus that actually sets equilibrium
///     speed, because the rank's value is reading a machine that is behaving, not only one
///     that is failing. Client-side throughout: MechanicalNetwork.broadcastData already syncs
///     speed, torque, resistance and available torque. Heat itself never syncs; effective
///     speed is the honest proxy the smoke already uses.
///
/// STAND-DOWN: ignition is version-gated to 1.22. The day a vanilla build arms its own
/// payload, the gate trips, TCM's roll retires, and the keeper lens moves to whatever seam
/// vanilla exposes (the antler-patch posture). Stamps, baseline, and readout carry forward.
/// </summary>
public static class EngOverheatPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    /// <summary>The game-version family this ignition payload is built for. Vanilla newer than
    /// this may have armed its own fire; ours retires rather than double-igniting.</summary>
    private const string BuiltForGameVersion = "1.22";

    /// <summary>Rigger stamp: pos -> "uid|engLevelAtPlacement". Persisted; removed on break.</summary>
    private static Dictionary<string, string> riggers = new();

    private static bool ignitionEnabled;

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;

        ignitionEnabled = GameVersion.ShortGameVersion.StartsWithOrdinal(BuiltForGameVersion);
        if (!ignitionEnabled)
            TcmLog.Warn(api, $"ENG overheat ignition RETIRED: built for {BuiltForGameVersion}, game is "
                + $"{GameVersion.ShortGameVersion}. Review whether vanilla armed its own payload; keeper stamps and readout stay live.");

        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacEngRiggers");
                if (data != null) riggers = SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
            }
            catch (Exception e) { TcmLog.Error(api, $"ENG rigger map unreadable ({e.Message}); starting empty"); }
            TcmLog.Cat(api, TcmLog.Config, $"ENG rigger stamps loaded: {riggers.Count}");
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacEngRiggers", SerializerUtil.Serialize(riggers));

        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidBreakBlock += OnDidBreakBlock;

        // The ignition companion: every 500ms, the same walk vanilla's own tick does. The
        // chance table is calibrated per 500ms check (parity 3% matches the stub's number).
        api.Event.RegisterGameTickListener(OnIgnitionTick, 500);

        TcmLog.Info(api, "ENG overheat live: keeper-scaled ignition"
            + (ignitionEnabled ? "" : " (RETIRED by version gate)")
            + ", rigger stamps, GM-assembled baseline");
    }

    // ------------------------------------------------------------ rigger stamp + GM baseline

    /// <summary>Stamp mechanical-power blocks with their rigger (stamp-only; placement stays a
    /// non-verb per the ENG razor). A Grandmaster's rig also starts its wearandtear parts at a
    /// lower decay baseline: the orphaned optional from ENG ruling 7, now that assembled-by
    /// attribution exists.</summary>
    private static void OnDidPlaceBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        if (byPlayer == null || blockSel?.Position == null || sapi == null) return;
        var be = sapi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
        if (be?.GetBehavior<BEBehaviorMPBase>() == null) return;

        int level = EngDomain.LevelOf(byPlayer);
        riggers[PosKey(blockSel.Position)] = $"{byPlayer.PlayerUID}|{level}";

        // GM-assembled baseline: parts born under a Grandmaster's hand wear slower until the
        // first service takes over the contract. Fully guarded: wearandtear optional, shapes
        // reflective, a miss costs the baseline and nothing else.
        if (Leveling.Domain.TierOf(level) < 4) return;
        try
        {
            double baseline = EngDomain.Knob(EngDomain.GmAssembledDecay, 0.92);
            if (baseline >= 1.0) return;
            foreach (var beh in be.Behaviors)
            {
                if (beh.GetType().FullName != "WearAndTear.Code.Behaviours.Part"
                    && beh.GetType().BaseType?.FullName != "WearAndTear.Code.Behaviours.Part") continue;
                var bonuses = Traverse.Create(beh).Property("Bonuses").GetValue();
                if (bonuses == null) continue;
                var t = Traverse.Create(bonuses).Field("DecayModifier");
                if (Math.Abs(t.GetValue<float>() - 1f) < 0.001f) t.SetValue((float)baseline);
            }
            be.MarkDirty(true);
        }
        catch (Exception e)
        {
            TcmLog.Cat(sapi, TcmLog.Config, $"ENG GM-assembled baseline skipped at {blockSel.Position} ({e.Message})");
        }
    }

    private static void OnDidBreakBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel)
    {
        if (blockSel?.Position != null) riggers.Remove(PosKey(blockSel.Position));
    }

    // ------------------------------------------------------------ the ignition companion

    private static void OnIgnitionTick(float dt)
    {
        if (!ignitionEnabled || sapi == null) return;
        var mech = sapi.ModLoader.GetModSystem<MechanicalPowerMod>();
        if (mech == null) return;

        // Vanilla keeps its networks in a private data holder; read-only walk, fail-open.
        if (Traverse.Create(mech).Field("data").GetValue() is not object data) return;
        if (Traverse.Create(data).Field("networksById").GetValue() is not System.Collections.IDictionary nets) return;

        foreach (object netObj in nets.Values)
        {
            if (netObj is not MechanicalNetwork net) continue;
            if (Traverse.Create(net).Field("nodes").GetValue() is not System.Collections.IDictionary nodes) continue;

            foreach (object nodeObj in nodes.Values)
            {
                if (nodeObj is not IMechanicalPowerNode node) continue;
                if (node.OverheatValue <= 1f) continue;

                // The plateau gate (2026-08-05). Vanilla's accumulator has no upper clamp in its
                // 2.5-to-4.5 band: the else-branch is floored by Math.Max(0f, ...) and capped by
                // nothing, so ANY part above 2.5 arms given time. That includes a stock windmill on
                // one large gear, which runs 3.3 at full wind. Measured on a creative rotor rig at
                // effective 3.21: twelve seconds from cold to fire, against 11s predicted.
                //
                // Real friction reaches an equilibrium temperature and stops there. A bow drill
                // turned slowly never lights, however long you turn it. Gate the roll on the node's
                // live effective speed so the same is true here, and so the contract the readout
                // already prints (warms 2.5, smokes 4.5, burns 5.5) is finally honest: until now it
                // burned at 2.5. Vanilla's accumulator is left untouched; we simply stop believing
                // it on its own. FireAt stays a compile-time const, never a Knob, because the
                // readout renders this same number client-side and Knob is server-only.
                float effective = Math.Abs(node.GearedRatio * net.Speed);
                if (effective <= FireAt) continue;

                BlockPos pos = node.GetPosition();
                if (pos == null) continue;

                // Proportional to the mistake, not binary (2026-08-06). Barely over is a slow
                // smoulder; grossly over is quick. Same principle as the gate itself.
                double chance = IgnitionChanceFor(pos) * OverspeedScale(effective);
                if (chance <= 0) continue;                       // guard only; no rank returns 0 since 2026-08-05
                if (sapi.World.Rand.NextDouble() >= chance) continue;

                TryIgnite(pos);
                node.OverheatValue = 0f;                         // the fire is the discharge
            }
        }
    }

    /// <summary>How far past the burn line a part is running, as a multiplier on its ignition
    /// chance. Zero exactly at FireAt and rising linearly from there, so 11.0 (twice the line) is
    /// unit rate and everything below it is a discount. Clamped both ends: the floor stops a part
    /// a hair over the line from being immortal, the cap stops a wildly overgeared train from
    /// igniting on the first roll.
    ///
    /// SERVER ONLY. This reads Knob, so it must never be called from the readout: C-3, the knob
    /// store does not exist client-side and a dedicated server would render defaults. The readout
    /// shows the raw unclamped fraction instead, which is arithmetic over FireAt, a compile-time
    /// const, and therefore honest on both sides.</summary>
    private static double OverspeedScale(float effective)
    {
        double over = (effective - FireAt) / FireAt;
        double floor = EngDomain.Knob(EngDomain.IgniteScaleFloor, 0.05);
        double cap = EngDomain.Knob(EngDomain.IgniteScaleCap, 3.0);
        return Math.Clamp(over, floor, cap);
    }

    /// <summary>The keeper lens: last servicer's rank where a wearandtear stamp stands, else the
    /// rigger's frozen rank, else vanilla parity. Returns the per-check ignition chance.</summary>
    private static double IgnitionChanceFor(BlockPos pos)
    {
        int level = -1;
        var be = sapi!.World.BlockAccessor.GetBlockEntity(pos);
        if (be != null && EngPatches.TryGetServicerLevel(be, out int servicerLevel)) level = servicerLevel;
        else if (riggers.TryGetValue(PosKey(pos), out string? packed) && packed != null)
        {
            string[] p = packed.Split('|');
            if (p.Length >= 2 && int.TryParse(p[1], out int riggerLevel)) level = riggerLevel;
        }

        if (level < 0) return EngDomain.Knob(EngDomain.IgniteNovice, 0.03);   // unattributed: parity
        if (level <= 0) return EngDomain.Knob(EngDomain.IgniteUntrained, 0.06);
        return Leveling.Domain.TierOf(level) switch
        {
            >= 4 => EngDomain.Knob(EngDomain.IgniteGm, 0.006),                 // best rank, not exempt
            3 => EngDomain.Knob(EngDomain.IgniteMaster, 0.012),
            2 => EngDomain.Knob(EngDomain.IgniteJourneyman, 0.02),
            _ => EngDomain.Knob(EngDomain.IgniteNovice, 0.03),
        };
    }

    /// <summary>The overdriven part burns itself out and takes nothing with it.
    ///
    /// RULED 2026-08-06. This deliberately does NOT place a vanilla fire block. It used to, and
    /// that fire spread like any other, which on a shared server means one player's gearing
    /// mistake can cost a neighbour their house. It came close twice. Non-spreading is now a
    /// property of the code rather than a tuning value: no fire block is created, so there is
    /// nothing that could propagate even if something else in the pack patched fire behaviour.
    ///
    /// The event still reads as fire. Flame and smoke particles, the vanilla fire sound, and the
    /// part is consumed with no drops. BreakBlock runs vanilla's own removal path, so multiblock
    /// parts clean up their own stubs instead of leaving orphans.</summary>
    private static void TryIgnite(BlockPos fuelPos)
    {
        var accessor = sapi!.World.BlockAccessor;
        Block fuelBlock = accessor.GetBlock(fuelPos);
        if (fuelBlock?.CombustibleProps == null) return;          // physics, not rank

        // Combustion still needs air, so a fully enclosed shaft starves. That was a happy
        // accident of the old fire-placement loop and it is kept on purpose: boxing a drive line
        // in is a real mitigation a player can choose, and it costs them the reading in exchange.
        bool hasAir = false;
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            if (accessor.GetBlock(fuelPos.AddCopy(facing)).Replaceable >= 6000) { hasAir = true; break; }
        }
        if (!hasAir) return;

        double cx = fuelPos.X + 0.5, cy = fuelPos.Y + 0.5, cz = fuelPos.Z + 0.5;
        var min = new Vec3d(cx - 0.35, cy - 0.35, cz - 0.35);
        var max = new Vec3d(cx + 0.35, cy + 0.35, cz + 0.35);

        // ColorUtil.ToRgba is ARGB exactly as named, confirmed in game 2026-08-06: passing
        // (255, 30, 140, 255) rendered bright blue, which is r=30 g=140 b=255. Ember orange is
        // therefore r=255 g=140 b=30.
        sapi.World.SpawnParticles(56f, ColorUtil.ToRgba(255, 255, 140, 30), min, max,
            new Vec3f(-0.25f, 0.1f, -0.25f), new Vec3f(0.25f, 0.85f, 0.25f),
            1.4f, -0.015f, 1.0f, EnumParticleModel.Quad);
        sapi.World.SpawnParticles(28f, ColorUtil.ToRgba(160, 60, 60, 60), min, max,
            new Vec3f(-0.15f, 0.25f, -0.15f), new Vec3f(0.15f, 0.6f, 0.15f),
            2.6f, -0.005f, 1.6f, EnumParticleModel.Quad);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/environment/fire"), cx, cy, cz,
            null, false, 24f, 1f);

        accessor.BreakBlock(fuelPos, null, 0f);
        accessor.MarkBlockDirty(fuelPos);

        TcmLog.Cat(sapi, "eng", $"overspeed ignition at {fuelPos} ({fuelBlock.Code}); the wheel found its limit, and nothing else");
    }

    // ------------------------------------------------------------ the reading of the machine

    /// <summary>Viewer-rank-gated heat reading on mechanical nodes (the K1 posture: knowledge is
    /// the rank's reveal). Client-side off the synced network speed, the same signal the smoke
    /// uses; heat itself never leaves the server. Journeyman reads hot; Grandmaster reads the
    /// ignition band.</summary>
    public static void PatchConditional(ICoreAPI api, HarmonyLib.Harmony harmony)
    {
        var m = AccessTools.DeclaredMethod(typeof(BEBehaviorMPBase), "GetBlockInfo");
        if (m == null)
        {
            TcmLog.Cat(api, TcmLog.Config, "ENG heat readout seam absent (BEBehaviorMPBase.GetBlockInfo); readout inactive");
            return;
        }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngOverheatPatches), nameof(BlockInfoPostfix))));
        TcmLog.Info(api, "ENG heat readout hooked (viewer-rank-gated, off synced network speed)");
    }

    // Vanilla's own band edges (MechanicalPowerMod.OnServerGameTick). HeatFloor is where
    // OverheatValue starts CLIMBING: the else-branch adds (eff - 2.5)/10, so anything above 2.5
    // accumulates. SmokeAt is only where vanilla starts RENDERING it. The gap between them is the
    // silent band, and the readout floor sits at HeatFloor precisely so it is no longer silent.
    private const float HeatFloor = 2.5f;
    private const float SmokeAt = 4.5f;
    private const float FireAt = 5.5f;

    public static void BlockInfoPostfix(BEBehaviorMPBase __instance, IPlayer forPlayer, StringBuilder sb)
    {
        var net = __instance?.Network;
        if (net == null || forPlayer == null) return;

        // THE SOLD READING (2026-08-05). The reading rank is the better of what the viewer knows and
        // what the machine's keeper knew: max(viewer, last servicer). A Grandmaster who services a
        // farmer's mill leaves the instrument panel behind on it, readable by the farmer, and that is
        // the service being sold. No new machinery and no new sync: EngPatches already round-trips the
        // servicer mark through the PartController's own ToTree/FromTree, so it persists to disk and
        // rehydrates client-side, which is where this postfix runs on a dedicated server.
        //
        // Servicer only, deliberately. The rigger stamp lives in a server-side savegame dictionary
        // that never reaches a client, and placement is not the sellable act anyway. Last-servicer-wins
        // already governs it: a lesser hand's later service pulls the reading back down with the
        // fire and decay contract, so the reading cannot be laundered by hiring a GM once.
        int viewerLevel = ViewerEngLevel(forPlayer);
        int readLevel = viewerLevel;
        if (__instance!.Blockentity is { } be && EngPatches.TryGetServicerLevel(be, out int servicerLevel))
            readLevel = Math.Max(readLevel, servicerLevel);

        // One test, not two: TierOf(l) < 2 is the same predicate as l < Rank.Journeyman. The
        // pair survived the 2026-08-12 threshold sweep as belt-and-braces; the braces are cut.
        if (readLevel < Rank.Journeyman) return;

        float ratio = __instance.GearedRatio;
        float effSpeed = Math.Abs(ratio * net.Speed);

        // ---- Grandmaster: the instrument panel, unconditional. The point of the rank is being able
        // to read a machine that is behaving, not just one that is failing, so this does NOT gate on
        // a heat band. Every value here is broadcast to clients by MechanicalNetwork.broadcastData,
        // so it is honest on a dedicated server. OverheatValue itself is server-only and never
        // shown; effective speed is the same proxy vanilla's smoke uses.
        if (readLevel >= Rank.Grandmaster)
        {
            float torque = Math.Abs(net.NetworkTorque);
            float resistance = net.NetworkResistance;
            float surplus = torque - resistance;      // >0 accelerating, ~0 at equilibrium, <0 slowing

            sb.AppendLine(Lang.Get("almanactcm:eng-read-header"));
            sb.AppendLine(Lang.Get("almanactcm:eng-read-drive", net.Speed.ToString("0.###"), ratio.ToString("0.##")));
            sb.AppendLine(Lang.Get("almanactcm:eng-read-effspeed", effSpeed.ToString("0.##"),
                HeatFloor.ToString("0.#"), SmokeAt.ToString("0.#"), FireAt.ToString("0.#")));
            sb.AppendLine(Lang.Get("almanactcm:eng-read-load", torque.ToString("0.##"),
                resistance.ToString("0.##"), net.TotalAvailableTorque.ToString("0.##")));

            // CAPACITY AND LOAD (2026-08-08, superseding the gross/net line of 0.4.34). Gross
            // and net were instantaneous torque: they loaf at equilibrium, thrash in transients,
            // and in game they read as a riddle. Capacity is what the sources could deliver at
            // stall, load is what the network takes, headroom is the machines you have not built
            // yet. All three are stable through transients. Opposition shows as capacity LOST
            // (absolute sum minus the magnitude of the signed sum), the fight stated in the only
            // currency that matters. Values arrive on the EngGrossTorque channel; absent a recent
            // packet the line drops rather than guessing.
            if (EngGrossTorque.TryGetReadout(net.networkId, out float _, out float capAbs, out float capEff)
                && capAbs > 0.005f)
            {
                float lost = capAbs - capEff;
                float headroom = capEff - resistance;
                if (lost > capAbs * 0.02f && lost > 0.01f)
                    sb.AppendLine(Lang.Get("almanactcm:eng-read-capacity-opposed",
                        capAbs.ToString("0.##"), lost.ToString("0.##"),
                        resistance.ToString("0.##"), headroom.ToString("0.##")));
                else
                    sb.AppendLine(Lang.Get("almanactcm:eng-read-capacity",
                        capAbs.ToString("0.##"), resistance.ToString("0.##"), headroom.ToString("0.##")));
            }

            sb.AppendLine(Lang.Get("almanactcm:eng-read-surplus",
                (surplus >= 0 ? "+" : "") + surplus.ToString("0.###")));
        }
        // ---- Master: the number behind the warning, but only once the part is actually accumulating.
        else if (readLevel >= Rank.Master && effSpeed > HeatFloor)
        {
            sb.AppendLine(Lang.Get("almanactcm:eng-read-effspeed", effSpeed.ToString("0.##"),
                HeatFloor.ToString("0.#"), SmokeAt.ToString("0.#"), FireAt.ToString("0.#")));
        }

        // ---- THE CAPACITY LADDER (2026-08-08). The exact panel skipped the ladder the heat
        // readout proved: words at Journeyman, coarse figures at Master, the instrument at GM.
        // Same synced numbers, display-gated only, so every tier is honest and the sellable
        // service (GM panel for anyone, via servicer) is untouched and advertised by the tiers.
        if (readLevel < Rank.Grandmaster
            && EngGrossTorque.TryGetReadout(net.networkId, out float _, out float capA, out float capE)
            && capA > 0.005f)
        {
            float load = net.NetworkResistance;
            bool opposed = (capA - capE) > capA * 0.02f && (capA - capE) > 0.01f;
            if (readLevel >= Rank.Master)
                sb.AppendLine(Lang.Get("almanactcm:eng-read-capacity-coarse",
                    Math.Round(capA).ToString("0"), Math.Round(load).ToString("0")));
            else
                sb.AppendLine(Lang.Get(
                    load < capE * 0.5f ? "almanactcm:eng-read-shaft-easy"
                    : load < capE * 0.85f ? "almanactcm:eng-read-shaft-room"
                    : "almanactcm:eng-read-shaft-limit"));
            if (opposed) sb.AppendLine(Lang.Get("almanactcm:eng-read-shaft-opposed"));
        }

        // ---- The heat words, Journeyman up. Floor moved 4.5 -> 2.5 (2026-08-05): the old floor sat
        // at vanilla's SMOKE edge, so the whole silent accumulation band produced no warning at all
        // and a part could burn with nothing ever shown. "Moments from fire" also drops from GM-only
        // to Master, since a Master can now see the number it refers to.
        if (effSpeed <= HeatFloor) return;

        if (effSpeed > FireAt)
        {
            sb.AppendLine($"<font color=\"{Engine.TcmTooltip.PenaltyColor}\">" + Lang.Get("almanactcm:eng-moments-from-fire") + "</font>");

            // Master up: how far past the limit, which is what actually sets the ignition rate now.
            // Raw and unclamped on purpose. OverspeedScale's floor and cap are server-side Knobs and
            // must not be read here (C-3); this is arithmetic over FireAt, a const, so it agrees on
            // both sides. The clamps only bound the roll, they do not change how overdriven a part is.
            if (readLevel >= Rank.Master)
                sb.AppendLine(Lang.Get("almanactcm:eng-read-overspeed",
                    ((effSpeed - FireAt) / FireAt * 100f).ToString("0")));
        }
        else if (effSpeed > SmokeAt)
            sb.AppendLine(Lang.Get("almanactcm:eng-running-hot"));
        else
            sb.AppendLine(Lang.Get("almanactcm:eng-warming"));
    }

    /// <summary>The looking player's own ENG level, on whichever side is asking.</summary>
    private static int ViewerEngLevel(IPlayer player)
    {
        // Server side (singleplayer integrated or dedicated): authoritative.
        if (Core?.Server != null)
        {
            var set = Core.Server.GetDomainSet(player);
            var d = set?.FindDomain(EngDomain.Code);
            if (d != null) return d.Level;
        }
        // Client side: the synced local state.
        var cm = AlmanacTcmModSystem.ClientInstance;
        var dom = cm?.Template?.FindDomain(EngDomain.Code);
        if (dom != null && cm!.Client != null && cm.Client.Domains.TryGetValue(dom.Id, out var state))
            return state.Level;
        return 0;
    }
}
