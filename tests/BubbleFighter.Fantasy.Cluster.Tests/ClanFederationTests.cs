using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Checks which nodes relay clan notices to each other through the directory, and the relay link over HTTP.</summary>
public sealed class ClanFederationTests
{
    private const string Domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task NodesOfOneVerifiedDomainHearEachOthersNoticesButFriendlyServersAndTheSenderDoNot()
    {
        var (directory, leases) = Create();
        var b = await Read(directory, leases, "b", "", 0);
        Assert.Empty(b.Notices);
        var friendly = await Read(directory, leases, "friendly", "", 0);
        var own = await Read(directory, leases, "a", "", 0);
        Assert.Equal(204, Publish(directory, leases, "a", Chat(7), Chat(8)));

        var next = await Read(directory, leases, "b", b.Hub, b.Cursor);
        Assert.Equal([7L, 8L], next.Notices.Select(notice => notice.Recipients.Single()));
        Assert.Empty((await Read(directory, leases, "a", own.Hub, own.Cursor)).Notices);
        Assert.Empty((await Read(directory, leases, "friendly", friendly.Hub, friendly.Cursor)).Notices);
        Assert.Empty((await Read(directory, leases, "b", next.Hub, next.Cursor)).Notices);
    }

    [Fact]
    public async Task ANewOrLostReaderSkipsAheadInsteadOfReceivingOldNotices()
    {
        var (directory, leases) = Create();
        Assert.Equal(204, Publish(directory, leases, "a", Chat(7)));
        var fresh = await Read(directory, leases, "b", "", 0);
        Assert.Empty(fresh.Notices);
        Assert.Empty((await Read(directory, leases, "b", "another-directory", 0)).Notices);
        Assert.Equal(204, Publish(directory, leases, "a", Chat(9)));
        Assert.Equal(9L, Assert.Single((await Read(directory, leases, "b", fresh.Hub, fresh.Cursor)).Notices).Recipients.Single());
    }

    [Fact]
    public async Task AReadCarriesABoundedNumberOfNoticesAndTheCursorResumesAfterThem()
    {
        var (directory, leases) = Create();
        var start = await Read(directory, leases, "b", "", 0);
        var many = Enumerable.Range(1, ServerDirectory.ClanReadLimit + 10).Select(user => Chat(user)).ToArray();
        Assert.Equal(204, Publish(directory, leases, "a", many));
        var first = await Read(directory, leases, "b", start.Hub, start.Cursor);
        Assert.Equal(ServerDirectory.ClanReadLimit, first.Notices.Length);
        var rest = await Read(directory, leases, "b", first.Hub, first.Cursor);
        Assert.Equal(Enumerable.Range(ServerDirectory.ClanReadLimit + 1, 10).Select(user => (long)user), rest.Notices.Select(notice => notice.Recipients.Single()));
    }

    [Fact]
    public async Task MalformedNoticesAStaleLeaseAndADisabledDirectoryAreRefusedAndALongPollWakes()
    {
        var (directory, leases) = Create();
        Assert.Equal(400, Publish(directory, leases, "a", Chat(7) with { Recipients = [] }));
        Assert.Equal(400, Publish(directory, leases, "a", Chat(7) with { Recipients = [0] }));
        Assert.Equal(400, Publish(directory, leases, "a", Chat(7) with { Kind = "Clan Chat" }));
        Assert.Equal(400, Publish(directory, leases, "a", Chat(7) with { Body = new string('x', ClanTransport.MaxBodyBytes + 1) }));
        Assert.Equal(400, Publish(directory, leases, "a", Chat(7) with { Recipients = [.. Enumerable.Range(1, ClanTransport.MaxRecipients + 1).Select(user => (long)user)] }));
        Assert.Equal(409, directory.PublishClan("a", "stale", leases["a"].Epoch, new([Chat(7)])));
        Assert.Equal(409, Publish(directory, leases, "unproven", Chat(7)));

        var start = await Read(directory, leases, "b", "", 0);
        var waiting = directory.ReadClanAsync("b", leases["b"].Token, leases["b"].Epoch, start.Hub, start.Cursor,
            TimeSpan.FromSeconds(10), default).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(204, Publish(directory, leases, "a", Chat(7)));
        var (status, batch) = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, status);
        Assert.Single(batch!.Notices);

