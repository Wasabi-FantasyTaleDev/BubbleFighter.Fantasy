using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the KR1256 Mystery Store readers and the independent successful-purchase suffix.</summary>
public sealed class MysteryStoreTableTests
{
    private const string Build = "kr-1256";
    private const string Reply = "UserMysteryStoreReply";
    private const string PurchaseTail = "UserMysteryStorePurchaseTail";
    private static readonly string[] Structures =
    [
        "UserMysteryStoreRequest", "UserMysteryStoreRequestType2", "UserMysteryStoreRequestType3",
        "UserMysteryStoreRequestType4", "MysteryStorePacketConfig", "MysteryStorePacketUserData",
        "MysteryStorePacketStock", "MysteryStoreStockList", Reply, PurchaseTail,
    ];

    /// <summary>Every combination of the four section bits read by kr-1256 sub_114E4E0.</summary>
    public static TheoryData<int> Masks => new(Enumerable.Range(0, 16));

    /// <summary>The request serializer kr-1256 sub_114E2E0 packs each proven tail after the same token.</summary>
    [Theory]
    [InlineData("UserMysteryStoreRequest", "0144332211", null, 0)]
    [InlineData("UserMysteryStoreRequestType2", "02443322116655", "remaining_seconds", 0x5566)]
    [InlineData("UserMysteryStoreRequestType3", "03443322116655", "refresh_cost", 0x5566)]
    [InlineData("UserMysteryStoreRequestType4", "044433221181841E006655", "coin_price", 0x5566)]
    public void RequestsReadProvenFieldNamesInNativeOrder(string structure, string hex, string? tail, int value)
    {
        var body = Convert.FromHexString(hex);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, body);
        Assert.Equal(0x11223344, fields["token"]);
        if (tail is not null) Assert.Equal((ushort)value, Assert.IsType<ushort>(fields[tail]));
        if (structure == "UserMysteryStoreRequestType4") Assert.Equal(2000001, fields["stock_or_stock_set_id"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, structure, fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, Build, structure, [.. body, 0]));
    }

    /// <summary>The legacy baseline and the builds based on it have no Mysterystore class, so they resolve no layout.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("cn-324")]
    public void BuildsWithoutAMysteryStoreResolveNoLayout(string build)
    {
        foreach (var structure in Structures)
        {
            Assert.NotEmpty(TestBuilds.Packets.Resolve(Build, structure));
            Assert.Empty(TestBuilds.Packets.Resolve(build, structure));
        }
    }

    /// <summary>Independent section bytes pin bool32, UTF-16, row order and every optional-field combination.</summary>
    [Theory]
    [MemberData(nameof(Masks))]
    public void EveryMaskReadsAndWritesExactlyItsSelectedSections(int mask)
    {
        var body = ReplyBytes(mask);
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, Reply, body);
        string[] names = ["config", "user_data", "offers", "purchased_mask"];
        for (var bit = 0; bit < names.Length; bit++)
            Assert.Equal((mask & (1 << bit)) != 0, fields.ContainsKey(names[bit]));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, Reply, ReplyValues(mask)));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, Reply, fields));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, [.. body, 0]));
    }

    /// <summary>kr-1256 sub_114E600 builds purchased state from this response's ordered rows and mask together.</summary>
    [Fact]
    public void FullPublicationCarriesOrderedRowsAlongsidePurchasedBits()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, Reply, ReplyValues(15));
        Assert.Equal(59, body.Length); // 27 fixed bytes, one 18-byte row and one 14-byte row.
        Assert.Equal(ReplyBytes(15), body);
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, Reply, body);
        Assert.Equal(12, Assert.IsType<byte>(fields["fields"]) & 12);
        var offers = Assert.IsType<Dictionary<string, object?>>(fields["offers"]);
        var stocks = Assert.IsType<object?[]>(offers["stocks"]);
        Assert.Equal(2, stocks.Length);
        var first = Assert.IsType<Dictionary<string, object?>>(stocks[0]);
        var second = Assert.IsType<Dictionary<string, object?>>(stocks[1]);
        Assert.Equal(1000001, first["stock_or_stock_set_id"]);
        Assert.Equal(2000001, second["stock_or_stock_set_id"]);
        Assert.Equal("A\uD55C", first["unknown_8"]);
        Assert.Equal("", second["unknown_8"]);
        Assert.Equal(0x01020304, first["unknown_4"]);
        Assert.Equal((ushort)42, first["coin_price"]);
        var purchased = unchecked((uint)Assert.IsType<int>(fields["purchased_mask"]));
        Assert.Equal(0x80000002u, purchased);
        Assert.Equal(0u, purchased & 1u);
        Assert.Equal(2u, purchased & 2u);
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, truncated));
        }
    }

    /// <summary>The native config reader consumes four bytes for its bool and normalizes any nonzero word.</summary>
    [Fact]
    public void ConfigBooleanHasNativeWordWidthAndOptionalCanonicalValidation()
    {
        var body = ReplyBytes(1);
        Assert.Equal(17, body.Length);
        body[13] = 2;
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, Reply, body);
        var config = Assert.IsType<Dictionary<string, object?>>(fields["config"]);
        Assert.True(Assert.IsType<bool>(config["group_stock_variants"]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, body,
            requireCanonicalBooleans: true));
        Assert.Equal(ReplyBytes(1), PacketCodec.Write(TestBuilds.Packets, Build, Reply, fields));
    }

    /// <summary>Negative native status bytes keep their bits instead of becoming unsigned success codes.</summary>
    [Theory]
    [InlineData(-78, 0xB2)]
    [InlineData(-1, 0xFF)]
    [InlineData(-128, 0x80)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void SignedStatusPreservesItsTransportByte(int status, int bits)
    {
        var values = ReplyValues(0);
        values["status"] = status;
        var body = PacketCodec.Write(TestBuilds.Packets, Build, Reply, values);
        Assert.Equal(new byte[] { 2, (byte)bits, 0 }, body);
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, Reply, body);
        Assert.Equal((sbyte)status, unchecked((sbyte)Assert.IsType<byte>(fields["status"])));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, Reply, fields));
    }

    /// <summary>kr-1256 sub_114E4E0 consumes the successful-buy word even when no section bit is set.</summary>
    [Theory]
    [MemberData(nameof(Masks))]
    public void SuccessfulPurchaseTailIsIndependentOfEverySectionMask(int mask)
    {
        var common = ReplyBytes(mask, action: 4);
        var tail = PacketCodec.Write(TestBuilds.Packets, Build, PurchaseTail,
            new Dictionary<string, object?> { ["reward_stock_or_stock_set_id"] = 2000001 });
        Assert.Equal(Convert.FromHexString("81841E00"), tail);
        byte[] body = [.. common, .. tail];
        var reader = new PacketReader(body);
        var fields = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, Build, Reply);
        Assert.Equal(common.Length, reader.Offset);
        Assert.Equal(4, reader.Remaining);
        Assert.Equal((byte)4, fields["action"]);
        var purchase = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, Build, PurchaseTail);
        Assert.Equal(2000001, purchase["reward_stock_or_stock_set_id"]);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(2000001, ReadNativeReply(body));
        Assert.Throws<PacketFormatException>(() => ReadNativeReply(common));
        Assert.Throws<PacketFormatException>(() => ReadNativeReply(body[..^1]));
        Assert.Throws<PacketFormatException>(() => ReadNativeReply([.. body, 0]));
    }

    /// <summary>Refusals and nonpurchase actions have no success word, even when all four sections are present.</summary>
    [Theory]
    [InlineData(4, -78)]
    [InlineData(4, 1)]
    [InlineData(4, -1)]
    [InlineData(3, 0)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    public void OnlyActionFourWithZeroStatusConsumesThePurchaseTail(int action, int status)
    {
        foreach (var mask in new[] { 0, 15 })
        {
            var body = ReplyBytes(mask, action, status);
            Assert.Null(ReadNativeReply(body));
            Assert.Throws<PacketFormatException>(() => ReadNativeReply([.. body, 0x81, 0x84, 0x1E, 0]));
        }
    }

    /// <summary>No unassigned flag can replace the native action-and-status test for the receipt suffix.</summary>
    [Fact]
    public void UnassignedMaskBitsDoNotGateThePurchaseTail()
    {
        Assert.Null(ReadNativeReply([2, 0, 0x10]));
        Assert.Throws<PacketFormatException>(() => ReadNativeReply([4, 0, 0x10]));
        Assert.Equal(2000001, ReadNativeReply([4, 0, 0x10, 0x81, 0x84, 0x1E, 0]));
    }

    /// <summary>Counts and strings remain bounded before allocating offer records or decoding their text.</summary>
    [Fact]
    public void MalformedOfferCountsAndStringLengthsAreRejected()
    {
        var excessiveCount = Convert.FromHexString("020004FFFFFFFF");
        var excessiveString = Convert.FromHexString("0200040100000000000000FFFFFFFF");
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, excessiveCount));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, excessiveString));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, ReplyBytes(15),
            new PacketReadLimits(MaxCollectionElements: 1)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, Reply, ReplyBytes(15),
            new PacketReadLimits(MaxStringCharacters: 1)));
    }

    private static byte[] ReplyBytes(int mask, int action = 2, int status = 0)
    {
        // Independent wire fragments from kr-1256 sub_114E110, kr-1256 sub_114E180,
        // kr-1256 sub_114E220 and kr-1256 sub_114E820.
        string[] chunks =
        [
            "4433221188776655341201000000", "7856",
            "02000000040302010200000041005CD541420F002A00080706050000000081841E002B00",
            "02000080",
        ];
        var bytes = new List<byte> { unchecked((byte)action), unchecked((byte)status), (byte)mask };
        for (var bit = 0; bit < chunks.Length; bit++)
            if ((mask & (1 << bit)) != 0) bytes.AddRange(Convert.FromHexString(chunks[bit]));
        return bytes.ToArray();
    }

    private static Dictionary<string, object?> ReplyValues(int mask)
    {
        var values = new Dictionary<string, object?> { ["action"] = 2, ["status"] = 0, ["fields"] = mask };
        if ((mask & 1) != 0)
            values["config"] = new Dictionary<string, object?>
            {
                ["token"] = 0x11223344, ["coin_item_id"] = 0x55667788,
                ["refresh_cost"] = 0x1234, ["group_stock_variants"] = true,
            };
        if ((mask & 2) != 0)
            values["user_data"] = new Dictionary<string, object?> { ["remaining_seconds"] = 0x5678 };
        if ((mask & 4) != 0)
            values["offers"] = new Dictionary<string, object?>
            {
                ["stocks"] = new Dictionary<string, object?>[]
                {
                    new() { ["unknown_4"] = 0x01020304, ["unknown_8"] = "A\uD55C", ["stock_or_stock_set_id"] = 1000001, ["coin_price"] = 42 },
                    new() { ["unknown_4"] = 0x05060708, ["unknown_8"] = "", ["stock_or_stock_set_id"] = 2000001, ["coin_price"] = 43 },
                },
            };
        if ((mask & 8) != 0) values["purchased_mask"] = 0x80000002u;
        return values;
    }

    private static int? ReadNativeReply(byte[] body)
    {
        var reader = new PacketReader(body);
        var fields = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, Build, Reply);
        var action = unchecked((sbyte)(byte)fields["action"]!);
        var status = unchecked((sbyte)(byte)fields["status"]!);
        int? reward = null;
        // kr-1256 sub_114E4E0 tests both signed bytes after the four masked sections.
        if (action == 4 && status == 0)
        {
            var tail = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, Build, PurchaseTail);
            reward = (int)tail["reward_stock_or_stock_set_id"]!;
        }
        if (reader.Remaining != 0) throw new PacketFormatException("Unexpected Mystery Store reply suffix.", reader.Offset);
        return reward;
    }
}
