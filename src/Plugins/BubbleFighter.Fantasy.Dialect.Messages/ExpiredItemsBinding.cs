using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the client's native expiry notice, S2C <c>gspCheckExpiredTimeResult</c>: one counted list of
/// item ids (packets/inventory.yaml <c>ExpiredItems</c>, kr-812 sub_840820). The client only displays it.
/// </summary>
internal sealed class ExpiredItemsBinding : IMessageSender<ItemsExpired>
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly ushort _opcode;

    /// <summary>Creates the sender and checks the layout field it writes when the row activates.</summary>
    /// <param name="protocol">The game-connection protocol name.</param>
    /// <param name="opcodes">The build's opcode table.</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose table is written.</param>
    internal ExpiredItemsBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    {
        Protocol = protocol;
        _tables = tables;
        _build = build;
        // The notice is one counted list of item ids; the binding writes that field.
        PacketLayoutContract.RequireFields(tables, build, "ExpiredItems", "Expired items", ("item_ids", "list<u32>"));
        // Resolved when the row activates, so a profile whose table lacks the row fails at load, not at the first expiry.
        _opcode = opcodes.S2C("gspCheckExpiredTimeResult");
    }

    public string Protocol { get; }

    public ValueTask SendAsync(ISession session, ItemsExpired message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ItemIds.Count == 0)
            throw new ArgumentException("An expiry notice names at least one item.", nameof(message));
        if (message.ItemIds.Any(id => id <= 0))
            throw new ArgumentException("Expired item ids must be positive.", nameof(message));
        return session.SendAsync(_opcode, Encode(_tables, _build, message.ItemIds), ct);
    }

    /// <summary>Encodes the notice body for one build.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <param name="itemIds">The expired item ids.</param>
    /// <returns>The body: a u32 count and that many u32 ids.</returns>
    internal static byte[] Encode(PacketTables tables, string build, IReadOnlyList<int> itemIds) =>
        PacketCodec.Write(tables, build, "ExpiredItems", new Dictionary<string, object?>
        {
            ["item_ids"] = itemIds.Select(id => (object?)(uint)id).ToArray(),
        });
}
