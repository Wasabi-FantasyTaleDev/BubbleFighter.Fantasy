using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    private static readonly MatchSeedRoster Roster = new([
        new MatchSeedOccupant(1_000_000, DefenceSeedTests.Worn(100072)),
        new MatchSeedOccupant(1_100_000, DefenceSeedTests.Worn(100203, 100204)),
        new MatchSeedOccupant(1_200_000, null),
    ]);

    /// <summary>
    /// A seat receives its critical table, every occupant's defence table in roster order, then its cancellation
    /// table; each defence table carries that occupant's own roll.
    /// </summary>
    [Fact]
    public async Task EachSeatReceivesEveryOccupantsDefenceTable()
    {
        var authority = DefenceAuthority("kr-812", DefenceSeedTests.Catalog("kr-812"), critical: true);
        var session = new SeedSession(1, "kr-812");
        await authority.SendAsync(session, Seat(0), default);
        var sent = session.Sent.ToArray();
        Assert.Equal([4, 5, 5, 5, 6], sent.Select(frame => (int)frame.Opcode));
        Assert.Equal([1_000_000, 1_100_000, 1_200_000], sent[1..4].Select(frame => BitConverter.ToInt32(frame.Payload, 0)));
        Assert.Equal(Enumerable.Range(1, 6), Values(sent[1].Payload).Distinct().Order());
        Assert.Equal([0], Values(sent[3].Payload).Distinct());
    }

    /// <summary>Every seat gets the same bytes for the same occupant, so all clients roll a victim's reduction alike.</summary>
    [Fact]
    public async Task EverySeatReceivesIdenticalBytesForEachOccupant()
    {
        var authority = DefenceAuthority("kr-812", DefenceSeedTests.Catalog("kr-812"), critical: true);
        var first = new SeedSession(1, "kr-812");
        var second = new SeedSession(2, "kr-812");
        await authority.SendAsync(first, Seat(0), default);
        await authority.SendAsync(second, Seat(1), default);
        var a = first.Sent.Where(frame => frame.Opcode == 5).Select(frame => frame.Payload).ToArray();
        var b = second.Sent.Where(frame => frame.Opcode == 5).Select(frame => frame.Payload).ToArray();
        Assert.Equal(3, a.Length);
        for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    /// <summary>Keeping equipment defence off preserves the original single-seat installation and report boundary.</summary>
    [Fact]
    public async Task DisabledDefencePreservesTheOriginalExchange()
    {
        var authority = Authority("cn-324", new CountingRandom());
        var session = new SeedSession(1, "cn-324");
        await authority.SendAsync(session, Seat(0), default);
        Assert.Equal(new ushort[] { 4, 5, 6 }, session.Sent.Select(frame => frame.Opcode));
        var defence = Assert.Single(session.Sent, frame => frame.Opcode == 5);
        Assert.Equal(Seat(0).EntityId, BitConverter.ToInt32(defence.Payload, 0));
        Assert.All(Values(defence.Payload), value => Assert.Equal(0, value));
        Assert.False(authority.Issued(session.Id, NativeSceneSeedKind.Defence, Seat(1).EntityId, out _));
    }

    /// <summary>Repeated requests never reset any occupant's defence cursor, including after a partially failed fan-out.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task DefenceFanOutCannotBeReinstalled(int failedAttempt)
    {
        var authority = DefenceAuthority("kr-812", DefenceSeedTests.Catalog("kr-812"), critical: true);
        var failure = new IOException("Defence queue failed.");
        var session = new SeedSession(1, "kr-812")
        {
            BeforeSend = (attempt, _) => attempt == failedAttempt ? Task.FromException(failure) : Task.CompletedTask,
        };
        if (failedAttempt == 0)
        {
            await authority.SendAsync(session, Seat(0), default);
            await authority.SendAsync(session, Seat(0), default);
            Assert.Equal(5, session.Attempts);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, Seat(0), default).AsTask()));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, Seat(0), default).AsTask()));
            Assert.Equal(failedAttempt, session.Attempts);
        }
    }

    /// <summary>A defence report may name any occupant the seat was sent; other kinds still name only the seat.</summary>
    [Fact]
    public async Task DefenceReportsAcceptEveryRosterOccupant()
    {
        var authority = DefenceAuthority("kr-812", DefenceSeedTests.Catalog("kr-812"), critical: true);
        var session = new SeedSession(1, "kr-812");
        await authority.SendAsync(session, Seat(0), default);
        Assert.True(authority.Issued(1, NativeSceneSeedKind.Defence, 1_100_000, out var generation));
        Assert.Equal(7, generation);
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Critical, 1_100_000, out _));
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Defence, 1_300_000, out _));
    }

    /// <summary>A build whose scene accept installs the critical tables is sent defence tables only.</summary>
    [Fact]
    public async Task DefenceOnlyRelaysReceiveNoCriticalTables()
    {
        var authority = DefenceAuthority("kr-1256", DefenceSeedTests.Catalog("kr-1256"), critical: false);
        var session = new SeedSession(1, "kr-1256");
        await authority.SendAsync(session, Seat(2), default);
        var sent = session.Sent.ToArray();
        Assert.Equal([5, 5, 5], sent.Select(frame => (int)frame.Opcode));
        Assert.Equal(Enumerable.Range(2, 6), Values(sent[0].Payload).Distinct().Order());
    }

    /// <summary>A roster that disagrees with the seat's own worn set is rejected before anything is sent.</summary>
    [Fact]
    public async Task RosterMustCarryTheSeatsOwnEquipment()
    {
        var authority = DefenceAuthority("kr-812", DefenceSeedTests.Catalog("kr-812"), critical: true);
        var session = new SeedSession(1, "kr-812");
        var request = Seat(0) with { Equipment = DefenceSeedTests.Worn(100204) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, request, default).AsTask());
        Assert.Empty(session.Sent);
    }

    /// <summary>A partial critical family is a construction error, not a silent half-sequence.</summary>
    [Fact]
    public void CriticalFamilyIsAllOrNothing()
    {
        Assert.Throws<ArgumentException>(() =>
            new SceneSeedAuthority("kr-812/relay", "kr-812", Tables, 4, 5, null, null, 4, new Random(1)));
    }

    private static MatchSeedTablesRequested Seat(int index) =>
        new(41, 7, Roster.Occupants[index].EntityId, MatchPreviousRound.None)
        {
            Equipment = Roster.Occupants[index].Equipment,
            Roster = Roster,
        };

    private static SceneSeedAuthority DefenceAuthority(string build, DefenceSeedCatalog catalog, bool critical)
    {
        var chances = Chances(Workbook(("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4")));
        return critical
            ? new SceneSeedAuthority(build + "/relay", build, Tables, 4, 5, 6, chances, 4, new Random(1), defences: catalog)
            : new SceneSeedAuthority(build + "/relay", build, Tables, null, 5, null, null, 4, new Random(1), defences: catalog);
    }

    // The defence body is the entity, the element count, then the bytes.
    private static IEnumerable<int> Values(byte[] payload)
    {
        Assert.Equal(1000, BitConverter.ToInt32(payload, 4));
        return payload.Skip(8).Select(value => (int)value);
    }
}
