using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Reads complete native relay handshakes through packet tables, including nested scene appearance.
/// Nothing here registers an opcode, grants a capability or authenticates a connection.
/// </summary>
internal static class RelayHandshakeWire
{
    /// <summary>
    /// Requires every structure the native handshake consumes or whose reply frames its accept path: the join
    /// with its modern identity tail, the reconnect map, the handshake settings, the accepted and scene signals, the
    /// three seed tables and the session signature the scene report embeds. A field retyped under the same
    /// name fails here, at activation, instead of on the first packet.
    /// </summary>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RelayJoinRequest", "Native relay handshake",
            ("key", "RelaySessionKey"), ("unknown_n3", "u32"), ("unknown_n4", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "RelayReconnectMapRequest", "Native relay handshake",
            ("client_version", "u32"), ("unknown_n2", "wstr"), ("unknown_n3", "u32"), ("unknown_n4", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "RelaySceneReport", "Native relay handshake",
            ("entity_id", "u32"), ("nickname", "wstr"),
            ("presets", "WeaponPresetList"), ("signature", "SessionSignatureData"));
        PacketLayoutContract.RequireFields(tables, build, "RelayHandshakeSettings", "Native relay handshake",
            ("unknown_n0", "bool"), ("unknown_n1", "u8"), ("unknown_n2", "u8"), ("unknown_n3", "u8"),
            ("unknown_n4", "u8"), ("unknown_n5", "u8"), ("unknown_n6", "u8"));
        PacketLayoutContract.RequireSignature(tables, build, "RelayJoinAcceptedSignal", "Native relay handshake",
            "", "unknown_n0:u32");
        RequireSeedLayouts(tables, build);
        PacketLayoutContract.RequireEmpty(tables, build, "RelaySceneAcceptedSignal", "Native relay handshake");
        LoginBurstWire.RequireSessionSignature(tables, build);
    }

    /// <summary>Checks the seed fields before either independently installed match row publishes services.</summary>
    internal static void RequireSeedLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedTable", "Native relay handshake",
            ("entity_id", "u32"), ("unknown_8", "list<u32>"), ("unknown_24", "list<u32>"));
        PacketLayoutContract.RequireFields(tables, build, "RelayDefenceSeedTable", "Native relay handshake",
            ("entity_id", "u32"), ("unknown_8", "list<u8>"));
        PacketLayoutContract.RequireFields(tables, build, "RelayCriticalSeedCancelTable", "Native relay handshake",
            ("entity_id", "u32"), ("unknown_8", "list<u32>"));
    }

    /// <summary>
    /// Reads the native join (kr-1256 sub_95FD30, cn-774 sub_B5B530, tw-454 sub_A7B050) as the account-bearing
    /// admission: the twenty-byte session key plus the account word and nickname in its modern tail (fields of
    /// <c>RelayJoinRequest</c>). The whole body is validated by the table reader.
    /// </summary>
    internal static MatchAuthenticate ReadAccountJoin(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var join = PacketCodec.Read(tables, build, "RelayJoinRequest", body);
        var key = (IReadOnlyDictionary<string, object?>)join["key"]!;
        return new(
            PacketCodec.Write(tables, build, "RelaySessionKey", key),
            Convert.ToInt32(join["unknown_n3"], CultureInfo.InvariantCulture),
            (string)join["unknown_n4"]!);
    }

    /// <summary>
    /// Reads the reconnect map (kr-1256 sub_9F02F0, cn-774 sub_8EB210, tw-454 sub_A6B6E0): the version must match its owner, then the
    /// map key, the entity word and the nickname (fields of <c>RelayReconnectMapRequest</c>).
    /// </summary>
    internal static MatchMapNamed ReadMap(PacketTables tables, string build, int clientVersion, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RelayReconnectMapRequest", body);
        if (Convert.ToInt32(fields["client_version"], CultureInfo.InvariantCulture) != clientVersion)
            throw new PacketFormatException("Unsupported match reconnect version.", 0);
        return new(
            Convert.ToInt32(fields["unknown_n3"], CultureInfo.InvariantCulture),
            (string)fields["unknown_n4"]!,
            (string)fields["unknown_n2"]!);
    }

    /// <summary>
    /// Reads the scene report completely before projecting its identity. The native writers
    /// kr-1256 sub_10F32C0, cn-774 sub_488AD0 and tw-454 sub_475C20 differ in identity order
    /// and appearance containers; those differences belong to RelaySceneReport and its nested tables.
    /// </summary>
    internal static MatchSceneRegistered ReadScene(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "RelaySceneReport", body);
        // cn-774 and tw-454 write no account word (cn-774 sub_B593D0), so they support only the identity handshake.
        if (!fields.TryGetValue("account", out var account))
            throw new PacketFormatException($"The {build} scene report carries no account word for an account handshake.", 0);
        return new(
            Convert.ToInt32(account, CultureInfo.InvariantCulture),
            Convert.ToInt32(fields["entity_id"], CultureInfo.InvariantCulture),
            (string)fields["nickname"]!);
    }

    /// <summary>Reads the scene report completely and projects the actor and display identity every modern build writes.</summary>
    internal static MatchSceneIdentityRegistered ReadSceneIdentity(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "RelaySceneReport", body);
        return new(Convert.ToInt32(fields["entity_id"], CultureInfo.InvariantCulture), (string)fields["nickname"]!);
    }
}
