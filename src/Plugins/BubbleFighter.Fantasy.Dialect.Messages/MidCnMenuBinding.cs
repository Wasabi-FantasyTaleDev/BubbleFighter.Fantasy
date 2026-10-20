using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Sends the mid-CN main-menu transition: <c>gspGotoRoom</c> kind 1 then
/// <c>gspGotoMenu</c>. cn-735 handler <c>sub_A23390</c> installs the session;
/// <c>sub_A23050</c> moves to stage 2 (<c>MainMenuStage</c>). Opcode numbers
/// come from the session profile's table (112 / 110 on these builds).
/// </summary>
internal sealed class MidCnMenuBinding(string protocol, IOpcodeTableRegistry tables,
    PacketTables packetTables, string build) : IMessageSender<MainMenuRoomOpened>
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public async ValueTask SendAsync(ISession session, MainMenuRoomOpened message, CancellationToken ct)
    {
        var table = tables.Get(session.Profile.Id);
        await session.SendAsync(table.S2C("gspGotoRoom"), MidCnMenuCodec.WriteMainMenu(packetTables, build, message), ct).ConfigureAwait(false);
        await session.SendAsync(table.S2C("gspGotoMenu"), MidCnMenuCodec.WriteGotoMenu(packetTables, build), ct).ConfigureAwait(false);
    }
}
