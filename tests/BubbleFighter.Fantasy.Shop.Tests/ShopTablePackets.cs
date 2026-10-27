using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>The shop packets the tests resolve by name in the kr-1256 opcode table the tests copy.</summary>
internal static class ShopTablePackets
{
    internal static readonly IOpcodeTable Table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"),
        new ClientProfile("kr-1256", "kr", 1256, 1304, 0, "kr-1256")).ForConnection(OpcodeConnection.Game);

    /// <summary>The S2C 816 purchase reply opcode the recording senders use.</summary>
    internal static ushort BuyReply => Table.S2C("gspRepShopBuy");

    /// <summary>The S2C 106 money snapshot opcode the recording senders use.</summary>
    internal static ushort UserMoneyStat => Table.S2C("gspUserMoneyStat");

    /// <summary>The S2C 803 slots update opcode the flush sender uses.</summary>
    internal static ushort InventorySlotsUpdate => Table.S2C("gspInventoryUpdateInventorySlots");
}
