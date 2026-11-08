using BubbleFighter.Fantasy.Packets;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native rank-resource limits and the early clients' overflowing actor and party projections.</summary>
public sealed class NativeDisplayLevelTests
{
    /// <summary>Every build states its own resource limit, including builds that currently inherit the same count.</summary>
    [Theory]
    [InlineData("kr-812", 200)]
    [InlineData("kr-1256", 260)]
    [InlineData("cn-279", 180)]
    [InlineData("cn-324", 180)]
    [InlineData("cn-406", 180)]
    [InlineData("cn-404", 180)]
    [InlineData("cn-408", 200)]
    [InlineData("cn-705", 200)]
    [InlineData("cn-706", 200)]
    [InlineData("cn-717", 200)]
    [InlineData("cn-735", 200)]
    [InlineData("cn-738", 200)]
    [InlineData("cn-742", 200)]
    [InlineData("cn-774", 240)]
    [InlineData("tw-454", 220)]
    [InlineData("id-400", 180)]
    [InlineData("id-1809", 180)]
    public void NativeResourceBoundsAreExplicit(string build, int maximum)
    {
        Assert.Equal(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TestBuilds.Packets.ValuesOf(build)["display_level_max"]);
        Assert.Equal(1, NativeDisplayLevel.Project(TestBuilds.Packets, build, 0));
        Assert.Equal(maximum - 1, NativeDisplayLevel.Project(TestBuilds.Packets, build, maximum - 1));
        Assert.Equal(maximum, NativeDisplayLevel.Project(TestBuilds.Packets, build, maximum));
        Assert.Equal(maximum, NativeDisplayLevel.Project(TestBuilds.Packets, build, int.MaxValue));
    }

    /// <summary>A saved level194 produces180 in both early-CN rank records while later builds retain194.</summary>
    [Theory]
    [InlineData("cn-279", 180)]
    [InlineData("cn-324", 180)]
    [InlineData("cn-406", 180)]
    [InlineData("cn-717", 194)]
    [InlineData("kr-812", 194)]
    public void PlazaAndPartyUseTheSameBoundWithoutMutatingProgress(string build, int expected)
    {
        var member = new PartyMember(7, "Ab", 194);
        var body = PartyBinding.WriteParty(TestBuilds.Packets, build, new PartySnapshot([member]));
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(28)));
        var schema = TestBuilds.Of(build).Schema;
        var plaza = new PlazaMember(0, 7, "Ab", member.Level, 300001, [300001]);
        var expectedPlaza = PlayRoomCodec.WritePlazaSeatSlot(TestBuilds.Packets, build, schema, plaza with { Level = expected });
        Assert.Equal(expectedPlaza, PlayRoomCodec.WritePlazaSeatSlot(TestBuilds.Packets, build, schema, plaza));
        Assert.Equal(194, member.Level);
        Assert.Equal(194, plaza.Level);
    }

    /// <summary>The CN2011 actor, party and profile consume the same level independently of the equipped character.</summary>
    [Theory]
    [InlineData(1, 1, 300003)]
    [InlineData(65, 65, 300003)]
    [InlineData(67, 67, 300001)]
    [InlineData(67, 67, 300003)]
    [InlineData(180, 180, 300010)]
    [InlineData(194, 180, 300001)]
    [InlineData(194, 180, 300010)]
    public void Cn279ConsumersReadLevelFromTheirNativePositions(int level, int expected, int character)
    {
        const string build = "cn-279";
        var tables = TestBuilds.Packets;
        var schema = TestBuilds.Of(build).Schema;
        var plaza = new PlazaMember(3, 7, "Ab", level, character, [character]);
        var party = PartyBinding.WriteParty(tables, build, new PartySnapshot([new(7, "Ab", level)]));
        Assert.Equal(36, party.Length);
        // cn-279 sub_44E7E0 stores the last member word at +20; cn-279 sub_8344D0 passes it to the rank row.
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(party.AsSpan(28)));

        var record = PlayRoomCodec.WritePlazaSeatRecord(tables, build, schema, plaza);
        Assert.Equal(102, record.Length);
        AssertSeat(record);
        var update = PlayRoomCodec.WritePlazaSeatSlot(tables, build, schema, plaza);
        Assert.Equal(122, update.Length);
        AssertSeat(update.AsSpan(4, record.Length));

        var menu = new MainMenuRoomOpened(7, "Ab", character, [character], level);
        var install = RoomCodec.WriteMainMenuEnvelope(menu, tables, build, schema);
        Assert.Equal(324, install.Length);
        AssertSeat(install.AsSpan(198, record.Length));
        var priorWriter = RoomCodec.WriteMainMenu(menu, schema);
        Assert.Equal(install.Length, priorWriter.Length);
        AssertSeat(priorWriter.AsSpan(198, record.Length));

        // cn-279 sub_44F8A0 reads FighterInfo after seven words; cn-279 sub_6F5F90 consumes its +16 level.
        var stat = UserStatWire.Write(tables, build, "Ab", level, 0, 0);
        Assert.Equal(172, stat.Length);
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(44)));
        Assert.Equal(level, plaza.Level);
        Assert.Equal(level, menu.Level);

        void AssertSeat(ReadOnlySpan<byte> body)
        {
            // cn-279 sub_4496E0 stores these words at +20/+32; cn-279 sub_685920 renders the former.
            Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(body[30..]));
            // cn-279 sub_701BD0 uses +32 as the outer IPv4 address, never as experience or rank.
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body[46..]));
        }
    }
}
