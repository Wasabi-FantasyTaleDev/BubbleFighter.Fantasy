using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Shop.Dialects;

/// <summary>
/// The packets the catalog-schema shop row reads and sends, resolved by name from the build's game opcode table
/// when the row binds, so a build with other numbers needs only its <c>opcodes.yaml</c>. A missing name fails the
/// row at activation. The row resolves its request numbers from the same table when it registers them.
/// </summary>
internal sealed record ShopPacketNumbers(
    ushort EnterShopReply,
    ushort EnterCashShopReply,
    ushort ShopBuyReply,
    ushort ShopItemCardInfoReply,
    ushort InventorySlotsUpdate,
    ushort RealCouponUseReply,
    ushort CheckCashReply,
    ushort UserMoneyStat)
{
    /// <summary>Resolves every reply this row sends from <paramref name="table"/> by its client label.</summary>
    internal static ShopPacketNumbers From(IOpcodeTable table) => new(
        table.S2C("gspRepEnterShop"),
        table.S2C("gspRepEnterTCShop"),
        table.S2C("gspRepShopBuy"),
        table.S2C("gspRepShopItemCardInfo"),
        table.S2C("gspInventoryUpdateInventorySlots"),
        table.S2C("gspRepRealCouponUse"),
        table.S2C("gspRepCheckCash"),
        table.S2C("gspUserMoneyStat"));
}
