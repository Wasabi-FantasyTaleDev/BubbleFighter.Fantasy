using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Sends a server notice through the packet the build's dispatcher names for it: <c>gspReceiveServerNoticeMsg</c>
/// (kr-812 sub_837170 case 107) or <c>gspServerChatNoticeMsg</c> (kr-1256 sub_9D2FF0 case 114). The body is the
/// build's <c>ServerNotice</c> structure.
/// </summary>
internal sealed class ServerNoticeBinding : IMessageSender<ServerNotice>
{
    private static readonly string[] PacketNames = ["gspReceiveServerNoticeMsg", "gspServerChatNoticeMsg"];

    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly ushort _opcode;

    /// <summary>Creates the sender and checks the layout field it writes when the row activates.</summary>
    /// <param name="protocol">The game-connection protocol name.</param>
    /// <param name="opcodes">The build's opcode table.</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose table is written.</param>
    internal ServerNoticeBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    {
        Protocol = protocol;
        _tables = tables;
        _build = build;
        // The notice body carries its text as one prefixed wide string; the binding sizes that field.
        PacketLayoutContract.RequireFields(tables, build, "ServerNotice", "Server notice", ("text", "wstr"));
        _opcode = PacketNames.Select(name => opcodes.Find(PacketDirection.ServerToClient, name))
            .FirstOrDefault(found => found is not null)
            ?? throw new ConfigException($"'{protocol}' names none of the server notice packets ({string.Join(", ", PacketNames)}).");
    }

    public string Protocol { get; }

    public ValueTask SendAsync(ISession session, ServerNotice message, CancellationToken ct)
    {
        // cn-324 sub_78A570 formats the text into a 512-wide-character buffer.
        if (message.Text.Length > 511)
            throw new ArgumentException("Server notice exceeds the native text buffer.", nameof(message));
        return session.SendAsync(_opcode, PacketCodec.Write(_tables, _build, "ServerNotice",
            new Dictionary<string, object?> { ["text"] = message.Text }), ct);
    }
}
