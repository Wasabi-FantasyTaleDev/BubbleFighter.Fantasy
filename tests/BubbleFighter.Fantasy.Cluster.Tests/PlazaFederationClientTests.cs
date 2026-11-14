using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Runs two nodes' plaza links against the directory over HTTP.</summary>
public sealed class PlazaFederationClientTests
{
    // Once with a lease source that reports its changes, so the send loop sleeps between changes, and once with
    // one that cannot, so it keeps its period.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMemberEnteringOnOneNodeIsShownOnTheOtherAndLeavesWithIt(bool signalling)
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        options.PlazaFederation = true;
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var url = server.Addresses.Single();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var a = Node(directory, url, "a", signalling);
        var b = Node(directory, url, "b", signalling);
        var runs = new[] { a, b }.SelectMany(node => new[] { node.Client.SendLoopAsync(stop.Token).AsTask(), node.Client.ReceiveLoopAsync(stop.Token).AsTask() }).ToArray();
        try
        {
            using var bindingA = (Binding)a.Endpoint.Bind(a.Client.LinkFor("kr-1256"));
            using var bindingB = (Binding)b.Endpoint.Bind(b.Client.LinkFor("kr-1256"));
            bindingA.Link.Publish(new(PlazaFederationEventKind.Entered, 7, new(0, 7, "seven", 5, 300014, [300014]) { ColorIndex = 3 }, default));
            bindingA.Link.Publish(new(PlazaFederationEventKind.Moved, 7, null, new byte[] { 1, 2, 3 }));
            var entered = await b.Endpoint.WaitFor(change => change.Kind == PlazaFederationEventKind.Moved, stop.Token);
            Assert.Contains(b.Endpoint.Changes, change => change is { Kind: PlazaFederationEventKind.Entered, UserNumber: 7, Member.Nickname: "seven", Member.ColorIndex: 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, entered.Position.ToArray());
            Assert.DoesNotContain(a.Endpoint.Changes, change => change.UserNumber == 7);
            bindingA.Link.Publish(new(PlazaFederationEventKind.Left, 7, null, default));
            await b.Endpoint.WaitFor(change => change.Kind == PlazaFederationEventKind.Left, stop.Token);
            // The loop has been idle for many periods by now; a change made after that must still go out.
            await Task.Delay(200, stop.Token);
            bindingA.Link.Publish(new(PlazaFederationEventKind.Entered, 8, new(0, 8, "eight", 5, 300014, [300014]), default));
            await b.Endpoint.WaitFor(change => change is { Kind: PlazaFederationEventKind.Entered, UserNumber: 8 }, stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runs);
            a.Client.Dispose();
            b.Client.Dispose();
        }
    }

