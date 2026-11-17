using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins kr-812's plaza booths against its own client: the four request writers (kr-812 sub_479090, sub_46CA40,
/// sub_4795A0, sub_479660), the reply readers (kr-812 sub_46D4C0, sub_46D5B0, sub_40DC10, sub_46D730), the day record
/// reader (kr-812 sub_83A780) and the plain slot-3 configuration its decoder reads (kr-812 sub_464240).
/// </summary>
public sealed class PlazaGamesKr812Tests
{
    private const string Build = "kr-812";

    /// <summary>Every request is exactly as long as its writer: the fishing start has no word after the cast terms.</summary>
    [Theory]
    [InlineData("LmgBasketballRequest", 12, 1)]
    [InlineData("LmgFishingStartRequest", 10, 10)]
    [InlineData("LmgRodLiftRequest", 18, 11)]
    [InlineData("LmgFortuneRequest", 16, 12)]
    [InlineData("LmgSyncAction", 6, 100)]
    public void RequestLengthsAreExact(string structure, int length, int subcmd)
    {
        var body = new byte[length];
        body[0] = (byte)subcmd;
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, body);
        Assert.Equal(subcmd, Convert.ToInt32(values["subcmd"]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, new byte[length + 1]));
    }

    /// <summary>The requests are read by their subcommand with kr-812's own word order.</summary>
    [Fact]
    public void RequestsAreReadInKr812sOrder()
    {
        var prices = new PlazaGamePrices(50, 400, 600, 200);
        PlazaGameRequested Read(string hex) => PlazaGamesBinding.ReadRequest(TestBuilds.Packets, Build, prices, Convert.FromHexString(hex));
        Assert.Equal(new PlazaGameRequested(PlazaGame.Basketball, 50) { Hit = true, PublishedCost = 50 },
            Read("0100" + "3200" + "01000000" + "00000000"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.FishingStart, 400) { PublishedCost = 400 },
            Read("0A00" + "00000000" + "9001" + "0300"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.FishingStart, 400) { PublishedCost = 600 },
            Read("0A00" + "01000000" + "9001" + "0300"));
        // The hooked word follows the displayed cost and the day's count, then a zero word.
        Assert.Equal(new PlazaGameRequested(PlazaGame.RodLift, 400) { Hooked = true },
            Read("0B00" + "01000000" + "9001" + "0300" + "01000000" + "00000000"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.RodLift, 400),
            Read("0B00" + "01000000" + "9001" + "0300" + "00000000" + "00000000"));
        Assert.Equal(new PlazaGameRequested(PlazaGame.Fortune, 200) { Paper = 0x0102030405060708UL, PublishedCost = 200 },
            Read("0C00" + "C800" + "0807060504030201" + "00000000"));
        Assert.Throws<PacketFormatException>(() => Read("0A00" + "00000000" + "9001" + "0300" + "00000000"));
        Assert.Throws<PacketFormatException>(() => Read("0200" + "3200"));
    }

    /// <summary>The animation step and its notice keep kr-812's keys: a u16 key then one word, after the acting gamer.</summary>
    [Fact]
    public void AnimationStepsUseKr812sKeys()
    {
        Assert.Equal(new PlazaGameSyncRequested(101, 203), PlazaGamesBinding.ReadSync(TestBuilds.Packets, Build, Convert.FromHexString("6500" + "CB000000")));
        Assert.Throws<PacketFormatException>(() => PlazaGamesBinding.ReadSync(TestBuilds.Packets, Build, Convert.FromHexString("6600" + "07000000")));
        var notice = PacketCodec.Write(TestBuilds.Packets, Build, "LmgSyncNotice",
            new Dictionary<string, object?> { ["gamer"] = 42, ["subcmd"] = 200, ["value"] = 1 });
        Assert.Equal("2A000000" + "C800" + "01000000", Convert.ToHexString(notice));
    }

    /// <summary>
    /// kr-812 starts a cast and counts a catch from 10 (kr-812 sub_46A430, sub_46A630), so every reply branch carries
    /// that code, and the lift reply has no paid-out word.
    /// </summary>
    [Fact]
    public void RepliesUseKr812sResultCodesAndLengths()
    {
        var results = PlazaGamesBinding.ReadResults(TestBuilds.Packets, Build);
        byte[] Write(PlazaGameReplied reply) => PlazaGamesBinding.WriteReply(TestBuilds.Packets, Build, results, reply);

        Assert.Equal("0100" + "0B000000" + "64000000", Convert.ToHexString(Write(new(PlazaGame.Basketball, PlazaGameOutcome.BasketballHit) { Wallet = 100 })));
        Assert.Equal("0100" + "0A000000" + "46000000", Convert.ToHexString(Write(new(PlazaGame.Basketball, PlazaGameOutcome.BasketballMiss) { Wallet = 70 })));
        Assert.Equal(14, Write(new(PlazaGame.Basketball, PlazaGameOutcome.BasketballItem) { Wallet = 1, Items = [5] }).Length);
        Assert.Equal("0100" + "00000000", Convert.ToHexString(Write(new(PlazaGame.Basketball, PlazaGameOutcome.Refused))));
        Assert.Equal("0A00" + "0A000000", Convert.ToHexString(Write(new(PlazaGame.FishingStart, PlazaGameOutcome.FishingStarted))));
        Assert.Equal("0A00" + "00000000", Convert.ToHexString(Write(new(PlazaGame.FishingStart, PlazaGameOutcome.Refused))));
        // result, the wallet and the day's count, with no paid word in between.
        Assert.Equal("0B00" + "0A000000" + "84030000" + "0300",
            Convert.ToHexString(Write(new(PlazaGame.RodLift, PlazaGameOutcome.Catch) { Paid = 200, Wallet = 900, FishingCount = 3 })));
        Assert.Equal(16, Write(new(PlazaGame.RodLift, PlazaGameOutcome.CatchOneItem) { Items = [5] }).Length);
        Assert.Equal(20, Write(new(PlazaGame.RodLift, PlazaGameOutcome.CatchTwoItems) { Items = [5, 6] }).Length);
        Assert.Equal("0C00" + "0B000000" + "0807060504030201",
            Convert.ToHexString(Write(new(PlazaGame.Fortune, PlazaGameOutcome.FortunePaper) { Paper = 0x0102030405060708UL })));
        // The fortune reader reads the paper even after a refusal (kr-812 sub_46D730).
        Assert.Equal(14, Write(new(PlazaGame.Fortune, PlazaGameOutcome.Refused)).Length);
        Assert.Equal(22, Write(new(PlazaGame.Fortune, PlazaGameOutcome.FortuneItem) { Items = [5] }).Length);
    }

    /// <summary>The day record is the u16 catch count and the u64 paper (kr-812 sub_83A780): ten bytes.</summary>
    [Fact]
    public void DayRecordIsTenBytes() =>
        Assert.Equal("0300" + "0807060504030201",
            Convert.ToHexString(PlazaGamesBinding.WriteRecord(TestBuilds.Packets, Build, new(3, 0x0102030405060708UL))));

    /// <summary>The booth prices come from the 38-byte slot-3 category the client's decoder reads.</summary>
    [Fact]
    public void PricesAreTheOnesTheCategoryPublishes()
    {
        Assert.Equal(new PlazaGamePrices(50, 0, 0, 200), PlazaGamesBinding.ReadPrices(TestBuilds.Packets, Build));
        var plain = PacketCodec.Write(TestBuilds.Packets, Build, "LobbyMinigameContext",
            DynamicConfigContent.PublishedSlot(TestBuilds.Packets, Build, 3, "LobbyMinigameContext")!.ToDictionary(pair => pair.Key, pair => pair.Value));
        // bool32, four u16, bool32, six u16, one i32, bool32 and one u16 (kr-812 sub_464240).
        Assert.Equal(38, plain.Length);
        Assert.Equal(190083, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(28)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(32)));
    }

    /// <summary>
    /// kr-812 reads the category's plain bytes after a u16 mask (kr-812 sub_45B5E0): the reply is the mask and the 38
    /// bytes, stamped with the Adler-32 of those plain bytes, and an up-to-date request is not answered.
    /// </summary>
    [Fact]
    public void TheCategoryIsServedPlainAndStampedOverItsPlainBytes()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, Build);
        Assert.Equal(new[] { 3 }, wire.ServedBits);
        var request = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 1 << 3);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), wire.StampOf(3) ^ 1);
        var reply = Assert.IsType<byte[]>(wire.Reply(request));
        Assert.Equal(2 + 38, reply.Length);
        Assert.Equal(1 << 3, BinaryPrimitives.ReadUInt16LittleEndian(reply));
        Assert.Equal(wire.StampOf(3), DynamicConfigWire.Checksum(reply.AsSpan(2)));
        Assert.Equal(new PlazaGamePrices(50, 0, 0, 200), PlazaGamesBinding.ReadPrices(TestBuilds.Packets, Build));
        var category = PacketCodec.Read(TestBuilds.Packets, Build, "LobbyMinigameContext", reply[2..]);
        Assert.Equal((ushort)200, category["fortune_cost"]);

        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), wire.StampOf(3));
        Assert.Null(wire.Reply(request));
        // A category the client holds that the server does not serve (kr-812 registers category 2 too) is left alone.
        var withUnserved = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(withUnserved, (1 << 2) | (1 << 3));
        BinaryPrimitives.WriteUInt32LittleEndian(withUnserved.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(withUnserved.AsSpan(6), wire.StampOf(3));
        Assert.Null(wire.Reply(withUnserved));
    }

    /// <summary>The dynamic-config request is the u16 mask and one word per set bit, and kr-812 needs no answer to a current one.</summary>
    [Fact]
    public void TheRequestAndTheUnchangedAnswerFollowKr812()
    {
        var request = PacketCodec.Write(TestBuilds.Packets, Build, "DynamicConfigRequest",
            new Dictionary<string, object?> { ["mask"] = 0x0C, ["checksum_2"] = 7, ["checksum_3"] = 9 });
        Assert.Equal("0C00" + "07000000" + "09000000", Convert.ToHexString(request));
        Assert.Equal((ushort)1500, TestBuilds.Opcodes(Build).C2S("gcpReqDynamicConfig"));
        Assert.Equal((ushort)1500, TestBuilds.Opcodes(Build).S2C("gspRepDynamicConfig"));
        Assert.False(DynamicConfigWire.AnswersUnchanged(TestBuilds.Packets, Build));
        Assert.True(DynamicConfigWire.AnswersUnchanged(TestBuilds.Packets, "kr-1256"));
        Assert.True(DynamicConfigWire.AnswersUnchanged(TestBuilds.Packets, "cn-774"));
        Assert.True(DynamicConfigWire.AnswersUnchanged(TestBuilds.Packets, "cn-735"));
        Assert.Equal("DynamicConfigBlob", DynamicConfigWire.CarrierOf(TestBuilds.Packets, "kr-1256", 3));
        Assert.Equal("LobbyMinigameContext", DynamicConfigWire.CarrierOf(TestBuilds.Packets, Build, 3));
        Assert.Throws<ConfigException>(() => DynamicConfigWire.CarrierOf(TestBuilds.Packets, Build, 4));
    }

    /// <summary>The builds based on kr-812 have no booth content and keep their own answer to an unchanged request.</summary>
    [Theory]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-735")]
    public void BuildsBasedOnKr812PublishNoBoothCategory(string build)
    {
        Assert.Empty(DynamicConfigContent.Load(TestBuilds.Packets, build));
        Assert.True(DynamicConfigWire.AnswersUnchanged(TestBuilds.Packets, build));
    }
}
