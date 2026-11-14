using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the independently audited racing bodies and opcode spaces across the two native wire generations.</summary>
public sealed class ExtremeRacingCrossBuildTests
{
    /// <summary>Each typed initializer writes both complete maps and includes stone-disposer words only when the native reader has them.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void InitializationPinsGenerationSpecificTail(string build, bool hasStones)
    {
        Assert.Equal(hasStones, ExtremeRacingBinding.RequireInitializationLayouts(TestBuilds.Packets, build));
        var body = ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, build, Initialization(hasStones));
        var expected = "6400000000000000C800000002000000"
            + "02000000C800000000000000C900000000000000"
            + "02000000C800000003000000C900000001000000"
            + (hasStones ? "2C01000001000000" : "");
        Assert.Equal(hasStones ? 64 : 56, body.Length);
        Assert.Equal(expected, Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingInitialization", body);
    }

    /// <summary>The native reader counts the maps independently and preserves high identity bits without native container padding.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void InitializationPinsIndependentMapCounts(string build, bool hasStones)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingInitialization", new Dictionary<string, object?>
        {
            ["first_cannon_identity"] = 0xF0000000u,
            ["unknown_8"] = 0x11223344u,
            ["first_coin_identity"] = 0xF0000100u,
            ["coin_count"] = 2,
            ["coin_variants"] = new object[]
            {
                new Dictionary<string, object?> { ["identity"] = 0xF0000100u, ["variant"] = 7 },
            },
            ["coin_rounds"] = new object[]
            {
                new Dictionary<string, object?> { ["identity"] = 0xF0000100u, ["round"] = 1 },
                new Dictionary<string, object?> { ["identity"] = 0xF0000101u, ["round"] = 3 },
            },
            ["first_stone_disposer_identity"] = 0xF0000200u,
            ["stone_disposer_count"] = 1,
        });
        Assert.Equal(hasStones ? 56 : 48, body.Length);
        Assert.Equal("000000F044332211000100F002000000"
            + "01000000000100F007000000"
            + "02000000000100F001000000010100F003000000"
            + (hasStones ? "000200F001000000" : ""), Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingInitialization", body);
    }

    /// <summary>An earlier reader cannot silently discard a requested stone count or accept a modern initialization tail.</summary>
    [Theory]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void EarlierInitializationRejectsUnrepresentableStoneDisposers(string build)
    {
        Assert.Throws<InvalidDataException>(() =>
            ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, build, Initialization(true)));
        var modernBody = ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, "kr-1256", Initialization(true));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "ExtremeRacingInitialization", modernBody));
    }

    /// <summary>The earlier fixed header fits 4094 complete coins and has no unused stone identity requirement.</summary>
    [Theory]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void EarlierInitializationUsesItsOwnWireBudget(string build)
    {
        var withoutStones = Initialization(false) with { FirstStoneDisposerIdentity = 0 };
        Assert.Equal(56, ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, build, withoutStones).Length);
        var maximum = withoutStones with
        {
            Coins = Enumerable.Range(0, 4094).Select(index => new RoundExtremeRacingCoin((uint)index + 200, 0, 1)).ToArray(),
        };
        Assert.Equal(65528, ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, build, maximum).Length);
        var excessive = maximum with
        {
            Coins = Enumerable.Range(0, 4095).Select(index => new RoundExtremeRacingCoin((uint)index + 200, 0, 1)).ToArray(),
        };
        Assert.Throws<InvalidDataException>(() => ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, build, excessive));
        Assert.Throws<InvalidDataException>(() =>
            ExtremeRacingBinding.WriteInitialization(TestBuilds.Packets, "kr-1256", maximum with { FirstStoneDisposerIdentity = 300 }));
    }

    /// <summary>Only modern map-state readers consume the third word and independently counted disappearance selectors.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void MapStatePinsItsOptionalVector(string build, bool hasVector)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingMapState", new Dictionary<string, object?>
        {
            ["unknown_0"] = 7,
            ["unknown_4"] = 9,
            ["unknown_8"] = 0x11223344u,
            ["vanish_selections"] = new object[] { 3u, 0u, 1u },
        });
        Assert.Equal(hasVector ? 28 : 8, body.Length);
        Assert.Equal("0700000009000000"
            + (hasVector ? "4433221103000000030000000000000001000000" : ""), Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingMapState", body);
    }

    /// <summary>Cannon commands preserve full-width identities and weapon words, while only modern clients receive the float scale.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void CannonCommandPinsIdentityWeaponAndScaleWidths(string build, bool hasScale)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingCannon", new Dictionary<string, object?>
        {
            ["group"] = 9,
            ["identity"] = 0xF0000001u,
            ["weapon"] = 50008,
            ["scale"] = 1.5f,
        });
        Assert.Equal(hasScale ? 16 : 12, body.Length);
        Assert.Equal("09000000010000F058C30000" + (hasScale ? "0000C03F" : ""), Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingCannon", body);
    }

    /// <summary>The peer shot uses a packed u16 weapon between full-width actor and cannon identities, totaling exactly 38 bytes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void CannonRelayPinsPackedWeaponAndFloatingPointVectors(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingCannonShot", new Dictionary<string, object?>
        {
            ["actor"] = 0xF1020304u,
            ["weapon"] = 50008,
            ["origin_x"] = 1f,
            ["origin_y"] = -2f,
            ["origin_z"] = 0.5f,
            ["direction_x"] = 0f,
            ["direction_y"] = 1f,
            ["direction_z"] = -1f,
            ["cannon_identity"] = 0xF1020345u,
            ["serial"] = 0xAABBCCDDu,
        });
        Assert.Equal(38, body.Length);
        Assert.Equal("040302F158C3"
            + "0000803F000000C00000003F"
            + "000000000000803F000080BF"
            + "450302F1DDCCBBAA", Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingCannonShot", body);
    }

    /// <summary>Record pairs are five wire bytes despite an eight-byte native memory stride, and seat keys remain u16.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void RecordPairsHaveNoNativeMemoryPadding(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingRecords", new Dictionary<string, object?>
        {
            ["scope"] = 1,
            ["records"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["seat"] = 0x1234,
                    ["values"] = new object[]
                    {
                        new Dictionary<string, object?> { ["field"] = 2, ["value"] = 0x89ABCDEFu },
                        new Dictionary<string, object?> { ["field"] = 6, ["value"] = 0x12345678u },
                    },
                },
            },
        });
        Assert.Equal(24, body.Length);
        Assert.Equal("010000000100000034120200000002EFCDAB890678563412", Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingRecords", body);
    }

    /// <summary>The typed round-over clock selects its build's native code while retaining the bool32 layout.</summary>
    [Theory]
    [InlineData("kr-1256", 2500, "C4090000")]
    [InlineData("cn-774", 2500, "C4090000")]
    [InlineData("tw-454", 2500, "C4090000")]
    [InlineData("cn-738", 5000, "88130000")]
    [InlineData("cn-742", 5000, "88130000")]
    public void TimerPinsCodeAndNativeBooleanWord(string build, int code, string codeHex)
    {
        var body = ExtremeRacingBinding.WriteTimer(TestBuilds.Packets, build, new(RoundExtremeRacingTimer.RoundOver, 1000, true));
        Assert.Equal(12, body.Length);
        Assert.Equal(code, BitConverter.ToInt32(body));
        Assert.Equal(codeHex + "E803000001000000", Convert.ToHexString(body));
        AssertStrictBody(build, "ExtremeRacingTimer", body);
        var noncanonical = Convert.FromHexString(codeHex + "E8030000FFFFFFFF");
        Assert.True(Assert.IsType<bool>(PacketCodec.Read(TestBuilds.Packets, build, "ExtremeRacingTimer", noncanonical)["active"]));
    }

    /// <summary>The round consumes its second word while the total-time HUD notification carries only an unused word.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void RoundAndTotalTimePinTheirUnusedWords(string build)
    {
        var round = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingRound",
            new Dictionary<string, object?> { ["round"] = 3 });
        Assert.Equal(8, round.Length);
        Assert.Equal("0000000003000000", Convert.ToHexString(round));
        AssertStrictBody(build, "ExtremeRacingRound", round);
        var notification = PacketCodec.Write(TestBuilds.Packets, build, "ExtremeRacingTotalTime",
            new Dictionary<string, object?>());
        Assert.Equal(4, notification.Length);
        Assert.Equal("00000000", Convert.ToHexString(notification));
        AssertStrictBody(build, "ExtremeRacingTotalTime", notification);
    }

    /// <summary>Each profile resolves its native strategy numbers separately from the audited relay's numeric space.</summary>
    [Theory]
    [InlineData("kr-1256", 711)]
    [InlineData("cn-774", 711)]
    [InlineData("tw-454", 711)]
    [InlineData("cn-738", 761)]
    [InlineData("cn-742", 761)]
    public void OpcodesPinNativeGameAndRelaySpaces(string build, int firstOpcode)
    {
        var game = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)firstOpcode, game.S2C("gspExtremeRacingTimer_"));
        Assert.Equal((ushort)(firstOpcode + 1), game.S2C("gspExtremeRacingRound_"));
        Assert.Equal((ushort)(firstOpcode + 2), game.S2C("gspExtremeRacingInitGameData_"));
        Assert.Equal((ushort)(firstOpcode + 3), game.S2C("gspExtremeRacingUpdatePlayRecord_"));
        Assert.Equal((ushort)(firstOpcode + 5), game.S2C("gspExtremeRacingMapState_"));
        Assert.Equal((ushort)(firstOpcode + 6), game.S2C("gspExtremeRacingTotalTime_"));
        Assert.Equal((ushort)(firstOpcode + 7), game.S2C("gspExtremeRacingCannon_"));
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
        var relay = OpcodeTableLoader.Load(path, game.Profile).ForConnection(OpcodeConnection.Relay);
        Assert.Equal((ushort)32902, relay.C2S("gppExtremeRacingCannon_"));
        Assert.Equal((ushort)32902, relay.S2C("gppExtremeRacingCannon_"));
    }

    /// <summary>Unaudited earlier builds acquire no racing fields, and earlier racing builds acquire no unaudited goal sender.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-735")]
    public void UnauditedBuildsKeepEmptyLayouts(string build)
    {
        foreach (var structure in new[]
        {
            "ExtremeRacingTimer", "ExtremeRacingRound", "ExtremeRacingInitialization",
            "ExtremeRacingRecords", "ExtremeRacingMapState", "ExtremeRacingTotalTime",
            "ExtremeRacingCannon", "ExtremeRacingCannonShot", "ExtremeRacingGoal",
        })
            Assert.Empty(TestBuilds.Packets.Resolve(build, structure));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-738", "ExtremeRacingGoal"));
        Assert.Empty(TestBuilds.Packets.Resolve("cn-742", "ExtremeRacingGoal"));
    }

    private static RoundExtremeRacingInitialized Initialization(bool hasStones) =>
        new(100, 200, [new(200, 0, 3), new(201, 0, 1)], 300, hasStones ? 1 : 0);

    private static void AssertStrictBody(string build, string structure, byte[] body)
    {
        var values = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, values));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, structure, body.Concat(new byte[] { 0 }).ToArray()));
    }
}
