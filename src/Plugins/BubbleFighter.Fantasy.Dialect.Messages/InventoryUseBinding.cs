using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Adapts item ID, quantity and parameters on requests, and result, item ID and parameters on replies.
/// The parameters field is the counted UTF-16 string list where the build's table states one
/// (kr-1256 sub_80DA80 writes it, kr-1256 sub_9B4100 reads it) and one string on the legacy builds and
/// tw-454 (kr-812 sub_8B9490/sub_8B9660; kr-812 sub_825070 interprets the reply).
/// </summary>
/// <remarks>
/// A list with more than one string is answered with the native failure result and never reaches the item
/// handler; a spray sends the list empty (kr-1256 sub_D407C0 case 1).
/// </remarks>
internal sealed class InventoryUseBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<InventoryItemUseRequested>, IMessageSender<InventoryItemUseReplied>, IMessageSender<UserStatUpdated>
{
    // kr-812 sub_825070 treats any result other than zero as a failure dialog.
    private const int Refused = 1;

    private readonly bool _listParameters = tables.Resolve(build, "InventoryUseRequest")
        .Single(field => field.Name == "parameters").Type.StartsWith("list<", StringComparison.Ordinal);

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        // The request fields this binding casts, and the reply fields it sizes. The parameters field's shape is a
        // build switch handled by the reader below, so it is not pinned to one type here.
        PacketLayoutContract.RequireFields(tables, build, "InventoryUseRequest", "Inventory item use",
            ("item_id", "u32"), ("quantity", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "InventoryUseReply", "Inventory item use",
            ("result", "u32"), ("item_id", "u32"));
        _ = tables.Resolve(build, "UserStat");
        var requestOpcode = opcodes.C2S("gcpReqInventoryUseItem");
        _ = opcodes.S2C("gspRepInventoryUseItem");
        _ = opcodes.S2C("gspUserStat");
        var packets = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InventoryItemUseRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<InventoryItemUseReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<UserStatUpdated>>(this));
        context.Effect(packets.Register(protocol,
            requestOpcode, async (session, packet, ct) =>
            {
                var (itemId, quantity, parameters) = ReadRequest(packet.Payload.Span);
                if (parameters is null)
                {
                    context.Logger.LogInformation("Session {Id} item use {Item} carries more than one parameter; refused unread.",
                        session.Id, itemId);
                    await SendAsync(session, new InventoryItemUseReplied(Refused, "") { ItemId = itemId }, ct).ConfigureAwait(false);
                    return;
                }
                var request = new InventoryItemUseRequested(itemId, quantity, parameters);
                if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Inventory item use has no handler.");
            }));
    }

    // A null parameter string means the request listed more than one and must not reach the item handler.
    private (int ItemId, int Quantity, string? Parameters) ReadRequest(ReadOnlySpan<byte> payload)
    {
        if (!_listParameters)
        {
            var scalars = PacketCodec.ReadScalars(tables, build, "InventoryUseRequest", payload);
            return ((int)scalars["item_id"], (int)scalars["quantity"], (string)scalars["parameters"]);
        }
        var fields = PacketCodec.Read(tables, build, "InventoryUseRequest", payload);
        var itemId = Convert.ToInt32(fields["item_id"], CultureInfo.InvariantCulture);
        var quantity = Convert.ToInt32(fields["quantity"], CultureInfo.InvariantCulture);
        var list = (object?[])fields["parameters"]!;
        return list.Length switch
        {
            0 => (itemId, quantity, ""),
            1 => (itemId, quantity, (string)list[0]!),
            _ => (itemId, quantity, null),
        };
    }

    public ValueTask SendAsync(ISession session, InventoryItemUseReplied message, CancellationToken ct)
    {
        object parameters = _listParameters
            ? (message.Parameters.Length == 0 ? Array.Empty<string>() : [message.Parameters])
            : message.Parameters;
        return session.SendAsync(opcodes.S2C("gspRepInventoryUseItem"), PacketCodec.Write(tables, build, "InventoryUseReply",
            new Dictionary<string, object?> { ["result"] = message.Result, ["item_id"] = message.ItemId, ["parameters"] = parameters }), ct);
    }

    public ValueTask SendAsync(ISession session, UserStatUpdated message, CancellationToken ct)
    {
        if (message.Player.UserNum != message.Wallet.UserNum)
            throw new InvalidOperationException("Local user state contains another player's wallet.");
        return session.SendAsync(opcodes.S2C("gspUserStat"), UserStatWire.Write(tables, build,
            message.Player.Nickname, message.Player.Level, message.Wallet.Lucci, message.Wallet.Cash,
            message.Player.ColorIndex, message.Player.UserNum), ct);
    }
}
