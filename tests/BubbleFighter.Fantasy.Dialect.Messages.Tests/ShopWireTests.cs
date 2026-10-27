using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class ShopWireTests
{
    /// <summary>Shop mounting rejects invalid wallet fields even when no login adapter is installed.</summary>
    [Theory]
    [InlineData("unknown_16")]
    [InlineData("unknown_20")]
    public async Task ShopWithoutLoginRejectsRetypedBalanceBeforePublishingEffects(string field)
    {
        var edited = PacketTableEdits.WithFieldType("UserMoneyStat", field, "u32", "wstr");
        var tables = PacketTables.Load(edited.FullName);
        var profile = new ClientProfile("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var opcodes = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile);
        using var fixture = new MatchMessagesMountTests.Fixture(opcodes);
        var context = fixture.Context("kr-1256", "kr-1256/game");
        try
        {
            var binding = new ShopCatalogBinding("kr-1256/game", opcodes, tables, "kr-1256", NullLogger.Instance);
            var error = Assert.Throws<ConfigException>(() => binding.MountModern(context, fixture.Messages));
            Assert.Contains("UserMoneyStat." + field, error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<ShopBalanceUpdated>>());
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public void CashBalanceRequestIsExactlyEmpty()
    {
        Assert.Equal(new ShopBalanceRequested(), ShopBinding.ReadBalanceRequest([]));
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadBalanceRequest([0]));
    }

    [Fact]
    public void Kr812OrdinaryPurchaseMatchesCapturedEightByteRequestAndReply()
    {
        // kr-812 0x503440 / 0x827550 establish the fields.
        var request = ShopBinding.ReadPurchase(TestBuilds.Packets, "kr-812", [0xEE, 0x45, 0x0F, 0, 0, 0, 0, 0]);
        Assert.Equal(new ShopPurchaseRequested(1000942, true), request);
        var reply = ShopBinding.WriteReply(TestBuilds.Packets, "kr-812", new(request.Key, true));
        Assert.Equal(8, reply.Length);
        Assert.Equal(new byte[] { 0xEE, 0x45, 0x0F, 0, 1, 0, 0, 0 }, reply);
    }

    [Fact]
    public void Kr812MalformedPurchaseCannotReachTheTransaction()
    {
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, "kr-812", new byte[7]));
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, "kr-812", new byte[9]));
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadPurchase(TestBuilds.Packets, "kr-812", [1, 0, 0, 0, 2, 0, 0, 0]));
    }

    [Fact]
    public void Kr812InitialStatusUsesNicknameLevelAndBalancesWithoutCopyingCaptureIdentityIntoColor()
    {
        var bytes = ShopBinding.WriteStat(PacketTables.Load(TestBuilds.PacketsDirectory()), "kr-812", "Player", 17, 500, 23);
        Assert.Equal(392, bytes.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(500, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(23, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(52 + 12)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(288 + 12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(292 + 12)));
    }
}
