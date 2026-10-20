using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>The kr-1256 result bodies as the packet tables write them (<c>InventoryEquipmentResult@kr-1256</c>).</summary>
internal static class EquipmentWire
{
    /// <summary>Case 0 of kr-1256 sub_9B34D0 applies the slot.</summary>
    public const int SuccessResult = 0;

    /// <summary>Case 3 of kr-1256 sub_9B34D0, "no item": the shared refusal.</summary>
    public const int FailureResult = 3;

    public const int RepInventoryEquipBytes = 12;
    public const int RepInventoryUnequipBytes = 12;

    internal static readonly PacketTables Tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

    public static byte[] RepInventoryEquip(int result, long key) => Result(result, key);

    public static byte[] RepInventoryUnequip(int result, long key) => Result(result, key);

    private static byte[] Result(int result, long key) => PacketCodec.Write(Tables, "kr-1256", "InventoryEquipmentResult",
        new Dictionary<string, object?> { ["result"] = result, ["item_id"] = key });
}

/// <summary>
/// Drives the equipment domain without a dialect row: reads the request and writes the result through the same
/// kr-1256 tables the shared <c>dialect.messages</c> row uses.
/// </summary>
internal sealed class EquipmentCodec(string protocol, IOpcodeTable opcodes)
    : IInboundMessageBinding<EquipmentChangeRequested>, IMessageSender<EquipmentChangeReplied>
{
    public string Protocol => protocol;

    public static EquipmentChangeRequested Decode(ReadOnlySpan<byte> payload, bool worn)
    {
        var key = Convert.ToInt64(PacketCodec.ReadScalars(EquipmentWire.Tables, "kr-1256", "InventoryEquipmentRequest", payload)["item_id"],
            CultureInfo.InvariantCulture);
        return new EquipmentChangeRequested(key, key is > 0 and < 100000000 ? (int)key : null, worn);
    }

    public ValueTask SendAsync(ISession session, EquipmentChangeReplied message, CancellationToken ct)
    {
        var result = message.Succeeded ? EquipmentWire.SuccessResult : EquipmentWire.FailureResult;
        return session.SendAsync(opcodes.S2C(message.Request.Worn ? "gspRepInventoryEquip" : "gspRepInventoryUnequip"),
            EquipmentWire.RepInventoryEquip(result, message.Request.Reference), ct);
    }
}
