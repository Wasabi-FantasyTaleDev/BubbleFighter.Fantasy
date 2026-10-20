using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Publishes seat allegiance using the build's audited two-word team-change table.</summary>
internal sealed class ZombieConversionCodec(string protocol, PacketTables packetTables, string build,
    IOpcodeTableRegistry tables) : IMessageSender<RoomTeamConverted>
{
    public string Protocol => protocol;

    public ValueTask SendAsync(ISession session, RoomTeamConverted message, CancellationToken ct) =>
        session.SendAsync(tables.ForProtocol(protocol).S2C("gspRoomChangeTeam"), Encode(packetTables, build, message), ct);

    /// <summary>Writes u32 seat and u32 team, read by kr-812 sub_6DB1B0.</summary>
    internal static byte[] Encode(PacketTables tables, string build, RoomTeamConverted message) =>
        PacketCodec.Write(tables, build, "ZombieTeamChange", new Dictionary<string, object?>
        {
            ["seat"] = message.Slot, ["team"] = message.Team,
        });
}
