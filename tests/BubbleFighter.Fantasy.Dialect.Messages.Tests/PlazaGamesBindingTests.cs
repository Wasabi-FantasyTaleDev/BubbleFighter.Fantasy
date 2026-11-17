using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the plaza booth requests, every reply branch and the builds' result codes.</summary>
public sealed class PlazaGamesBindingTests
{
    /// <summary>The modern builds read each booth request by its subcommand (kr-1256 sub_841B40's keys).</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RequestsAreReadByTheirSubcommand(string build)
    {
        var prices = new PlazaGamePrices(50, 400, 600, 200);
        PlazaGameRequested Read(string hex) => PlazaGamesBinding.ReadRequest(TestBuilds.Packets, build, prices, Convert.FromHexString(hex));
        Assert.Equal(new PlazaGameRequested(PlazaGame.Basketball, 50) { Hit = true, PublishedCost = 50 },
            Read("0100" + "3200" + "01000000" + "00000000"));
        // The cast's first flag selects the second published price (kr-1256 sub_842600).
        Assert.Equal(new PlazaGameRequested(PlazaGame.FishingStart, 400) { PublishedCost = 400 },
            Read("0A00" + "00000000" + "00000000" + "9001" + "0300"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.FishingStart, 400) { PublishedCost = 600 },
            Read("0A00" + "01000000" + "00000000" + "9001" + "0300"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.RodLift, 400) { Hooked = true },
            Read("0B00" + "01000000" + "00000000" + "9001" + "0300" + "01000000"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.Fortune, 200) { Paper = 0x0102030405060708UL, PublishedCost = 200 },
            Read("0C00" + "C800" + "0807060504030201" + "00000000"));
        Assert.Throws<PacketFormatException>(() => Read("0200" + "3200"));
        Assert.Throws<PacketFormatException>(() => Read("0100" + "3200"));
    }

    /// <summary>
    /// kr-1256 publishes its booth prices in dynamic-config slot 3 (basketball 50, fishing free, fortune 200); a build
    /// with the booth packets but no published slot 3 cannot price them.
    /// </summary>
    [Fact]
    public void PricesAreTheOnesTheBuildPublishes()
    {
        Assert.Equal(new PlazaGamePrices(50, 0, 0, 200), PlazaGamesBinding.ReadPrices(TestBuilds.Packets, "kr-1256"));
        Assert.Equal(new PlazaGamePrices(50, 0, 0, 200), PlazaGamesBinding.ReadPrices(TestBuilds.Packets, "cn-774"));
        Assert.Equal(new PlazaGamePrices(50, 0, 0, 200), PlazaGamesBinding.ReadPrices(TestBuilds.Packets, "tw-454"));
        Assert.Throws<ConfigException>(() => PlazaGamesBinding.ReadPrices(TestBuilds.Packets, "cn-717"));
    }

    /// <summary>
    /// cn-774 sends category 3 as a counted zlib blob (cn-774 sub_811660) and tw-454 as plain bytes (tw-454 sub_628B00);
    /// both carry the 42 bytes their decoders read (cn-774 sub_9D81D0, tw-454 sub_633520), stamped over the bytes the
    /// client holds, and the prices in them are the ones the server charges.
    /// </summary>
    [Fact]
    public void ModernBuildsPublishTheCategoryInTheirOwnCarrier()
    {
        var prices = new PlazaGamePrices(50, 0, 0, 200);
        var request = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 1 << 3);

