using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Publishes authoritative user statistics without requiring an unrelated inventory-use request binding.</summary>
internal sealed class UserStatSender(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IMessageSender<UserStatUpdated>
{
    public string Protocol => protocol;

    public ValueTask SendAsync(ISession session, UserStatUpdated message, CancellationToken ct)
    {
        if (message.Player.UserNum != message.Wallet.UserNum)
            throw new InvalidOperationException("Local user state contains another player's wallet.");
        return session.SendAsync(opcodes.S2C("gspUserStat"), UserStatWire.Write(tables, build,
            message.Player.Nickname, message.Player.Level, message.Wallet.Lucci, message.Wallet.Cash,
            message.Player.ColorIndex, message.Player.UserNum, CoupleProfileWire.Current(session)), ct);
    }
}
