using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Projects opaque native relay keys through audited tables without issuing or granting a room capability.</summary>
internal static class RelayConnectionWire
{
    /// <summary>
    /// Requires the layouts this wire consumes: the join's nested key, the offer's endpoint and key, the
    /// endpoint's IPv4 word and port (kr-812 sub_B262B0 copies the word to sockaddr and applies ntohs to the
    /// port), and the whole session-key shape of the build — the three audited variants, because
    /// <see cref="WriteOffer"/> reads every stated key field to move it between shapes untruncated. The two
    /// match rows call this at activation, so a field retyped under the same name fails at mount, not on the
    /// first packet.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <exception cref="BubbleFighter.Fantasy.Abstractions.ConfigException">A consumed field is missing or has another type.</exception>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RelayJoinRequest", "Relay connection",
            ("key", "RelaySessionKey"));
        PacketLayoutContract.RequireFields(tables, build, "RelayConnectionOffer", "Relay connection",
            ("endpoint", "RelayEndpoint"), ("key", "RelaySessionKey"));
        PacketLayoutContract.RequireFields(tables, build, "RelayEndpoint", "Relay connection",
            ("ipv4_word", "u32"), ("port", "u16"));
        PacketLayoutContract.RequireSignature(tables, build, "RelaySessionKey", "Relay connection",
            "unknown_4:u32,unknown_8:u32,unknown_12:u32,unknown_16:u16,unknown_18:u16,unknown_20:u32",
            "unknown_4:u32,unknown_8:u32,unknown_12:u32,unknown_16:u16,unknown_20:u32",
            "unknown_4:u32,unknown_8:u32,unknown_12:u32");
    }

    /// <summary>
    /// Validates the complete join, including unknown tail fields, before extracting its exact key bytes.
    /// kr-812 sub_810110 reads key, byte and word; earlier differences live in relay-connection.yaml.
    /// No join tail is interpreted as an account, nickname or transport identity.
    /// </summary>
    internal static byte[] ReadJoinTicket(PacketTables tables, string build, ReadOnlySpan<byte> body,
        PacketReadLimits? limits = null)
    {
        var join = PacketCodec.Read(tables, build, "RelayJoinRequest", body, limits);
        var key = (IReadOnlyDictionary<string, object?>)join["key"]!;
        return PacketCodec.Write(tables, build, "RelaySessionKey", key);
    }

    /// <summary>
    /// Writes an IPv4 endpoint and already-issued key using the selected native layout.
    /// kr-812 sub_844630 delegates to NetAddress then RelaySessionKey; sub_B262B0 uses IPv4 bytes and a numeric port.
    /// Validating the key independently prevents truncation or padding between build-specific key shapes.
    /// </summary>
    /// <remarks>
    /// The baseline offer is endpoint.ipv4_word (u32), endpoint.port (u16), then the opaque key fields
    /// unknown_4/unknown_8/unknown_12 (u32), unknown_16/unknown_18 (u16) and unknown_20 (u32).
    /// Native shorter keys and their own reader addresses are defined in relay-connection.yaml.
    /// </remarks>
    internal static byte[] WriteOffer(PacketTables tables, string build, IPAddress address, ushort port,
        ReadOnlySpan<byte> ticket)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The native relay endpoint requires an IPv4 address.", nameof(address));
        if (port == 0) throw new ArgumentOutOfRangeException(nameof(port), "The native relay port must be nonzero.");
        var key = PacketCodec.ReadScalars(tables, build, "RelaySessionKey", ticket);
        return PacketCodec.Write(tables, build, "RelayConnectionOffer", new Dictionary<string, object?>
        {
            ["endpoint"] = new Dictionary<string, object?>
            {
                ["ipv4_word"] = BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes()),
                ["port"] = port,
            },
            ["key"] = key,
        });
    }
}
