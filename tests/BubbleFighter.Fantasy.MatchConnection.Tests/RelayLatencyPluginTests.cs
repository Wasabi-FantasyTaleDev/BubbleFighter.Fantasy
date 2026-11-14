using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Exercises scoped passive replies, required service cardinality and cancellation during row teardown.</summary>
public sealed class RelayLatencyPluginTests
{
    /// <summary>Routes each repeated probe through its own protocol owner and removes only the unloaded row.</summary>
    [Fact]
    public async Task FiveProtocolsSendOncePerRequestThroughTheirOwnOperationOwner()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var builds = new[] { "kr-1256", "cn-738", "cn-742", "cn-774", "tw-454" };
        var installed = new List<(Operations Owner, Replies Sender, PluginContext Context)>();
        foreach (var build in builds)
        {
            var protocol = build + "/relay";
            var owner = new Operations(protocol);
            var sender = new Replies(protocol);
            fixture.Publish<IMatchConnectionOperations>(owner);
            fixture.Publish<IInboundMessageBinding<RelayLatencyRequested>>(new Binding(protocol));
            fixture.Publish<IMessageSender<RelayLatencyAcknowledged>>(sender);
            var context = fixture.Row(protocol, new() { ["reply_word"] = uint.MaxValue });
            context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
            await new RelayLatencyPlugin().ActivateAsync(context, default);
            installed.Add((owner, sender, context));
        }
        foreach (var (owner, sender, _) in installed)
        {
            Assert.Empty(sender.Sent);
            var session = new Session(installed.FindIndex(item => item.Owner == owner) + 1);
            Assert.False(await fixture.Router.DispatchAsync(owner.Protocol.Replace("/relay", "/game", StringComparison.Ordinal),
                session, new RelayLatencyRequested(0), default));
            for (var repeat = 0; repeat < 2; repeat++)
                Assert.True(await fixture.Router.DispatchAsync(owner.Protocol, session, new RelayLatencyRequested(0x80000000), default));
            Assert.Equal(2, owner.Calls);
            Assert.Equal(2, sender.Sent.Count);
            Assert.All(sender.Sent, item =>
            {
                Assert.Same(session, item.Session);
                Assert.Equal(uint.MaxValue, item.Message.Unknown4);
            });
        }
        await installed[0].Context.UnwindAsync();
        Assert.DoesNotContain(fixture.Router.Handlers, handler => handler.Protocol == installed[0].Owner.Protocol);
        Assert.Equal(4, fixture.Router.Handlers.Count);
    }

    /// <summary>Refuses missing, duplicate or differently scoped operation owners, bindings and reply senders.</summary>
    [Theory]
    [InlineData("operation", 0)]
    [InlineData("operation", 2)]
    [InlineData("binding", 0)]
    [InlineData("binding", 2)]
    [InlineData("sender", 0)]
    [InlineData("sender", 2)]
    [InlineData("wrong-scope", 1)]
    public async Task MissingAmbiguousOrWrongScopeServicesCannotInstallARoute(string fault, int count)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        const string protocol = "kr-1256/relay";
        var publishedScope = fault == "wrong-scope" ? "cn-774/relay" : protocol;
        for (var i = 0; i < (fault == "operation" ? count : 1); i++)
            fixture.Publish<IMatchConnectionOperations>(new Operations(publishedScope));
        for (var i = 0; i < (fault == "binding" ? count : 1); i++)
            fixture.Publish<IInboundMessageBinding<RelayLatencyRequested>>(new Binding(publishedScope));
        for (var i = 0; i < (fault == "sender" ? count : 1); i++)
            fixture.Publish<IMessageSender<RelayLatencyAcknowledged>>(new Replies(publishedScope));
        var context = fixture.Row(protocol);
        var error = await Record.ExceptionAsync(async () =>
        {
            context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
            await new RelayLatencyPlugin().ActivateAsync(context, default);
        });
        Assert.NotNull(error);
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Rejects absent build identities and non-relay protocol scopes before registering a handler.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relay")]
    [InlineData("kr-1256/game")]
    public async Task InvalidProtocolIsRejected(string? protocol)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var context = fixture.Row(protocol);
        var error = await Assert.ThrowsAsync<ConfigException>(() => new RelayLatencyPlugin().ActivateAsync(context, default).AsTask());
        Assert.Contains("protocol", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Rejects negative, overflowing and fractional response words without installing a route.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("1.5")]
    public async Task ReplyWordMustFitAnUnsignedWord(string value)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        const string protocol = "kr-1256/relay";
        fixture.Publish<IMatchConnectionOperations>(new Operations(protocol));
        fixture.Publish<IInboundMessageBinding<RelayLatencyRequested>>(new Binding(protocol));
        fixture.Publish<IMessageSender<RelayLatencyAcknowledged>>(new Replies(protocol));
        var context = fixture.Row(protocol, new() { ["reply_word"] = value });
        context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
        Assert.NotNull(await Record.ExceptionAsync(() => new RelayLatencyPlugin().ActivateAsync(context, default).AsTask()));
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Unloads an awaiting route with cancellation before its delayed callback can publish a reply.</summary>
    [Fact]
    public async Task RowUnloadCancelsAnAwaitedOperationBeforeItsReply()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Operations("kr-1256/relay")
        {
            BeforeOperation = async ct =>
            {
                entered.TrySetResult();
                await resume.Task.WaitAsync(ct);
            },
        };
        var sender = new Replies(owner.Protocol);
        fixture.Publish<IMatchConnectionOperations>(owner);
        fixture.Publish<IInboundMessageBinding<RelayLatencyRequested>>(new Binding(owner.Protocol));
        fixture.Publish<IMessageSender<RelayLatencyAcknowledged>>(sender);
        var context = fixture.Row(owner.Protocol);
        context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
        await new RelayLatencyPlugin().ActivateAsync(context, default);
        var pending = fixture.Router.DispatchAsync(owner.Protocol, new Session(1), new RelayLatencyRequested(7), default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await context.UnwindAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(sender.Sent);
        Assert.Empty(fixture.Router.Handlers);
    }

    /// <summary>Uses the configured zero default and propagates one failed send without replaying the request.</summary>
    [Fact]
    public async Task DefaultOpaqueWordIsZeroAndFailedPublicationIsNotRetried()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var owner = new Operations("kr-1256/relay");
        var sender = new Replies(owner.Protocol);
        fixture.Publish<IMatchConnectionOperations>(owner);
        fixture.Publish<IInboundMessageBinding<RelayLatencyRequested>>(new Binding(owner.Protocol));
        fixture.Publish<IMessageSender<RelayLatencyAcknowledged>>(sender);
        var context = fixture.Row(owner.Protocol);
        context.BindServices(typeof(RelayLatencyPlugin).GetCustomAttribute<PluginAttribute>()!);
        await new RelayLatencyPlugin().ActivateAsync(context, default);
        var session = new Session(1);
        Assert.True(await fixture.Router.DispatchAsync(owner.Protocol, session, new RelayLatencyRequested(123), default));
        Assert.Equal(0u, Assert.Single(sender.Sent).Message.Unknown4);
        sender.FailSend = true;
        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Router.DispatchAsync(owner.Protocol, session, new RelayLatencyRequested(456), default).AsTask());
        Assert.Equal(2, owner.Calls);
        Assert.Equal(2, sender.Attempts);
        Assert.Single(sender.Sent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        private EventBus Events { get; } = new(NullLogger.Instance);
        internal IMessageRouter Router => Services.Get<IMessageRouter>();
        private readonly List<PluginContext> _contexts = [];
        internal async Task InitializeAsync()
        {
            var root = Row("root");
            var bootstrap = new PluginContext(new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" },
                Services, Events, NullLogger.Instance);
            IMessageRouter router;
            try
            {
                await new MessageRouterPlugin().ActivateAsync(bootstrap, default);
                router = bootstrap.Services.Get<IMessageRouter>();
            }
            finally { await bootstrap.UnwindAsync(); }
            root.Effect(Services.Register<IMessageRouter>(router));
        }
        internal void Publish<T>(T service) where T : class =>
            _contexts[0].Effect(Services.RegisterMany<T>(service));
        internal PluginContext Row(string? protocol, Dictionary<string, object?>? extra = null)
        {
            var config = new Dictionary<string, object?> { ["protocol"] = protocol };
            foreach (var (key, value) in extra ?? []) config[key] = value;
            var context = new PluginContext(new PluginRow
            {
                Id = "latency-" + _contexts.Count, Plugin = "match-latency", Source = "test", Config = config,
            }, Services, Events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }
        /// <summary>Unwinds mounted test rows in reverse order so pending route operations are canceled.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
    }

    private sealed class Binding(string protocol) : IInboundMessageBinding<RelayLatencyRequested>
    {
        /// <summary>Gets the relay scope advertised by this request-binding fixture.</summary>
        public string Protocol => protocol;
    }

    private sealed class Operations(string protocol) : IMatchConnectionOperations
    {
        /// <summary>Gets the relay scope whose callback admission this fixture records.</summary>
        public string Protocol => protocol;
        internal int Calls { get; private set; }
        internal Func<CancellationToken, ValueTask>? BeforeOperation { get; init; }
        /// <summary>Counts each admission and waits for the configured barrier before invoking the cancellable callback.</summary>
        public async ValueTask RunAuthenticatedAsync(ISession session, Func<CancellationToken, ValueTask> operation, CancellationToken ct)
        {
            Calls++;
            if (BeforeOperation is { } before) await before(ct);
            ct.ThrowIfCancellationRequested();
            await operation(ct);
        }
    }

    private sealed class Replies(string protocol) : IMessageSender<RelayLatencyAcknowledged>
    {
        /// <summary>Gets the relay scope selected for recorded acknowledgement messages.</summary>
        public string Protocol => protocol;
        internal bool FailSend { get; set; }
        internal int Attempts { get; private set; }
        internal List<(ISession Session, RelayLatencyAcknowledged Message)> Sent { get; } = [];
        /// <summary>Records one typed acknowledgement or propagates the configured send failure without retrying.</summary>
        public ValueTask SendAsync(ISession session, RelayLatencyAcknowledged message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Attempts++;
            if (FailSend) throw new IOException("send queue full");
            Sent.Add((session, message));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long id) : ISession
    {
        /// <summary>Gets the synthetic recipient identity used to check per-request delivery.</summary>
        public long Id => id;
        /// <summary>Gets the in-memory close-state transport paired with this recipient.</summary>
        public IConnection Connection { get; } = new Connection(id);
        /// <summary>Gets the fixed KR1256 profile used by these typed routing fixtures.</summary>
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        /// <summary>Gets per-session state available to the message router.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Rejects raw packet sends because the reply fixture records typed acknowledgements.</summary>
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        /// <summary>Gets the transport identity paired with the fixture session.</summary>
        public long Id => id;
        /// <summary>Gets a loopback placeholder without opening a socket.</summary>
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        /// <summary>Rejects byte-stream input because these tests dispatch typed requests directly.</summary>
        public PipeReader Input => throw new NotSupportedException();
        /// <summary>Rejects byte-stream output because the reply fixture records typed messages.</summary>
        public PipeWriter Output => throw new NotSupportedException();
        /// <summary>Gets transport-local state retained for lifecycle contracts.</summary>
        public ConcurrentDictionary<string, object> Items { get; } = new();
        /// <summary>Gets whether fixture cleanup has closed this transport.</summary>
        public bool IsClosed { get; private set; }
        /// <summary>Records transport closure without performing network I/O.</summary>
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
