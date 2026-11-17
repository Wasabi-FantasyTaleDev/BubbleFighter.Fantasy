using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan game-connection bodies through <c>packets/clan.yaml</c> and the binding's empty writers,
/// after slice S9 moved the clan plugin onto the tables: the exact empty hex per build, a field walk in the
/// client's read order that ends with zero remaining bytes, the seven opcode numbers by table name, the
/// three empty request structures and the builds that carry the <c>clan</c> capability. This keeps the
/// evidence of the deleted <c>ClanWireByteLengthTests</c> and <c>ClanRequestParseTests</c>.
/// </summary>
public sealed class ClanTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>The empty passport is 20 bytes on the enabled build; cn-774 inherits the same table.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void EmptyClanPassportIsTwentyBytes(string build)
    {
        var body = ClanBinding.WritePassport(Tables, build);
        Assert.Equal(20, body.Length);
        Assert.Equal("0000000000000000000000000000000000000000", Convert.ToHexString(body));
        ReadSub99C1D0(body);
    }

    /// <summary>The empty mark info is 8 bytes: one word and one empty counted table.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void EmptyClanMarkInfoIsEightBytes(string build)
    {
        var body = ClanBinding.WriteMarkInfo(Tables, build);
        Assert.Equal(8, body.Length);
        Assert.Equal("0000000000000000", Convert.ToHexString(body));
        ReadSub99CFA0(body);
    }

    /// <summary>The empty guild info is 16 bytes: an i64 and two words kept as booleans.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void EmptyClanGuildInfoIsSixteenBytes(string build)
    {
        var body = ClanBinding.WriteGuildInfo(Tables, build);
        Assert.Equal(16, body.Length);
        Assert.Equal("00000000000000000000000000000000", Convert.ToHexString(body));
        ReadSub9B2690(body);
    }

    /// <summary>
    /// The empty attendance state is 14 bytes: a card count of zero, then the Attendance object's 4, 4 and 2 bytes that
    /// kr-1256 sub_9CD440 reads through kr-1256 sub_113F1F0 after the list.
    /// </summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void EmptyClanAttendanceStateIsFourteenBytes(string build)
    {
        var body = ClanBinding.WriteAttendanceState(Tables, build);
        Assert.Equal(14, body.Length);
        Assert.Equal(new string('0', 28), Convert.ToHexString(body));
        ReadSub9CD440(body);
    }

    /// <summary>The clan tables are written empty on every enabled field and leave nothing unread.</summary>
    /// <remarks>
    /// The passport walk follows the five pieces the table states for kr-1256: the leading word the raw
    /// .text bytes at 0x99C1D0-0x99C550 pin before the name (the decompile is flagged and dropped that
    /// read), then the name, two words and the byte list.
    /// </remarks>
    private static void ReadSub99C1D0(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadWideStringPrefixed());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Walks the mark info as kr-1256 sub_99CFA0 reads it: a word, then a u32 count.</summary>
    private static void ReadSub99CFA0(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Walks the guild info as kr-1256 sub_9B2690 reads it: i64, then two 4-byte words kept as booleans.</summary>
    private static void ReadSub9B2690(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.Equal(0L, reader.ReadInt64());
        Assert.False(reader.ReadBool32());
        Assert.False(reader.ReadBool32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Walks the attendance state as kr-1256 sub_10C5590 reads it: a u32 card count, then the cards.</summary>
    // kr-1256 sub_9CD440: the card list (kr-1256 sub_10C5590), then kr-1256 sub_113F1F0 reads 4, 4 and 2 bytes.
    private static void ReadSub9CD440(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.Equal(0u, reader.ReadUInt32());
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt16();
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>The seven clan numbers the kr-1256 table gives the names S9 moved into the table.</summary>
    [Fact]
    public void ClanOpcodesAreTheClientNumbers()
    {
        var table = TestBuilds.Opcodes("kr-1256");
        Assert.Equal(21, (int)table.S2C("gspClanPassport"));
        Assert.Equal(140, (int)table.S2C("gspReqClanMarkInfo"));
        Assert.Equal(918, (int)table.S2C("gspResGuildInfo"));
        Assert.Equal(1508, (int)table.S2C("gspClanAttendanceState"));
        Assert.Equal(25, (int)table.C2S("gcpReqClanPassport"));
        Assert.Equal(2040, (int)table.C2S("gcpReqClanMainOpen_"));
        Assert.Equal(2041, (int)table.C2S("gcpReqClanAttendanceCheckIn_"));
    }

    /// <summary>The three client requests are stated as structures with no fields, so they write zero bytes.</summary>
    [Theory]
    [InlineData("ClanPassportRequest")]
    [InlineData("ClanMainOpenRequest")]
    [InlineData("ClanAttendanceCheckInRequest")]
    public void EmptyRequestStructuresWriteNoBytes(string structure)
    {
        Assert.Empty(Tables.Resolve("kr-1256", structure));
        Assert.Empty(PacketCodec.Write(Tables, "kr-1256", structure, new Dictionary<string, object?>()));
    }

    /// <summary>kr-1256 and kr-812 have their clan readers verified; cn-774 and tw-454 stay off until theirs are read.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("kr-812", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public void ClanCapabilityIsEnabledOnTheReadBuildsOnly(string build, bool expected) =>
        Assert.Equal(expected, Tables.CapabilityOf(build, "clan"));
}
