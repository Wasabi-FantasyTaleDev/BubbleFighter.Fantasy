using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Shared catalog fixtures for the per-build S2C 800 variant pins.</summary>
internal static class ShopWireTestSupport
{
    internal static readonly ShopCardRow[] Cards =
    [
        new(0, "Test", "lucci", true, false, 120, 120, 0, "m100", "s106", 100001,
            [new ShopStockRow(1000001, 100001, true, 1, 1, 120, 120, 0, 0)], 100, 106, 1),
        new(2000001, "Set", "lucci", true, false, 4500, 4500, 0, "m800", "s800", 100001,
            [new ShopStockRow(2000001, 2000001, true, 1, 0, 4500, 4500, 0, 0)], 800, 800, 2),
    ];

    internal static readonly ShopCategoryRow[] Categories =
    [
        new(100, "cat", 1, [new ShopSubcategoryRow(106, "sub", 1, 1, 0)]),
    ];

    internal static byte[] EnterShop812() => ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, Cards, Categories, 1000);

    internal static byte[] EnterShop400() => ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("id-400"), 0, Cards, Categories);

    internal static byte[] EnterShop1809() => ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("id-1809"), 0, Cards, Categories);

    internal static byte[] EnterShop324() => ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-324"), 0, Cards, Categories);

    internal static byte[] EnterShop279() => ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-279"), 0, Cards, Categories);
}
