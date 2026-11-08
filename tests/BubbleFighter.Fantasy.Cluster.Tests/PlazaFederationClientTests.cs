using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Runs two nodes' plaza links against the directory over HTTP.</summary>
public sealed class PlazaFederationClientTests
{
    [Fact]
    public async Task AMemberEnteringOnOneNodeIsShownOnTheOtherAndLeavesWithIt()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        options.PlazaFederation = true;
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var url = server.Addresses.Single();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var a = Node(directory, url, "a");
        var b = Node(directory, url, "b");
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
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runs);
            a.Client.Dispose();
            b.Client.Dispose();
        }
    }

    private static (PlazaFederationClient Client, Endpoint Endpoint) Node(ServerDirectory directory, string url, string id)
    {
        var grant = Assert.IsType<LeaseGrant>(directory.Register(id, Guid.NewGuid().ToString("N")));
        Assert.True(directory.Observe(id, new(grant.Token, grant.Epoch, 1, false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", [new("kr-1256", true, 1)], [])));
        var endpoint = new Endpoint();
        var options = new PlazaFederationOptions
        {
            NodeId = id, DirectoryUrl = url, AccessKey = new string('x', 32) + id, Builds = ["kr-1256"],
            FlushIntervalMs = 20, PollWaitSeconds = 1, HttpTimeoutMs = 5000, RetryDelayMs = 100,
        };
        Assert.Empty(options.Validate());
        var http = new HttpClient { BaseAddress = new(url), Timeout = Timeout.InfiniteTimeSpan };
        return (new(options, new Lease(id, url, grant), http, [endpoint], NullLogger.Instance), endpoint);
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