        var cn = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), cn.StampOf(3) ^ 1);
        var cnReply = Assert.IsType<byte[]>(cn.Reply(request));
        Assert.Equal(1 << 3, BinaryPrimitives.ReadUInt16LittleEndian(cnReply));
        Assert.Equal(cnReply.Length - 6, BinaryPrimitives.ReadInt32LittleEndian(cnReply.AsSpan(2)));
        Assert.Equal(cn.StampOf(3), DynamicConfigWire.Checksum(cnReply.AsSpan(6)));
        using var input = new MemoryStream(cnReply[6..], writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zlib.CopyTo(plain);
        Assert.Equal(42, plain.Length);
        var cnCategory = PacketCodec.Read(TestBuilds.Packets, "cn-774", "LobbyMinigameContext", plain.ToArray());
        Assert.Equal(prices, new PlazaGamePrices((ushort)cnCategory["basketball_cost"]!, (ushort)cnCategory["fishing_cost"]!,
            (ushort)cnCategory["fishing_second_cost"]!, (ushort)cnCategory["fortune_cost"]!));

        var tw = new DynamicConfigWire(TestBuilds.Packets, "tw-454");
        Assert.Equal(new[] { 3 }, tw.ServedBits);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), tw.StampOf(3) ^ 1);
        var twReply = Assert.IsType<byte[]>(tw.Reply(request));
        Assert.Equal(2 + 42, twReply.Length);
        Assert.Equal(1 << 3, BinaryPrimitives.ReadUInt16LittleEndian(twReply));
        Assert.Equal(tw.StampOf(3), DynamicConfigWire.Checksum(twReply.AsSpan(2)));
        var twCategory = PacketCodec.Read(TestBuilds.Packets, "tw-454", "LobbyMinigameContext", twReply[2..]);
        Assert.Equal(prices, new PlazaGamePrices((ushort)twCategory["basketball_cost"]!, (ushort)twCategory["fishing_cost"]!,
            (ushort)twCategory["fishing_second_cost"]!, (ushort)twCategory["fortune_cost"]!));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), tw.StampOf(3));
        Assert.Null(tw.Reply(request));
    }

    /// <summary>The server charges the four prices present in the category actually sent to the native client.</summary>
    [Fact]
    public void PricesMatchThePublishedWireCategory()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        var request = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(request, 1 << 3);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), wire.StampOf(3) ^ 1);
        var reply = Assert.IsType<byte[]>(wire.Reply(request));
        Assert.Equal(1 << 3, BinaryPrimitives.ReadInt32LittleEndian(reply));
        Assert.Equal(reply.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4)));
        using var input = new MemoryStream(reply[8..], writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var category = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "LobbyMinigameContext", output.ToArray());
        Assert.Equal(new PlazaGamePrices(
            Assert.IsType<ushort>(category["basketball_cost"]),
            Assert.IsType<ushort>(category["fishing_cost"]),
            Assert.IsType<ushort>(category["fishing_second_cost"]),
            Assert.IsType<ushort>(category["fortune_cost"])),
            PlazaGamesBinding.ReadPrices(TestBuilds.Packets, "kr-1256"));
    }

    /// <summary>Only the client's four animation keys are accepted; the notice names the acting player.</summary>
    [Fact]
    public void AnimationStepsUseTheClientsKeys()
    {
        Assert.Equal(new PlazaGameSyncRequested(200, 7), PlazaGamesBinding.ReadSync(TestBuilds.Packets, "kr-1256", Convert.FromHexString("C800" + "07000000")));
        Assert.Throws<PacketFormatException>(() => PlazaGamesBinding.ReadSync(TestBuilds.Packets, "kr-1256", Convert.FromHexString("6600" + "07000000")));
        var notice = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "LmgSyncNotice",
            new Dictionary<string, object?> { ["gamer"] = 42, ["subcmd"] = 100, ["value"] = 1 });
        Assert.Equal("2A000000" + "6400" + "01000000", Convert.ToHexString(notice));
    }

    /// <summary>
    /// Each reply branch has the length its reader consumes and the build's own result code: kr-1256 starts a cast at
    /// 30 and counts a catch from 20 (kr-1256 sub_84A990, sub_84AD60), cn-774 and tw-454 at 20 and 10.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 30u, 20u)]
    [InlineData("cn-774", 20u, 10u)]
    [InlineData("tw-454", 20u, 10u)]
    public void RepliesUseTheBuildsResultCodes(string build, uint started, uint caught)
    {
        var results = PlazaGamesBinding.ReadResults(TestBuilds.Packets, build);
        byte[] Write(PlazaGameReplied reply) => PlazaGamesBinding.WriteReply(TestBuilds.Packets, build, results, reply);

        Assert.Equal("0100" + "0B000000" + "64000000",
            Convert.ToHexString(Write(new(PlazaGame.Basketball, PlazaGameOutcome.BasketballHit) { Wallet = 100 })));
        Assert.Equal(14, Write(new(PlazaGame.Basketball, PlazaGameOutcome.BasketballItem) { Wallet = 1, Items = [5] }).Length);
        Assert.Equal("0100" + "00000000", Convert.ToHexString(Write(new(PlazaGame.Basketball, PlazaGameOutcome.Refused))));
        var start = Write(new(PlazaGame.FishingStart, PlazaGameOutcome.FishingStarted));
        Assert.Equal(6, start.Length);
        Assert.Equal(started, BitConverter.ToUInt32(start, 2));
        var lift = Write(new(PlazaGame.RodLift, PlazaGameOutcome.Catch) { Paid = 200, Wallet = 900, FishingCount = 3 });
        Assert.Equal("0B00" + Convert.ToHexString(BitConverter.GetBytes(caught)) + "C8000000" + "84030000" + "0300", Convert.ToHexString(lift));
        Assert.Equal(20, Write(new(PlazaGame.RodLift, PlazaGameOutcome.CatchOneItem) { Items = [5] }).Length);
        Assert.Equal(24, Write(new(PlazaGame.RodLift, PlazaGameOutcome.CatchTwoItems) { Items = [5, 6] }).Length);
        Assert.Equal("0C00" + "0B000000" + "0807060504030201",
            Convert.ToHexString(Write(new(PlazaGame.Fortune, PlazaGameOutcome.FortunePaper) { Paper = 0x0102030405060708UL })));
        // The fortune reader reads the paper even after a refusal (kr-1256 sub_84D4D0).
        Assert.Equal(14, Write(new(PlazaGame.Fortune, PlazaGameOutcome.Refused)).Length);
        Assert.Equal(22, Write(new(PlazaGame.Fortune, PlazaGameOutcome.FortuneItem) { Items = [5] }).Length);
    }

    /// <summary>A reply the booth's reader cannot show, or with the wrong number of items, is refused before writing.</summary>
    [Fact]
    public void InvalidRepliesAreRefused()
    {
        var results = PlazaGamesBinding.ReadResults(TestBuilds.Packets, "kr-1256");
        Assert.Throws<InvalidDataException>(() => PlazaGamesBinding.WriteReply(TestBuilds.Packets, "kr-1256", results,
            new(PlazaGame.Basketball, PlazaGameOutcome.Catch)));
        Assert.Throws<InvalidDataException>(() => PlazaGamesBinding.WriteReply(TestBuilds.Packets, "kr-1256", results,
            new(PlazaGame.RodLift, PlazaGameOutcome.CatchTwoItems) { Items = [5] }));
    }

    /// <summary>The day record is the u16 catch count and the u64 paper (kr-1256 sub_9D2FF0 case 142).</summary>
    [Fact]
    public void DayRecordIsTenBytes()
    {
        Assert.Equal("0300" + "0807060504030201",
            Convert.ToHexString(PlazaGamesBinding.WriteRecord(TestBuilds.Packets, "kr-1256", new(3, 0x0102030405060708UL))));
    }

    /// <summary>
    /// The booths follow the capability: kr-1256 has it; cn-774 and tw-454 (no published prices) and kr-812 (request
    /// numbers not established) do not.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", true, 551, 552)]
    [InlineData("kr-812", true, 571, 572)]
    [InlineData("cn-774", true, 551, 552)]
    [InlineData("tw-454", true, 551, 552)]
    [InlineData("cn-717", false, 0, 0)]
    [InlineData("id-400", false, 0, 0)]
    [InlineData("cn-324", false, 0, 0)]
    public void CapabilityMarksTheBuildsWithBooths(string build, bool booths, int request, int sync)
    {
        Assert.Equal(booths, TestBuilds.Packets.CapabilityOf(build, "plaza_games"));
        if (booths)
        {
            Assert.Equal((ushort)request, TestBuilds.Opcodes(build).C2S("gcpLmgRequest_"));
            Assert.Equal((ushort)request, TestBuilds.Opcodes(build).S2C("gspLmgReply_"));
            Assert.Equal((ushort)sync, TestBuilds.Opcodes(build).C2S("gcpLmgSync_"));
            Assert.Equal((ushort)sync, TestBuilds.Opcodes(build).S2C("gspLmgSyncState_"));
        }
        else
            Assert.Throws<ConfigException>(() =>
            {
                PlazaGamesBinding.ReadResults(TestBuilds.Packets, build);
                PlazaGamesBinding.ReadPrices(TestBuilds.Packets, build);
            });
    }
}
