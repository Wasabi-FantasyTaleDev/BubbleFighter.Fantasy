using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Publishes seat allegiance using the build's audited two-word team-change table.</summary>
internal sealed class ZombieConversionCodec : IMessageSender<RoomTeamConverted>
{
    private readonly string protocol;
    private readonly PacketTables packetTables;
    private readonly string build;
    private readonly IOpcodeTableRegistry tables;

    /// <summary>Builds the sender and checks the layout it will write while the row is still binding.</summary>
    /// <param name="protocol">The connection protocol the row publishes on.</param>
    /// <param name="packetTables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    /// <param name="tables">The opcode tables of the row's protocol.</param>
    public ZombieConversionCodec(string protocol, PacketTables packetTables, string build, IOpcodeTableRegistry tables)
    {
        this.protocol = protocol;
        this.packetTables = packetTables;
        this.build = build;
        this.tables = tables;
        // The only body this sender writes is two words; a table that keeps the names but changes the widths must fail
        // at the bind, not on the first conversion.
        PacketLayoutContract.RequireFields(packetTables, build, "ZombieTeamChange", "Zombie conversion",
            ("seat", "u32"), ("team", "u32"));
    }

    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, RoomTeamConverted message, CancellationToken ct) =>
        session.SendAsync(tables.ForProtocol(protocol).S2C("gspRoomChangeTeam"),
            Encode(packetTables, build, message), ct);

    /// <summary>Writes u32 seat and u32 team, read by kr-812 sub_6DB1B0.</summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    /// <param name="message">The seat and its new team.</param>
    /// <returns>The S2C body (exactly eight bytes).</returns>
    internal static byte[] Encode(PacketTables tables, string build, RoomTeamConverted message) =>
        PacketCodec.Write(tables, build, "ZombieTeamChange", new Dictionary<string, object?>
        {
            ["seat"] = message.Slot, ["team"] = message.Team,
        });
}
