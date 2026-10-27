using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes the native room automatic-start notification from the build's packet table.</summary>
internal sealed class AutoStartCodec(string protocol, PacketTables packetTables, string build,
    IOpcodeTableRegistry tables) : IMessageSender<RoomAutoStartNotified>
{
    // kr-812 sub_80C160 and cn-324 sub_5B5D00 consume one bool32 — the whole body
    // (packets/room.yaml gspRepAutoStart) — so a table that retypes it must fail when the codec
    // mounts, not on the first automatic start.
    private readonly PacketTables _packetTables = RequireLayout(packetTables, build);

    public string Protocol => protocol;

    public ValueTask SendAsync(ISession session, RoomAutoStartNotified message, CancellationToken ct) =>
        session.SendAsync(tables.ForProtocol(protocol).S2C("gspRepAutoStart"), Encode(_packetTables, build, true), ct);

    internal static byte[] Encode(PacketTables tables, string build, bool enabled) =>
        PacketCodec.Write(tables, build, "gspRepAutoStart", new Dictionary<string, object?> { ["enabled"] = enabled });

    private static PacketTables RequireLayout(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "gspRepAutoStart", "Room auto start", ("enabled", "bool"));
        return tables;
    }
}
