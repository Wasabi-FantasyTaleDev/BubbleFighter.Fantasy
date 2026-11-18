using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Exercises authenticated audience selection, missing data and the boundary between concurrent accounts.</summary>
public sealed class PirateAssaultRankingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheLobbyMountsOnlyAnExplicitBindingWithAnAuthoritativeSource(bool bind, bool hasSource)
    {
        const string protocol = "pirate-test/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var login = services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Wire<LoginRequested>());
        using var accepted = services.RegisterMany<IMessageSender<LoginSucceeded>>(new Wire<LoginSucceeded>());
        using var rejected = services.RegisterMany<IMessageSender<LoginRejected>>(new Wire<LoginRejected>());
        using var request = bind ? services.RegisterMany<IInboundMessageBinding<PirateAssaultRankingRequested>>(new Wire<PirateAssaultRankingRequested>()) : null;
        using var reply = bind ? services.RegisterMany<IMessageSender<PirateAssaultRankingPage>>(new Wire<PirateAssaultRankingPage>()) : null;
        using var source = hasSource ? services.Register<IPirateAssaultRankingSource>(new Source(_ => null)) : null;
        var router = Context("core.message-router");
        var lobby = Context("lobby", new() { ["protocol"] = protocol });
        try
        {
            await new MessageRouterPlugin().ActivateAsync(router, default);
            if (bind && !hasSource)
                Assert.Contains("IPirateAssaultRankingSource", (await Assert.ThrowsAsync<ConfigException>(async () =>
                    await new LobbyPlugin().ActivateAsync(lobby, default))).Message);
            else
                await new LobbyPlugin().ActivateAsync(lobby, default);
            var messages = services.Get<IMessageRouter>();
            Assert.Equal(bind && hasSource, messages.Handlers.Any(row => row.MessageType == typeof(PirateAssaultRankingRequested)));
            await lobby.UnwindAsync();
            Assert.DoesNotContain(messages.Handlers, row => row.MessageType == typeof(PirateAssaultRankingRequested));
        }
        finally
        {
            await lobby.UnwindAsync();
            await router.UnwindAsync();
        }

        PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "pirate-ranking-test", Config = config }, services, events, NullLogger.Instance);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheSourceDecidesIfTheRequestedPageIsKnown(bool personal, bool known)
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        online.Attach(session, new(17, "self", "Self", 1));
        var replies = new Replies();
        var key = new PirateAssaultRankingKey("cn-774", 1001, personal ? 17 : null);
        var expected = new PirateAssaultRankingPage(key, 45222, personal ? [] : [new(2, "Other", 7, 5, 2468, 3)]);
        var source = new Source(query =>
        {
            Assert.Equal(key, query);
            return known ? expected : null;
        });
        await PirateAssaultRankingHandler.HandleAsync(online, source, replies, session, new("cn-774", 1001, personal), default);
        Assert.Equal(1, source.Reads);
        if (known) Assert.Same(expected, Assert.Single(replies.Pages));
        else Assert.Empty(replies.Pages);
    }

    [Fact]
    public async Task ForeignSnapshotsAndUnauthenticatedQueriesCannotPublishAPage()
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        var replies = new Replies();
        var source = new Source(query => new(query with { UserNum = 9 }, 45222, []));
        var request = new PirateAssaultRankingRequested("tw-454", 1001, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PirateAssaultRankingHandler.HandleAsync(online, source, replies, session, request, default).AsTask());
        Assert.Equal(0, source.Reads);
        online.Attach(session, new(17, "self", "Self", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PirateAssaultRankingHandler.HandleAsync(online, source, replies, session, request, default).AsTask());
        Assert.Equal(1, source.Reads);
        Assert.Empty(replies.Pages);
    }

    [Fact]
    public async Task ConnectionReplacedDuringTheReadGetsNoPersonalReply()
    {
        using var online = new PlayerSessionScope();
        var session = new FeatureSession(1);
        online.Attach(session, new(17, "self", "Self", 1));
        var replies = new Replies();
        var source = new Source(key =>
        {
            online.Remove(session.Connection.Id);
            online.Attach(new FeatureSession(2), new(17, "self", "Self", 1));
            return new(key, 45222, [new(2, "Self", 7, 5, 2468, 3)]);
        });
        await PirateAssaultRankingHandler.HandleAsync(online, source, replies, session, new("kr-1256", 1001, true), default);
        Assert.Equal(1, source.Reads);
        Assert.Empty(replies.Pages);
    }

    private sealed class Source(Func<PirateAssaultRankingKey, PirateAssaultRankingPage?> read) : IPirateAssaultRankingSource
    {
        internal int Reads { get; private set; }
        public ValueTask<PirateAssaultRankingPage?> ReadAsync(PirateAssaultRankingKey key, CancellationToken ct)
        {
            Reads++;
            return ValueTask.FromResult(read(key));
        }
    }

    private sealed class Replies : IMessageSender<PirateAssaultRankingPage>
    {
        public string Protocol => "test/game";
        internal List<PirateAssaultRankingPage> Pages { get; } = [];
        public ValueTask SendAsync(ISession session, PirateAssaultRankingPage message, CancellationToken ct)
        {
            Pages.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Wire<T> : IInboundMessageBinding<T>, IMessageSender<T> where T : IMessage
    {
        public string Protocol => "pirate-test/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FeatureSession(long id) : ISessionWithFeatures
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();
        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
