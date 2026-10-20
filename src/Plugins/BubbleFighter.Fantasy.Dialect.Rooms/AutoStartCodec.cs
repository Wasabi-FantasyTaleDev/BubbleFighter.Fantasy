using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes the native room automatic-start notification from the build's packet table.</summary>
internal sealed class AutoStartCodec(string protocol, PacketTables packetTables, string build,
    IOpcodeTableRegistry tables) : IMessageSender<RoomAutoStartNotified>
{
    public string Protocol => protocol;

    public ValueTask SendAsync(ISession session, RoomAutoStartNotified message, CancellationToken ct) =>
        session.SendAsync(tables.ForProtocol(protocol).S2C("gspRepAutoStart"), Encode(packetTables, build, true), ct);

    internal static byte[] Encode(PacketTables tables, string build, bool enabled) =>
        PacketCodec.Write(tables, build, "gspRepAutoStart", new Dictionary<string, object?> { ["enabled"] = enabled });
}
