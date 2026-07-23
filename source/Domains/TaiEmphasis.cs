using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>The book toggle's packet: the player flips their TAI emphasis on the Tailoring detail
/// page and the client sends this to the server (client -> server). Emphasis is 0 Lasting / 1 Warm /
/// 2 Cool (see <see cref="TaiDomain"/>).</summary>
[ProtoContract]
public class TaiEmphasisPacket
{
    [ProtoMember(1)] public int Emphasis;
}

/// <summary>
/// TAI emphasis — the Grandmaster's Warm / Lasting / Cool choice, set from a three-way switch on the
/// Tailoring detail page in the Almanac book (see CallingsTab). Stored as a per-player
/// WatchedAttribute: server-authoritative, persists with the player entity, and syncs to the client
/// so the book reads it directly. A tiny client-&gt;server packet flips it. Read at the creating act
/// by the stamp (<see cref="TaiMark"/> via <see cref="EmphasisOf"/>); it only bites at Grandmaster,
/// where the quality curves gate the emphasis bump on level. Mirrors <see cref="AlcEmphasis"/>, but
/// three-way rather than a bool.
/// </summary>
public static class TaiEmphasis
{
    /// <summary>Per-player emphasis: 0 Lasting (the base default) / 1 Warm / 2 Cool.</summary>
    public const string Attr = "almanactcm:taiEmphasis";
    private const string ChannelName = "almanactcmtai";

    private static IClientNetworkChannel? clientChannel;

    public static void RegisterServer(ICoreServerAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TaiEmphasisPacket>()
            .SetMessageHandler<TaiEmphasisPacket>((fromPlayer, packet) =>
            {
                var wa = fromPlayer?.Entity?.WatchedAttributes;
                if (wa == null) return;
                int e = packet.Emphasis;
                if (e < TaiDomain.EmphLasting || e > TaiDomain.EmphCool) e = TaiDomain.EmphLasting;
                wa.SetInt(Attr, e);
                wa.MarkPathDirty(Attr);
            });
    }

    public static void RegisterClient(ICoreClientAPI api)
    {
        clientChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<TaiEmphasisPacket>();
    }

    /// <summary>The player's stored emphasis (0 Lasting / 1 Warm / 2 Cool). Read server-side at the
    /// stamp; harmless below Grandmaster (the read gates the emphasis bump on level).</summary>
    public static int EmphasisOf(IPlayer? player) =>
        player?.Entity?.WatchedAttributes.GetInt(Attr, TaiDomain.EmphLasting) ?? TaiDomain.EmphLasting;

    /// <summary>Flip the local player's emphasis from the book toggle: send it to the server (which is
    /// authoritative and syncs it back) and set it locally for an instant redraw.</summary>
    public static void Set(ICoreClientAPI capi, int emphasis)
    {
        clientChannel?.SendPacket(new TaiEmphasisPacket { Emphasis = emphasis });
        capi.World.Player?.Entity?.WatchedAttributes.SetInt(Attr, emphasis);
    }
}
