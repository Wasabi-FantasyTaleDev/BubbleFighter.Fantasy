using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the table-driven login burst bodies and the move-stage reader against the bytes the modern login has
/// always sent: the balance words, the zero pc-room snapshot, the empty center list and the three-word
/// move-into reply. The tables themselves are packets/login.yaml; the gate command <c>bff packets check</c>
/// loads the same directory through the same reader.
/// </summary>
public sealed class LoginBurstWireTests
{
    private const string FundedMoney = "00000000000000000000000000000000D20400002E160000";
    private const string EmptyMoney = "000000000000000000000000000000000000000000000000";
    private const string Pcroom = "0000000000000000000000000000";
    private const string EmptyCenterList = "00000000";
    private const string MoveInto = "010000005000000000000000";

    /// <summary>The three builds the shared login serves; cn-774 and tw-454 inherit kr-1256's layouts.</summary>
    public static TheoryData<string> ModernBuilds => ["kr-1256", "cn-774", "tw-454"];

    /// <summary>
    /// The six-word money body: the funded form keeps the first four words zero and puts lucci and cash in
    /// the fifth and sixth, and the unset wallet form is all zero (the bytes pinned by the old writer).
    /// </summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void MoneySnapshotKeepsTheBalancesInTheLastTwoWords(string build)
    {
        Assert.Equal(FundedMoney, Convert.ToHexString(LoginBurstWire.WriteUserMoneyStat(TestBuilds.Packets, build, 1234, 5678)));
        Assert.Equal(EmptyMoney, Convert.ToHexString(LoginBurstWire.WriteUserMoneyStat(TestBuilds.Packets, build, 0, 0)));
    }

    /// <summary>The pc-room body stays 14 bytes: two zero words, an empty counted string and a zero u16.</summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void PcroomSnapshotIsZeroAndEmpty(string build)
    {
        var body = LoginBurstWire.WriteUserPcroomStat(TestBuilds.Packets, build);
        Assert.Equal(14, body.Length);
        Assert.Equal(Pcroom, Convert.ToHexString(body));
    }

    /// <summary>The empty center list is the single zero count the modern reader consumes.</summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void CenterListEmptyIsTheZeroCount(string build)
    {
        Assert.Equal(EmptyCenterList, Convert.ToHexString(LoginBurstWire.WriteCenterListEmpty(TestBuilds.Packets, build)));
    }

    /// <summary>
    /// A build whose table does not describe the count-first list must fail loud instead of sending an empty
    /// body; the legacy baseline's own center body is not audited in these tables.
    /// </summary>
    [Fact]
    public void CenterListRefusesABuildWithoutTheCountFirstLayout()
    {
        Assert.Throws<ConfigException>(() => LoginBurstWire.WriteCenterListEmpty(TestBuilds.Packets, "kr-812"));
    }

    /// <summary>
    /// The move request is exactly one word: both a short and a long body are rejected, while the word's
    /// bits come back unchanged (all ones stays -1).
    /// </summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void MoveStageReadsExactlyOneWord(string build)
    {
        Assert.Equal(70, LoginBurstWire.ReadMoveStage(TestBuilds.Packets, build, [70, 0, 0, 0]));
        Assert.Equal(-1, LoginBurstWire.ReadMoveStage(TestBuilds.Packets, build, [0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Throws<PacketFormatException>(() => LoginBurstWire.ReadMoveStage(TestBuilds.Packets, build, ReadOnlySpan<byte>.Empty));
        Assert.Throws<PacketFormatException>(() => LoginBurstWire.ReadMoveStage(TestBuilds.Packets, build, [1, 2, 3]));
        Assert.Throws<PacketFormatException>(() => LoginBurstWire.ReadMoveStage(TestBuilds.Packets, build, [1, 2, 3, 4, 5]));
    }

    /// <summary>
    /// The move-into reply is three words: the nonzero result, the channel and the unproven third word, which
    /// keeps its default. The same twelve bytes go to every build the shared login serves.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void MoveIntoWritesResultChannelAndTheUnprovenWord(string build)
    {
        var body = LoginBurstWire.WriteMoveInto(TestBuilds.Packets, build, 1, 80);
        Assert.Equal(12, body.Length);
        Assert.Equal(MoveInto, Convert.ToHexString(body));
    }

    /// <summary>
    /// The main-menu probe reply keeps the seven-byte empty <c>gspSessionSignatureData</c> body the modern
    /// codec wrote: a zero u16, a zero u8 and an empty counted byte vector (consumer
    /// kr-1256 sub_9EBD80 -&gt; kr-1256 sub_1155B00). The table fields are the same on the three modern builds.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModernBuilds))]
    public void SessionSignatureEmptyPinsTheSevenByteProbeReply(string build)
    {
        var body = LoginBurstWire.WriteSessionSignatureEmpty(TestBuilds.Packets, build);
        Assert.Equal(7, body.Length);
        Assert.Equal("00000000000000", Convert.ToHexString(body));
    }

    /// <summary>
    /// A zero category mask in kr-1256 sub_9D2FF0 case 1500 carries no compressed configuration blobs.
    /// </summary>
    [Fact]
    public void DynamicConfigEmptyPinsTheZeroMaskBody()
    {
        var body = LoginBurstWire.WriteDynamicConfigEmpty(TestBuilds.Packets, "kr-1256");
        Assert.Equal(4, body.Length);
        Assert.Equal("00000000", Convert.ToHexString(body));
    }

    /// <summary>
    /// Loads the repository's packet directory the way <c>bff packets check</c> does and confirms every
    /// structure this file writes resolves for the modern family with the audited field count.
    /// </summary>
    [Fact]
    public void RepositoryTablesDescribeTheLoginBurstForEveryModernBuild()
    {
        var tables = PacketTables.Load(TestBuilds.PacketsDirectory());
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            Assert.Equal(6, tables.Resolve(build, "UserMoneyStat").Count);
            Assert.Equal(4, tables.Resolve(build, "UserPcroomStat").Count);
            Assert.Single(tables.Resolve(build, "gspCenterList"));
            Assert.Equal(5, tables.Resolve(build, "GateInfo").Count);
            Assert.Single(tables.Resolve(build, "MoveStageRequest"));
            Assert.Equal(3, tables.Resolve(build, "gspRepMoveInto").Count);
        }
        Assert.Empty(tables.Resolve("kr-812", "gspCenterList"));
    }
}
