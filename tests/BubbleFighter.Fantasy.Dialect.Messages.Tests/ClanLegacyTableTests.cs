using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins kr-812's clan-server connection: the bodies that differ from kr-1256 (the base tables of
/// <c>packets/clan-server.yaml</c>), byte for byte, and the opcode numbers of its dispatcher kr-812 sub_498790.
/// </summary>
public sealed class ClanLegacyTableTests
{
    // 2100-01-01 as days since 1899-12-30 (73051.0).
    private const string NoDisband = "00000000B0D5F140";

    private static readonly DateTime Created = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static PacketTables Tables => TestBuilds.Packets;

    private static ClanRecord Clan => new(1000, "Wolf", 42, "intro", "notice", 3, 250, 7, 100, 1, Created, null, 12);

    /// <summary>
    /// The fourteen profile fields in the order kr-812 sub_499140 reads them: one date, the member count, a zero word,
    /// then notice, introduction, mark and the capacity last. No second date and no recruitment word.
    /// </summary>
    [Fact]
    public void ProfileWritesTheFourteenFieldsInReaderOrder()
    {
        var body = ClanDirectoryBinding.WriteProfile(Tables, "kr-812", new ClanProfileSent(Clan, "Boss", 43, "Aide"));
        Assert.Equal(
            Wc("Wolf") + U32(42) + Wc("Boss") + U32(43) + Wc("Aide") + U32(3) + U32(250)
            + F64(ClanDirectoryBinding.ToOleDate(Created)) + U32(12) + U32(0) + Wc("notice") + Wc("intro") + U32(7) + U32(100),
            Convert.ToHexString(body));
    }

    /// <summary>kr-812 sub_499760 writes the name, a word, the introduction and the card item; the capacity is the server's.</summary>
    [Fact]
    public void CreateHasNoCapacityWord()
    {
        var body = Convert.FromHexString(Wc("Wolf") + U32(5) + Wc("hi") + U32(100819));
        Assert.Equal(new ClanCreateRequested("Wolf", 5, "hi", 100819, null), ClanDirectoryBinding.ReadCreate(Tables, "kr-812", body));
    }

    /// <summary>A kr-1256-shaped create (with the capacity) is not a kr-812 request: the extra word is trailing bytes.</summary>
    [Fact]
    public void CreateWithACapacityWordIsRejected()
    {
        var body = Convert.FromHexString(Wc("Wolf") + U32(5) + Wc("hi") + U32(100819) + U32(30));
        Assert.Throws<PacketFormatException>(() => ClanDirectoryBinding.ReadCreate(Tables, "kr-812", body));
    }

    /// <summary>kr-812 sub_49A7C0 reads two words: the result and the item.</summary>
    [Fact]
    public void CreateResultIsTwoWords() =>
        Assert.Equal(U32(1) + U32(100819),
            Convert.ToHexString(ClanDirectoryBinding.WriteCreateResult(Tables, "kr-812", new ClanCreateAnswered(1, 100819, 30))));

    /// <summary>
    /// A search row as kr-812 sub_49A800 reads it: the founding date, the member count and an unused word, the
    /// introduction, the disband date and the mark. No recruitment word, second date or last-login date.
    /// </summary>
    [Fact]
    public void SearchRowIsTwelveFields()
    {
        var page = new ClanSearchSent(2, 1, "wo", [new ClanSearchRow(Clan, "Boss", Created)]);
        Assert.Equal(
            U32(1) + U32(2) + U32(1) + Wc("wo")
            + U32(1000) + Wc("Wolf") + U32(42) + Wc("Boss") + U32(3) + U32(250) + F64(ClanDirectoryBinding.ToOleDate(Created))
            + U32(12) + U32(0) + Wc("intro") + NoDisband + U32(7),
            Convert.ToHexString(ClanDirectoryBinding.WriteSearchResult(Tables, "kr-812", page)));
    }

