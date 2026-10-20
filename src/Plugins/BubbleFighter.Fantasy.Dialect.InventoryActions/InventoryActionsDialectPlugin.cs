using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.InventoryActions;

/// <summary>Adapts the native expired-item action through shared request and reply packet tables.</summary>
/// <remarks>
/// Requests carry item_id:u32; replies carry item_id:u32 followed by result:u32.
/// Requests: kr-812 sub_9857B0, kr-1256 sub_8136F0, cn-774 sub_9B4BC0, tw-454 sub_C12C40.
/// Replies: kr-812 sub_6DB1B0, kr-1256 sub_9B6250, cn-774 sub_BB4930, tw-454 sub_A92020.
/// </remarks>
[Plugin("dialect.inventory-actions", Description = "Table-driven expired-item request and identity/result reply.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IInboundMessageBinding<InventoryThrowRequested>), typeof(IMessageSender<InventoryThrowReplied>)])]
public sealed class InventoryActionsDialectPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<InventoryActionsDialectOptions>();
        if (string.IsNullOrWhiteSpace(options.Protocol))
            throw new ConfigException("dialect.inventory-actions requires a protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        var build = tables.NearestBuild(context.Row.LineageOrBuild())
            ?? throw new ConfigException("dialect.inventory-actions requires a build named in the packet tables.");
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        var request = opcodes.C2S("gcpReqInventoryThrowItem");
        var reply = opcodes.S2C("gspRepInventoryThrowItem");
        // The request supplies item_id and the reply echoes it with the result; both are cast or sized as u32.
        PacketLayoutContract.RequireFields(tables, build, "InventoryActionRequest", "Inventory throw",
            ("item_id", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "InventoryThrowReply", "Inventory throw",
            ("item_id", "u32"), ("result", "u32"));
        var binding = new Binding(options.Protocol, tables, build, reply);
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<InventoryThrowRequested>>(binding));
        context.Effect(context.Services.RegisterMany<IMessageSender<InventoryThrowReplied>>(binding));
        context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, request, async (session, packet, ct) =>
        {
            var fields = PacketCodec.ReadScalars(tables, build, "InventoryActionRequest", packet.Payload.Span);
            var message = new InventoryThrowRequested((int)fields["item_id"]);
            if (!await messages.DispatchAsync(options.Protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Inventory throw requests have no typed handler.");
        }));
        return ValueTask.CompletedTask;
    }

    private sealed class Binding(string protocol, PacketTables tables, string build, ushort reply)
        : IInboundMessageBinding<InventoryThrowRequested>, IMessageSender<InventoryThrowReplied>
    {
        public string Protocol => protocol;

        public ValueTask SendAsync(ISession session, InventoryThrowReplied message, CancellationToken ct) =>
            session.SendAsync(reply, PacketCodec.Write(tables, build, "InventoryThrowReply",
                new Dictionary<string, object?> { ["item_id"] = message.ItemId, ["result"] = message.Result }), ct);
    }
}

/// <summary>Selects a game protocol and the audited layout directory for inventory actions.</summary>
public sealed class InventoryActionsDialectOptions
{
    /// <summary>Gets or sets the game protocol whose table supplies the request and reply opcode names.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the directory containing builds.yaml and the inventory action structures.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
