using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks chat admission across routes, independent connections, concurrent sends and row lifetime.</summary>
public sealed class ChatRateLimitTests
{
    /// <summary>The mounted row limits routed chat and releases its middleware and connection accounting on unload.</summary>
    [Fact]
    public async Task MountedRowLimitsChatAndReclaimsClosedConnections()
    {
        const string protocol = "kr-812/game";
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = new PluginContext(new PluginRow { Id = "root", Plugin = "test", Source = "test" },
            services, events, NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            root.Effect(services.RegisterMany<IInboundMessageBinding<RoomChatSent>>(new Binding<RoomChatSent>(protocol)));
            root.Effect(services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new Binding<RoomCreateRequested>(protocol)));
            root.Effect(services.RegisterMany<IMessageSender<RoomEntered>>(new UnusedSender<RoomEntered>(protocol)));
            root.Effect(services.RegisterMany<IMessageSender<RoomSeatUpdated>>(new UnusedSender<RoomSeatUpdated>(protocol)));
            root.Effect(services.RegisterMany<IMessageSender<RoomSeatVacated>>(new UnusedSender<RoomSeatVacated>(protocol)));
            root.Effect(services.RegisterMany<IMessageSender<RoomMasterChanged>>(new UnusedSender<RoomMasterChanged>(protocol)));
            var sent = new ChatSender(protocol);
            root.Effect(services.RegisterMany<IMessageSender<RoomChatReceived>>(sent));
            var ingame = new PluginContext(new PluginRow
            {
                Id = "chat", Plugin = "ingame", Source = "test", Config = new Dictionary<string, object?>
                {
                    ["typed_only"] = true, ["game_protocol"] = protocol,
                    ["chat_rate_limit_messages"] = 2, ["chat_rate_limit_window_ms"] = 3600000,
                    ["chat_rate_limit_max_sessions"] = 1,
                },
            }, services, events, NullLogger.Instance);
            try
            {
                await new InGamePlugin().ActivateAsync(ingame, default);
                var router = services.Get<IMessageRouter>();
                var first = new RoomPushTests.RecordingSession(1);
                first.SetLobbyPlayer(11, "first");
                for (var i = 0; i < 3; i++)
                    Assert.True(await router.DispatchAsync(protocol, first, new RoomChatSent(0, "hello"), default));
                Assert.Equal(2, sent.Count);
                await first.Connection.CloseAsync("test");
                events.Emit(new ConnectionClosedEvent(first.Connection, "test"));
                var second = new RoomPushTests.RecordingSession(2);
                second.SetLobbyPlayer(22, "second");
                Assert.True(await router.DispatchAsync(protocol, second, new RoomChatSent(0, "hello"), default));
                Assert.Equal(3, sent.Count);
                var replacement = new PluginContext(new PluginRow { Id = "replacement", Plugin = "test", Source = "test" },
                    services, events, NullLogger.Instance, [$"chat:{typeof(RoomChatSent).FullName}"]);
                try
                {
                    var replacedCalls = 0;
                    replacement.Effect(router.For(replacement).Override<RoomChatSent>(protocol, "chat",
                        (_, _, _) => { replacedCalls++; return ValueTask.CompletedTask; }));
                    for (var i = 0; i < 2; i++)
                        Assert.True(await router.DispatchAsync(protocol, second, new RoomChatSent(0, "override"), default));
                    Assert.Equal(1, replacedCalls);
                    Assert.Equal(3, sent.Count);
                }
                finally { await replacement.UnwindAsync(); }
            }
            finally { await ingame.UnwindAsync(); }
            Assert.DoesNotContain(services.Get<IMessageRouter>().Handlers, handler => handler.OwnerRowId == "chat");
        }
        finally { await root.UnwindAsync(); }
    }

    /// <summary>Zero and excessive row settings are rejected by configuration validation.</summary>
    [Theory]
    [InlineData(nameof(InGameOptions.ChatRateLimitMessages), 0)]
    [InlineData(nameof(InGameOptions.ChatRateLimitWindowMs), 0)]
    [InlineData(nameof(InGameOptions.ChatRateLimitMaxSessions), 0)]
    [InlineData(nameof(InGameOptions.ChatRateLimitMaxSessions), 65537)]
    public void InvalidSettingsFailValidation(string property, int value)
    {
        var options = Options();
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(options) { MemberName = property };
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateProperty(value, context, []));
    }

    /// <summary>Raw and typed requests cannot obtain separate budgets by alternating entry points.</summary>
    [Fact]
    public async Task RawAndTypedRequestsShareAWindowAndRecoverAtItsBoundary()
    {
        var clock = new ManualClock();
        using var limit = new ChatRateLimit(Options(), NullLogger.Instance, clock);
        var session = new RoomPushTests.RecordingSession(1);
        var calls = 0;
        var raw = limit.Wrap((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        var typed = limit.Wrap<RoomChatSent>((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        await raw(session, new InboundPacket(1, ReadOnlyMemory<byte>.Empty), default);
        await typed(session, new RoomChatSent(0, "line"), default);
        await raw(session, new InboundPacket(1, ReadOnlyMemory<byte>.Empty), default);
        Assert.Equal(2, calls);
        clock.Now = 99;
        await typed(session, new RoomChatSent(0, "line"), default);
        Assert.Equal(2, calls);
        clock.Now = 100;
        await typed(session, new RoomChatSent(0, "line"), default);
        Assert.Equal(3, calls);
    }

    /// <summary>One flood is capped atomically and produces only one warning in its accounting window.</summary>
    [Fact]
    public async Task ConcurrentFloodIsBoundedWithoutAnOveruseLogStorm()
    {
        var logger = new WarningCounter();
        using var limit = new ChatRateLimit(Options(), logger, new ManualClock());
        var session = new RoomPushTests.RecordingSession(1);
        var calls = 0;
        var handler = limit.Wrap<RoomChatSent>((_, _, _) => { Interlocked.Increment(ref calls); return ValueTask.CompletedTask; });
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            handler(session, new RoomChatSent(0, "line"), default).AsTask())));
        Assert.Equal(2, calls);
        Assert.Equal(1, logger.Warnings);
    }

    /// <summary>Closed connections reclaim the configured capacity, and a stale callback cannot recreate their entry.</summary>
    [Fact]
    public async Task ConnectionCloseReclaimsCapacityAndPreventsStaleAdmission()
    {
        var options = Options();
        options.ChatRateLimitMaxSessions = 1;
        using var limit = new ChatRateLimit(options, NullLogger.Instance);
        var first = new RoomPushTests.RecordingSession(1);
        var second = new RoomPushTests.RecordingSession(2);
        var calls = 0;
        var handler = limit.Wrap<RoomChatSent>((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        await handler(first, new RoomChatSent(0, "line"), default);
        Assert.Throws<InvalidOperationException>(() => { _ = handler(second, new RoomChatSent(0, "line"), default); });
        await first.Connection.CloseAsync("test");
        limit.OnConnectionClosed(new ConnectionClosedEvent(first.Connection, "test"));
        await handler(first, new RoomChatSent(0, "stale"), default);
        await handler(second, new RoomChatSent(0, "line"), default);
        Assert.Equal(2, calls);
    }

    /// <summary>Independent connections receive independent budgets.</summary>
    [Fact]
    public async Task OneConnectionCannotSpendAnothersBudget()
    {
        using var limit = new ChatRateLimit(Options(), NullLogger.Instance);
        var calls = 0;
        var handler = limit.Wrap<RoomChatSent>((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        var first = new RoomPushTests.RecordingSession(1);
        var second = new RoomPushTests.RecordingSession(2);
        for (var i = 0; i < 3; i++) await handler(first, new RoomChatSent(0, "line"), default);
        await handler(second, new RoomChatSent(0, "line"), default);
        Assert.Equal(3, calls);
    }

    /// <summary>Middleware applies the same budget to whichever typed base or override handler is selected.</summary>
    [Fact]
    public async Task MiddlewareLimitsReplacementHandlersBeforeTheyExecute()
    {
        using var limit = new ChatRateLimit(Options(), NullLogger.Instance);
        var session = new RoomPushTests.RecordingSession(1);
        var calls = 0;
        MessageHandler<RoomChatSent> original = (_, _, _) => { calls++; return ValueTask.CompletedTask; };
        MessageHandler<RoomChatSent> replacement = (_, _, _) => { calls += 10; return ValueTask.CompletedTask; };
        await limit.Filter(session, new RoomChatSent(0, "line"), original, default);
        await limit.Filter(session, new RoomChatSent(0, "line"), replacement, default);
        await limit.Filter(session, new RoomChatSent(0, "line"), replacement, default);
        Assert.Equal(11, calls);
    }

    /// <summary>Already canceled requests consume no budget; disposal ends admission rather than leaving live wrappers.</summary>
    [Fact]
    public async Task CancellationAndUnloadDoNotLeaveAdmissionActive()
    {
        var limit = new ChatRateLimit(Options(), NullLogger.Instance);
        var session = new RoomPushTests.RecordingSession(1);
        var calls = 0;
        var handler = limit.Wrap<RoomChatSent>((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        Assert.Throws<OperationCanceledException>(() => { _ = handler(session, new RoomChatSent(0, "line"), new CancellationToken(true)); });
        for (var i = 0; i < 2; i++) await handler(session, new RoomChatSent(0, "line"), default);
        Assert.Equal(2, calls);
        limit.Dispose();
        limit.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = handler(session, new RoomChatSent(0, "line"), default); });
    }

    /// <summary>The configuration switch preserves unrestricted dispatch without allocating connection entries.</summary>
    [Fact]
    public async Task DisabledPolicyDoesNotEnforceBudgetOrConnectionCapacity()
    {
        var options = Options();
        options.ChatRateLimitEnabled = false;
        options.ChatRateLimitMaxSessions = 1;
        using var limit = new ChatRateLimit(options, NullLogger.Instance);
        var calls = 0;
        var handler = limit.Wrap<RoomChatSent>((_, _, _) => { calls++; return ValueTask.CompletedTask; });
        for (var i = 0; i < 10; i++)
            await handler(new RoomPushTests.RecordingSession(i), new RoomChatSent(0, "line"), default);
        Assert.Equal(10, calls);
    }

    private static InGameOptions Options() => new()
    {
        ChatRateLimitMessages = 2, ChatRateLimitWindowMs = 100, ChatRateLimitMaxSessions = 2,
    };

    private sealed class ManualClock : TimeProvider
    {
        internal long Now;
        /// <inheritdoc />
        public override long TimestampFrequency => 1000;
        /// <inheritdoc />
        public override long GetTimestamp() => Now;
    }

    private sealed class Binding<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => protocol;
    }

    private sealed class UnusedSender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ChatSender(string protocol) : IMessageSender<RoomChatReceived>
    {
        internal int Count;
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, RoomChatReceived message, CancellationToken ct)
        {
            Count++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WarningCounter : ILogger
    {
        internal int Warnings;
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Warnings);
        }
    }
}
