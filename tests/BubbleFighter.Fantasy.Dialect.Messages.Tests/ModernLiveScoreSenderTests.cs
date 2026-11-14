using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins personal statistics on the modern sender's actual two-frame score publication.</summary>
public sealed class ModernLiveScoreSenderTests
{
    /// <summary>Each modern build sends both changed seats once, with native counters and recipient-local opcodes.</summary>
    [Theory]
    [InlineData("kr-1256", 0, "010B000301040000001013000005000C04000E0200040000001008000002000C01000E040000000000")]
    [InlineData("kr-1256", 5000, "010B000301040000001013000005000C04000E0200040000001008000002000C01000E040000000000")]
    [InlineData("cn-774", 0, "010B000301040000001E13000005001904001B0200040000001E08000002001901001B040000000000")]
    [InlineData("cn-774", 5000, "010B000301040000001E13000005001904001B0200040000001E08000002001901001B040000000000")]
    [InlineData("tw-454", 0, "010B000301040000001E13000005001904001B0200040000001E08000002001901001B040000000000")]
    [InlineData("tw-454", 5000, "010B000301040000001E13000005001904001B0200040000001E08000002001901001B040000000000")]
    public async Task BothSeatsReachTheClient(string build, int shift, string first)
    {
        var native = TestBuilds.Opcodes(build);
        var table = new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + shift)) }));
        var session = new Session(table.Profile);
        await Sender(build, table).SendAsync(session,
            new RoundScoreUpdated(7, 2, [11, 22], [new(3, 4, 5, 19, 2), new(1, 1, 2, 8, 4)]), default);

        Assert.Collection(session.Sent,
            frame =>
            {
                Assert.Equal(table.S2C("gspUpdateScoreData"), frame.Opcode);
                Assert.Equal(41, frame.Body.Length);
                Assert.Equal(first, Convert.ToHexString(frame.Body));
            },
            frame =>
            {
                Assert.Equal(table.S2C("gspUpdateScoreData"), frame.Opcode);
                Assert.Equal(17, frame.Body.Length);
                Assert.Equal("021600FFFF000000000000000000000000", Convert.ToHexString(frame.Body));
            });
    }

    /// <summary>A single changed seat retains the missing victim sentinel, including the native table's last seat.</summary>
    [Theory]
    [InlineData("kr-1256", "010B0023FF040000001013000005000C04000E02000000000000000000")]
    [InlineData("cn-774", "010B0023FF040000001E13000005001904001B02000000000000000000")]
    [InlineData("tw-454", "010B0023FF040000001E13000005001904001B02000000000000000000")]
    public async Task OneSeatDoesNotResetAnAbsentVictim(string build, string first)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new Session(table.Profile);
        await Sender(build, table).SendAsync(session, new RoundScoreUpdated(7, 0, [11, 22], [new(35, 4, 5, 19, 2)]), default);
        Assert.Equal(29, session.Sent[0].Body.Length);
        Assert.Equal(first, Convert.ToHexString(session.Sent[0].Body));
    }

    /// <summary>A total-only update retains both missing seats and three empty lists in each frame.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task EmptySeatUpdatesKeepTheExistingTotals(string build)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new Session(table.Profile);
        await Sender(build, table).SendAsync(session, new RoundScoreUpdated(7, 2, [11, 22], []), default);
        Assert.Equal(new[] { "010B00FFFF000000000000000000000000", "021600FFFF000000000000000000000000" },
            session.Sent.Select(frame => Convert.ToHexString(frame.Body)));
    }

    /// <summary>WeaponMaster progress reaches the native gauge through the published sender, after ordinary counters.</summary>
    [Fact]
    public async Task WeaponMasterProgressReachesTheLiveGauge()
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var session = new Session(table.Profile);
        await Sender("kr-1256", table).SendAsync(session, new RoundScoreUpdated(7, 1, [18, 0],
            [new(0, 18, 0, 0, 7) { WeaponMaster = new(10, 1800, 1800, 1801) }]), default);
        var first = session.Sent[0].Body;
        Assert.Equal(41, first.Length);
        Assert.Equal("01120000FF080000001000000000000C12000E07001F0A002008072108072209070000000000000000", Convert.ToHexString(first));
    }

    /// <summary>Invalid changed-seat sets fail before publishing either frame instead of aliasing or discarding rows.</summary>
    [Theory]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 36 })]
    [InlineData(new[] { 256 })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 0, 1, 2 })]
    public async Task InvalidSeatsPublishNothing(int[] slots)
    {
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            var table = TestBuilds.Opcodes(build);
            var session = new Session(table.Profile);
            await Assert.ThrowsAsync<ArgumentException>(() => Sender(build, table).SendAsync(session,
                new RoundScoreUpdated(7, 1, [11, 22], slots.Select(slot => new RoundSeatStats(slot, 0, 0, 0, 0)).ToArray()), default).AsTask());
            Assert.Empty(session.Sent);
        }
    }

    /// <summary>An unrepresentable total in either frame fails before the client sees a partial score update.</summary>
    [Theory]
    [InlineData(-1, 22)]
    [InlineData(65536, 22)]
    [InlineData(11, -1)]
    [InlineData(11, 65536)]
    public async Task InvalidTotalsPublishNothing(int red, int blue)
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var session = new Session(table.Profile);
        await Assert.ThrowsAsync<OverflowException>(() => Sender("kr-1256", table).SendAsync(session,
            new RoundScoreUpdated(7, 1, [red, blue], [new(0, 1, 2, 3, 4)]), default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>Missing or additional team totals are rejected before either native frame is sent.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task InvalidTeamVectorPublishesNothing(int count)
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var session = new Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(() => Sender("kr-1256", table).SendAsync(session,
            new RoundScoreUpdated(7, 1, new int[count], []), default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>The modern sender rejects incompatible seat and vector layouts when it is constructed.</summary>
    [Theory]
    [InlineData("scorer_slot", "u8", "u16")]
    [InlineData("victim_slot", "u8", "u16")]
    [InlineData("scorer_stats", "\"list<RoundLiveScoreStat>\"", "empty_list")]
    [InlineData("victim_stats", "\"list<RoundLiveScoreStat>\"", "empty_list")]
    [InlineData("unknown_32", "\"list<RoundLiveScoreStat>\"", "empty_list")]
    public void IncompatibleSeatLayoutsFailAtConstruction(string field, string from, string to)
    {
        var directory = PacketTableEdits.WithFieldType("RoundLiveScore@kr-1256", field, from, to);
        var packets = PacketTables.Load(directory.FullName);
        var table = TestBuilds.Opcodes("cn-774");
        var error = Assert.Throws<ConfigException>(() => new RoundResultSender("cn-774/game", packets, "cn-774",
            _ => table, [1], new("unknown_500", new Dictionary<string, object?>()), null));
        Assert.Contains("RoundLiveScore." + field, error.Message, StringComparison.Ordinal);
    }

    private static RoundResultSender Sender(string build, IOpcodeTable table) => new(build + "/game",
        TestBuilds.Packets, build, _ => table, [1, 6, 26], new("unknown_500", new Dictionary<string, object?>()), null);

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }
}
