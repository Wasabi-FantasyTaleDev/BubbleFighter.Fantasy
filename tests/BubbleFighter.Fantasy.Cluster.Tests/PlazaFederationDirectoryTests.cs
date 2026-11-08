using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Checks which nodes share plaza members through the directory and how stale members leave.</summary>
public sealed class PlazaFederationDirectoryTests
{
    private const string Domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly PlazaWireMember Look = new("seven", 5, 300014, [300014, 100111], 2);

    [Fact]
    public async Task NodesOfOneVerifiedDomainSeeEachOthersMembersButFriendlyServersAndTheOwnerDoNot()
    {
        var (directory, _, leases) = Create();
        Assert.Equal(204, Publish(directory, leases, "a", Entered(7), Moved(7)));
        var b = await Read(directory, leases, "b", "", 0);
        Assert.True(b.Snapshot);
        Assert.Equal(new[] { PlazaFederationEventKind.Entered, PlazaFederationEventKind.Moved }, b.Events.Select(change => change.Kind));
        Assert.Equal("seven", b.Events[0].Member!.Nickname);
        Assert.Empty((await Read(directory, leases, "a", "", 0)).Events);
        Assert.Empty((await Read(directory, leases, "friendly", "", 0)).Events);

        Assert.Empty((await Read(directory, leases, "b", b.Hub, b.Cursor)).Events);
        Assert.Equal(204, Publish(directory, leases, "a", Moved(7), new("kr-1256", PlazaFederationEventKind.Left, 7, null, null)));
        var next = await Read(directory, leases, "b", b.Hub, b.Cursor);
        Assert.False(next.Snapshot);
        Assert.Equal(new[] { PlazaFederationEventKind.Moved, PlazaFederationEventKind.Left }, next.Events.Select(change => change.Kind));
        Assert.Empty((await Read(directory, leases, "b", "", 0)).Events);
    }

    [Fact]
    public async Task ALapsedNodeTakesItsMembersWithItAndAnotherNodeCannotClaimThemMeanwhile()
    {
        var (directory, time, leases) = Create();
        Assert.Equal(204, Publish(directory, leases, "a", Entered(7)));
        var b = await Read(directory, leases, "b", "", 0);
        // b's report for a player a still hosts is ignored, so b keeps seeing a's member.
        Assert.Equal(204, Publish(directory, leases, "b", Entered(7)));
        Assert.Equal(7, Assert.Single((await Read(directory, leases, "b", "", 0)).Events).User);
        time.Advance(10);
        Observe(directory, leases, "b", 2);
        time.Advance(6);
        var after = await Read(directory, leases, "b", b.Hub, b.Cursor);
        var left = Assert.Single(after.Events);
        Assert.Equal((PlazaFederationEventKind.Left, 7), (left.Kind, left.User));
    }

    [Fact]
    public async Task AnUnprovenDomainOrUnreportedBuildIsRefusedAndALongPollWakesOnAChange()
    {
        var (directory, _, leases) = Create();
        Assert.Equal(400, Publish(directory, leases, "a", Entered(7) with { Build = "cn-324" }));
        Assert.Equal(409, directory.PublishPlaza("a", "stale", leases["a"].Epoch, new([Entered(7)])));
        Assert.Equal(409, Publish(directory, leases, "unproven", Entered(8)));
        var start = await Read(directory, leases, "b", "", 0);
        var waiting = directory.ReadPlazaAsync("b", leases["b"].Token, leases["b"].Epoch, start.Hub, start.Cursor,
            TimeSpan.FromSeconds(10), default).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(204, Publish(directory, leases, "a", Entered(7)));
        var (status, batch) = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, status);
        Assert.Equal(7, Assert.Single(batch!.Events).User);

        var disabled = new ServerDirectory(ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", [])));
        Assert.Equal(404, disabled.PublishPlaza("a", "token", 1, new([Entered(7)])));
    }

    private static PlazaWireEvent Entered(int user) => new("kr-1256", PlazaFederationEventKind.Entered, user, Look, null);

    private static PlazaWireEvent Moved(int user) => new("kr-1256", PlazaFederationEventKind.Moved, user, null, [1, 2, 3, 4]);

    private static int Publish(ServerDirectory directory, Dictionary<string, LeaseGrant> leases, string node, params PlazaWireEvent[] changes) =>
        directory.PublishPlaza(node, leases[node].Token, leases[node].Epoch, new(changes));

    private static async Task<PlazaWireBatch> Read(ServerDirectory directory, Dictionary<string, LeaseGrant> leases, string node,
        string hub, long after)
    {
        var (status, batch) = await directory.ReadPlazaAsync(node, leases[node].Token, leases[node].Epoch, hub, after, TimeSpan.Zero, default);
        Assert.Equal(200, status);
        return batch!;
    }

    private static void Observe(ServerDirectory directory, Dictionary<string, LeaseGrant> leases, string node, long sequence) =>
        Assert.True(directory.Observe(node, new(leases[node].Token, leases[node].Epoch, sequence, false,
            Domain, [new("kr-1256", true, 1)], [])));

    private static (ServerDirectory Directory, Clock Time, Dictionary<string, LeaseGrant> Leases) Create()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []),
            ServerDirectoryTests.Node("friendly", "friendly", []), ServerDirectoryTests.Node("unproven", "formal2", []));
        options.PlazaFederation = true;
        Assert.Empty(options.Validate());
        var time = new Clock();
        var directory = new ServerDirectory(options, time);
        var leases = new Dictionary<string, LeaseGrant>();
        foreach (var node in new[] { "a", "b", "friendly", "unproven" })
        {
            leases[node] = Assert.IsType<LeaseGrant>(directory.Register(node, Guid.NewGuid().ToString("N")));
            // The unproven node cannot report without a database identity, so its fresh lease proves no domain.
            if (node != "unproven") Observe(directory, leases, node, 1);
        }
        return (directory, time, leases);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
