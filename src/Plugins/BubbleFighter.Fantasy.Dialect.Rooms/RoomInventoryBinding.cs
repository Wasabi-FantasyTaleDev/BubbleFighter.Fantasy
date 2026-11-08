using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Maps the early native room selection envelope to shared waiting-room messages.</summary>
internal sealed class RoomInventoryBinding(string protocol, IOpcodeTable table, PacketTables packets,
    string build, ILogger logger) : IInboundMessageBinding<RoomInventoryChangeRequested>, IMessageSender<RoomInventoryChanged>
{
    private readonly int _count = SelectionCount(packets, build);
    private RoomInventoryCatalog? _catalog;
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        var fields = RoomInventoryProjection.Fields(packets, build);
        if (fields.Weapons.Length != _count)
            throw new ConfigException("Room inventory selector count disagrees with the native slot aliases.");
        _ = table.C2S("gcpRoomChangeInventory");
        _ = table.S2C("gspRoomChangeInventory");
        PacketLayoutContract.RequireFields(packets, build, "RoomInventoryChangeRequest", "Room inventory selection",
            ("character_selection", "u32"), ("unknown_8", "u32"), ("weapon_selections", "list<u32>"));
        PacketLayoutContract.RequireFields(packets, build, "RoomInventoryChanged", "Room inventory selection",
            ("unknown_4", "u32"), ("unknown_8", "u32"), ("unknown_12", "u32"), ("unknown_16", "list<u32>"));
        _catalog = RoomInventoryCatalog.Load(context.Services.Get<IClientDataStore>());
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomInventoryChangeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoomInventoryChanged>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, "gcpRoomChangeInventory", Read);
    }

    internal RoomInventoryChangeRequested Read(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(packets, build, "RoomInventoryChangeRequest", body,
            new PacketReadLimits(MaxCollectionElements: _count));
        var words = ((object?[])fields["weapon_selections"]!).Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToArray();
        var character = (int)fields["character_selection"]!;
        if (words.Length != _count || character is < 0 or > short.MaxValue
            || words.Any(word => word is < -1 or > short.MaxValue))
            throw new PacketFormatException("Room inventory selection has an invalid native selector count or width.", 0);
        var selection = new RoomInventorySelection(character, Array.AsReadOnly(words));
        try { (_catalog ?? throw new InvalidOperationException("Room inventory binding has not been mounted.")).Validate(selection); }
        catch (ArgumentException ex) { throw new PacketFormatException(ex.Message, 0); }
        return new(selection);
    }

    public ValueTask SendAsync(ISession session, RoomInventoryChanged message, CancellationToken ct)
    {
        if (message.Slot is < 0 or >= 16 || message.Selection.WeaponSelections.Count != _count)
            throw new ArgumentException("Room inventory notification has an invalid seat or selector count.", nameof(message));
        // cn-279 sub_708720 and cn-324 sub_6EDF70 use word 1 as the seat and word 2 as
        // the character, then copy the vector into the seat's short selectors; word 3 is unused.
        return session.SendAsync(table.S2C("gspRoomChangeInventory"), PacketCodec.Write(packets, build,
            "RoomInventoryChanged", new Dictionary<string, object?>
            {
                ["unknown_4"] = message.Slot, ["unknown_8"] = message.Selection.CharacterSelection,
                ["unknown_12"] = 0, ["unknown_16"] = message.Selection.WeaponSelections,
            }), ct);
    }

    private static int SelectionCount(PacketTables packets, string build) =>
        packets.ValuesOf(build).TryGetValue("room_inventory_selection_count", out var raw)
        && int.TryParse(raw as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count is 4 or 5
            ? count : throw new ConfigException("Room inventory selection requires an audited four- or five-selector build value.");
}
