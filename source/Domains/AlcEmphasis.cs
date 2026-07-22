using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>The book toggle's packet: the player flips their ALC emphasis on the Alchemy detail
/// page and the client sends this to the server (client -> server).</summary>
[ProtoContract]
public class AlcEmphasisPacket
{
    [ProtoMember(1)] public bool Potent;
}

/// <summary>
/// ALC emphasis — the Grandmaster's Potent/Lasting choice, set from a switch on the Alchemy detail
/// page in the Almanac book (see CallingsTab). Stored as a per-player WatchedAttribute: server-
/// authoritative, persists with the player entity, and syncs to the client so the book reads it
/// directly. A tiny client->server packet flips it. Read at the creating act by the stamp
/// (AlcBrand via <see cref="IsPotent"/>); it only bites at Grandmaster, where PotencyMul/DurationMul
/// gate on level.
///
/// This replaces the original ingredient-quantity detection, which was dead on arrival: alchemy
/// 2.1.11 potion cooking recipes are hard-fixed at minQuantity=maxQuantity=1 (no concentration
/// lever, one cook already yields quantity 100) and grid poultices are fixed grids — so quantity
/// could never signal emphasis. The choice is the player's, made in the book, not inferred from the pot.
/// </summary>
public static class AlcEmphasis
{
    /// <summary>Per-player emphasis: 1 = Potent, 0 = Lasting (the base default).</summary>
    public const string Attr = "almanactcm:alcEmphasis";
    private const string ChannelName = "almanactcmalc";

    private static IClientNetworkChannel? clientChannel;

    public static void RegisterServer(ICoreServerAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<AlcEmphasisPacket>()
            .SetMessageHandler<AlcEmphasisPacket>((fromPlayer, packet) =>
            {
                var wa = fromPlayer?.Entity?.WatchedAttributes;
                if (wa == null) return;
                wa.SetInt(Attr, packet.Potent ? 1 : 0);
                wa.MarkPathDirty(Attr);
            });
    }

    public static void RegisterClient(ICoreClientAPI api)
    {
        clientChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<AlcEmphasisPacket>();
    }

    /// <summary>True if the player's stored emphasis is Potent (else Lasting). Read server-side at the
    /// stamp; harmless below Grandmaster (the read gates emphasis on level).</summary>
    public static bool IsPotent(IPlayer? player) =>
        (player?.Entity?.WatchedAttributes.GetInt(Attr, 0) ?? 0) == 1;

    /// <summary>Flip the local player's emphasis from the book toggle: send it to the server (which is
    /// authoritative and syncs it back) and set it locally for an instant redraw.</summary>
    public static void Set(ICoreClientAPI capi, bool potent)
    {
        clientChannel?.SendPacket(new AlcEmphasisPacket { Potent = potent });
        capi.World.Player?.Entity?.WatchedAttributes.SetInt(Attr, potent ? 1 : 0);
    }
}
