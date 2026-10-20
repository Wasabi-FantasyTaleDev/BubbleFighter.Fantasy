using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Dialects;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>The catalog-schema shop's packet numbers, looked up by name in the kr-1256 opcode table the tests copy.</summary>
internal static class ShopTablePackets
{
    internal static readonly IOpcodeTable Table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"),
        new ClientProfile("kr-1256", "kr", 1256, 1304, 0, "kr-1256")).ForConnection(OpcodeConnection.Game);

    internal static readonly ShopPacketNumbers Numbers = ShopPacketNumbers.From(Table);
}