    [Fact]
    public async Task MembersQueuedBeforeTheLeaseAreSentWhenItArrivesAndAgainForANewLease()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        options.PlazaFederation = true;
        options.Listen = "127.0.0.1:0";
        var clock = new Clock();
        var directory = new ServerDirectory(options, clock);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var url = server.Addresses.Single();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = new NodeLeaseSource("a", url);
        var a = Node(url, "a", source);
        var receiver = new NodeLeaseSource("b", url);
        receiver.Publish(Grant(directory, "b"));
        var b = Node(url, "b", receiver);
        var runs = new[] { a, b }.SelectMany(node => new[] { node.Client.SendLoopAsync(stop.Token).AsTask(), node.Client.ReceiveLoopAsync(stop.Token).AsTask() }).ToArray();
        try
        {
            using var bindingA = (Binding)a.Endpoint.Bind(a.Client.LinkFor("kr-1256"));
            using var bindingB = (Binding)b.Endpoint.Bind(b.Client.LinkFor("kr-1256"));
            bindingA.Link.Publish(new(PlazaFederationEventKind.Entered, 7, new(0, 7, "seven", 5, 300014, [300014]), default));
            await Task.Delay(300, stop.Token);
            Assert.DoesNotContain(b.Endpoint.Changes, change => change.UserNumber == 7);

            source.Publish(Grant(directory, "a"));
            await b.Endpoint.WaitFor(change => change is { Kind: PlazaFederationEventKind.Entered, UserNumber: 7 }, stop.Token);

            // Both leases run out and the nodes register again. The sender's loop is idle by now and must wake for
            // the new lease to send its members under it.
            clock.Advance(options.LeaseSeconds + 1);
            receiver.Publish(Grant(directory, "b"));
            await Task.Delay(500, stop.Token);
            var before = b.Endpoint.Changes.Count(change => change is { Kind: PlazaFederationEventKind.Entered, UserNumber: 7 });
            source.Publish(Grant(directory, "a"));
            while (b.Endpoint.Changes.Count(change => change is { Kind: PlazaFederationEventKind.Entered, UserNumber: 7 }) == before)
                await Task.Delay(20, stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runs);
            a.Client.Dispose();
            b.Client.Dispose();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Volatile.Read(ref _ticks));
        internal void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private static LeaseGrant Grant(ServerDirectory directory, string id)
    {
        var grant = Assert.IsType<LeaseGrant>(directory.Register(id, Guid.NewGuid().ToString("N")));
        Assert.True(directory.Observe(id, new(grant.Token, grant.Epoch, 1, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 1)], [])));
        return grant;
    }

    private static (PlazaFederationClient Client, Endpoint Endpoint) Node(ServerDirectory directory, string url, string id, bool signalling)
    {
        var grant = Grant(directory, id);
        INodeLeaseSource lease = new Lease(id, url, grant);
        if (signalling)
        {
            var source = new NodeLeaseSource(id, url);
            source.Publish(grant);
            lease = source;
        }
        return Node(url, id, lease);
    }

    private static (PlazaFederationClient Client, Endpoint Endpoint) Node(string url, string id, INodeLeaseSource lease)
    {
        var endpoint = new Endpoint();
        var options = new PlazaFederationOptions
        {
            NodeId = id, DirectoryUrl = url, AccessKey = new string('x', 32) + id, Builds = ["kr-1256"],
            FlushIntervalMs = 20, PollWaitSeconds = 1, HttpTimeoutMs = 5000, RetryDelayMs = 100,
        };
        Assert.Empty(options.Validate());
        var http = new HttpClient { BaseAddress = new(url), Timeout = Timeout.InfiniteTimeSpan };
        return (new(options, lease, http, [endpoint], NullLogger.Instance), endpoint);
    }

    private sealed class Lease(string id, string url, LeaseGrant grant) : INodeLeaseSource
    {
        public string NodeId => id;
        public string DirectoryUrl => url;
        public NodeLeaseCredentials? Current => new(grant.Token, grant.Epoch);
    }

    private sealed class Endpoint : IPlazaFederationEndpoint
    {
        internal ConcurrentQueue<PlazaFederationEvent> Changes { get; } = new();
        private readonly SemaphoreSlim _arrived = new(0);
        public string ClientBuild => "kr-1256";

        public IDisposable Bind(IPlazaFederationLink link) => new Binding(link);

        public ValueTask ApplyAsync(PlazaFederationBatch batch, CancellationToken cancellationToken)
        {
            foreach (var change in batch.Events) Changes.Enqueue(change);
            _arrived.Release();
            return ValueTask.CompletedTask;
        }

        internal async Task<PlazaFederationEvent> WaitFor(Func<PlazaFederationEvent, bool> match, CancellationToken ct)
        {
            while (true)
            {
                if (Changes.FirstOrDefault(match) is { } found) return found;
                await _arrived.WaitAsync(ct);
            }
        }
    }

    private sealed class Binding(IPlazaFederationLink link) : IDisposable
    {
        internal IPlazaFederationLink Link => link;
        public void Dispose() { }
    }
}