        var off = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []));
        off.ClanFederation = false;
        var disabled = new ServerDirectory(off);
        Assert.Equal(404, disabled.PublishClan("a", "token", 1, new([Chat(7)])));
    }

    [Fact]
    public async Task ANoticePublishedOnOneNodeIsDeliveredOnTheOtherOverHttp()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []));
        options.ClanFederation = true;
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
            // A reader starts at the newest notice, so the sender repeats until the other node's first read is done.
            while (b.Endpoint.Notices.IsEmpty)
            {
                a.Client.Publish(new("ClanChatReceived", [7], "{\"nickname\":\"Ten\",\"text\":\"hi\"}"));
                await Task.Delay(100, stop.Token);
            }
            Assert.True(b.Endpoint.Notices.TryPeek(out var notice));
            Assert.Equal(("ClanChatReceived", 7L), (notice.Kind, notice.Recipients.Single()));
            Assert.Equal("{\"nickname\":\"Ten\",\"text\":\"hi\"}", notice.Body);
            Assert.True(a.Endpoint.Notices.IsEmpty);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runs);
            a.Client.Dispose();
            b.Client.Dispose();
        }
    }

    private static ClanWireNotice Chat(long user) => new("ClanChatReceived", [user], "{\"nickname\":\"Ten\",\"text\":\"hi\"}");

    private static int Publish(ServerDirectory directory, Dictionary<string, LeaseGrant> leases, string node, params ClanWireNotice[] notices) =>
        directory.PublishClan(node, leases[node].Token, leases[node].Epoch, new(notices));

    private static async Task<ClanWireBatch> Read(ServerDirectory directory, Dictionary<string, LeaseGrant> leases, string node, string hub, long after)
    {
        var (status, batch) = await directory.ReadClanAsync(node, leases[node].Token, leases[node].Epoch, hub, after, TimeSpan.Zero, default);
        Assert.Equal(200, status);
        return batch!;
    }

    private static (ServerDirectory Directory, Dictionary<string, LeaseGrant> Leases) Create()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", []), ServerDirectoryTests.Node("b", "formal", []),
            ServerDirectoryTests.Node("friendly", "friendly", []), ServerDirectoryTests.Node("unproven", "formal2", []));
        options.ClanFederation = true;
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        var leases = new Dictionary<string, LeaseGrant>();
        foreach (var node in new[] { "a", "b", "friendly", "unproven" })
        {
            leases[node] = Assert.IsType<LeaseGrant>(directory.Register(node, Guid.NewGuid().ToString("N")));
            // The unproven node reports no database identity, so its lease proves no domain.
            if (node != "unproven")
                Assert.True(directory.Observe(node, new(leases[node].Token, leases[node].Epoch, 1, false, Domain, [new("kr-1256", true, 1)], [])));
        }
        return (directory, leases);
    }

    private static (ClanFederationClient Client, Endpoint Endpoint) Node(ServerDirectory directory, string url, string id)
    {
        var grant = Assert.IsType<LeaseGrant>(directory.Register(id, Guid.NewGuid().ToString("N")));
        Assert.True(directory.Observe(id, new(grant.Token, grant.Epoch, 1, false, Domain, [new("kr-1256", true, 1)], [])));
        var source = new NodeLeaseSource(id, url);
        source.Publish(grant);
        var options = new ClanFederationOptions
        {
            NodeId = id, DirectoryUrl = url, AccessKey = new string('x', 32) + id,
            FlushIntervalMs = 20, PollWaitSeconds = 1, HttpTimeoutMs = 5000, RetryDelayMs = 100,
        };
        Assert.Empty(options.Validate());
        var endpoint = new Endpoint();
        var http = new HttpClient { BaseAddress = new(url), Timeout = Timeout.InfiniteTimeSpan };
        return (new(options, source, http, endpoint, NullLogger.Instance), endpoint);
    }

    private sealed class Endpoint : IClanRelayEndpoint
    {
        internal ConcurrentQueue<ClanRelayNotice> Notices { get; } = new();

        public IDisposable Bind(IClanRelayLink link) => throw new NotSupportedException();

        public ValueTask DeliverAsync(IReadOnlyList<ClanRelayNotice> notices, CancellationToken cancellationToken)
        {
            foreach (var notice in notices) Notices.Enqueue(notice);
            return ValueTask.CompletedTask;
        }
    }
}