    /// <summary>kr-812 sub_49D1D0 reads a word it discards before the two strings; kr-1256 sub_52EFC0 reads the strings only.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ChatMessageHasALeadingWordOnEveryBuild(string build)
    {
        var body = PacketCodec.Write(Tables, build, "ClanServerChatMessage",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["nickname"] = "Bo", ["text"] = "hi" });
        Assert.Equal(U32(0) + Wc("Bo") + Wc("hi"), Convert.ToHexString(body));
    }

    /// <summary>The whisper request and message carry no trailing byte in kr-812 (kr-812 sub_498DB0, kr-812 sub_49B1F0).</summary>
    [Fact]
    public void WhispersHaveNoTrailingByte()
    {
        Assert.Equal("kind:u32,target:wcstr,text:wcstr", PacketLayoutContract.Signature(Tables, "kr-812", "ClanServerWhisper"));
        Assert.Equal("kind:u32,from:wcstr,to:wcstr,text:wcstr", PacketLayoutContract.Signature(Tables, "kr-812", "ClanServerWhisperMessage"));
        Assert.Equal("kind:u32,target:wcstr,text:wcstr,unknown_3:u8", PacketLayoutContract.Signature(Tables, "kr-1256", "ClanServerWhisper"));
    }

    /// <summary>
    /// cn-774 keeps kr-1256's clan-server shapes; tw-454 drops the create result's third word and both whisper bytes
    /// (tw-454 sub_685A10 case 0x404, tw-454 sub_6871C0, tw-454 sub_68A520).
    /// </summary>
    [Fact]
    public void The2019BuildsPinTheirClanServerDifferences()
    {
        var created = new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = 1, ["item"] = 100819, ["unknown_8"] = 0 };
        Assert.Equal(12, PacketCodec.Write(Tables, "cn-774", "ClanServerCreateResult", created).Length);
        created.Remove("unknown_8");
        Assert.Equal(8, PacketCodec.Write(Tables, "tw-454", "ClanServerCreateResult", created).Length);
        Assert.Equal("kind:u32,target:wcstr,text:wcstr,unknown_3:u8", PacketLayoutContract.Signature(Tables, "cn-774", "ClanServerWhisper"));
        Assert.Equal("kind:u32,from:wcstr,to:wcstr,text:wcstr,unknown_4:u8", PacketLayoutContract.Signature(Tables, "cn-774", "ClanServerWhisperMessage"));
        Assert.Equal("kind:u32,target:wcstr,text:wcstr", PacketLayoutContract.Signature(Tables, "tw-454", "ClanServerWhisper"));
        Assert.Equal("kind:u32,from:wcstr,to:wcstr,text:wcstr", PacketLayoutContract.Signature(Tables, "tw-454", "ClanServerWhisperMessage"));
    }

    /// <summary>
    /// cn-735 keeps kr-812's clan numbers but reads kr-1256's profile and search row (cn-735 sub_5CFDF0, cn-735 sub_5D1780);
    /// its other clan bodies, such as the create request, stay kr-812's.
    /// </summary>
    [Fact]
    public void Cn735ReadsKr1256ProfileAndSearchRowsUnderKr812Numbers()
    {
        foreach (var structure in new[] { "ClanServerProfile", "ClanServerSearchEntry" })
            Assert.Equal(PacketLayoutContract.Signature(Tables, "kr-1256", structure), PacketLayoutContract.Signature(Tables, "cn-735", structure));
        Assert.Equal(PacketLayoutContract.Signature(Tables, "kr-812", "ClanServerCreate"), PacketLayoutContract.Signature(Tables, "cn-735", "ClanServerCreate"));
        Assert.Equal(1018, (int)TestBuilds.Opcodes("cn-735").ForConnection(OpcodeConnection.Clan).S2C("ClanProfile"));
        Assert.Equal(1102, (int)TestBuilds.Opcodes("cn-735").ForConnection(OpcodeConnection.Clan).S2C("ClanRecruitmentEditResult"));
        // cn-738 sub_60B1C0 adds kr-1256's capacity word to the create request; cn-742 is based on cn-738.
        Assert.Equal(PacketLayoutContract.Signature(Tables, "kr-1256", "ClanServerCreate"), PacketLayoutContract.Signature(Tables, "cn-738", "ClanServerCreate"));
        Assert.Equal(PacketLayoutContract.Signature(Tables, "kr-812", "ClanServerRosterMember"), PacketLayoutContract.Signature(Tables, "cn-738", "ClanServerRosterMember"));
        // cn-742 sub_55F5A0 reads the same sixteen profile fields under the same number.
        Assert.Equal(PacketLayoutContract.Signature(Tables, "kr-1256", "ClanServerProfile"), PacketLayoutContract.Signature(Tables, "cn-742", "ClanServerProfile"));
        Assert.Equal(1018, (int)TestBuilds.Opcodes("cn-742").ForConnection(OpcodeConnection.Clan).S2C("ClanProfile"));
    }

    /// <summary>The game-connection empty bodies of kr-812: the passport 20 bytes, two mark tables 12, guild info 16, no attendance.</summary>
    [Fact]
    public void GameConnectionEmptyBodiesHaveTheKr812Lengths()
    {
        Assert.Equal(20, ClanBinding.WritePassport(Tables, "kr-812").Length);
        Assert.Equal(12, ClanBinding.WriteMarkInfo(Tables, "kr-812").Length);
        Assert.Equal(16, ClanBinding.WriteGuildInfo(Tables, "kr-812").Length);
        Assert.Empty(ClanBinding.WriteAttendanceState(Tables, "kr-812"));
    }

    /// <summary>
    /// The numbers are kr-812's own (writers stamped through kr-812 sub_A518F0, cases of kr-812 sub_498790): equal to
    /// kr-1256's up to 1032, one lower for most rows after it.
    /// </summary>
    [Theory]
    [InlineData("c2s", "ClanHeartbeat", 1003)]
    [InlineData("c2s", "ClanLogin", 1005)]
    [InlineData("s2c", "ClanLoginResult", 1006)]
    [InlineData("c2s", "ClanHomeRequest", 1015)]
    [InlineData("s2c", "ClanHome", 1016)]
    [InlineData("s2c", "ClanProfile", 1018)]
    [InlineData("c2s", "ClanCreate", 1027)]
    [InlineData("s2c", "ClanCreateResult", 1028)]
    [InlineData("c2s", "ClanApply", 1032)]
    [InlineData("s2c", "ClanApplyResult", 1033)]
    [InlineData("c2s", "ClanLeave", 1035)]
    [InlineData("s2c", "ClanMemberLeft", 1036)]
    [InlineData("c2s", "ClanWhisper", 1041)]
    [InlineData("s2c", "ClanWhisperMessage", 1043)]
    [InlineData("c2s", "ClanTransferAnswer", 1059)]
    [InlineData("c2s", "ClanChat", 1065)]
    [InlineData("s2c", "ClanChatMessage", 1067)]
    [InlineData("c2s", "ClanApplicantsRequest", 1068)]
    [InlineData("s2c", "ClanApplicants", 1069)]
    [InlineData("s2c", "ClanMarkChanged", 1096)]
    public void OpcodesAreTheClientNumbers(string direction, string name, int value)
    {
        var clan = TestBuilds.Opcodes("kr-812").ForConnection(OpcodeConnection.Clan);
        Assert.Equal(value, (int)(direction == "c2s" ? clan.C2S(name) : clan.S2C(name)));
    }

    /// <summary>kr-812 has no join, recruitment or clan-connection mark edit: the rows are absent, not guessed.</summary>
    [Theory]
    [InlineData("ClanJoin")]
    [InlineData("ClanRecruitmentEdit")]
    [InlineData("ClanMarkEdit")]
    public void RequestsKr812LacksHaveNoRow(string name) =>
        Assert.Null(TestBuilds.Opcodes("kr-812").ForConnection(OpcodeConnection.Clan).Find(PacketDirection.ClientToServer, name));

    private static string U32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    private static string F64(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    /// <summary>A string as this connection carries it: UTF-16LE up to a NUL word, no count.</summary>
    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
