using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class MessageOverridePluginTests
{
    public sealed record Ping : IMessage;
    private const string BaseTarget = "base:BubbleFighter.Fantasy.Kernel.Tests.MessageOverridePluginTests+Ping";
    private const string MiddleTarget = "middle:BubbleFighter.Fantasy.Kernel.Tests.MessageOverridePluginTests+Ping";
    private const string Protocol = "test/game";
    private static readonly ConcurrentDictionary<string, IMessageRouter> Captured = new();

    [Plugin("message-test.capture", Requires = [typeof(IMessageRouter)])]
    public sealed class Capture : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Captured[context.Row.Id] = context.Services.Get<IMessageRouter>();
            context.Effect(() => Captured.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("message-test.base", Requires = [typeof(IMessageRouter)])]
    public sealed class Base : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMessageRouter>().For(context).On<Ping>(Protocol,
                (s, _, ct) => s.SendAsync(1, ReadOnlyMemory<byte>.Empty, ct)));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("message-test.override", Requires = [typeof(IMessageRouter)], Overrides = [BaseTarget])]
    public sealed class Replacement : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMessageRouter>().For(context).Override<Ping>(Protocol, "base",
                (s, _, ct) => s.SendAsync(2, ReadOnlyMemory<byte>.Empty, ct)));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("message-test.outer", Requires = [typeof(IMessageRouter)], Overrides = [MiddleTarget])]
    public sealed class Outer : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMessageRouter>().For(context).Override<Ping>(Protocol, "middle",
                (s, _, ct) => s.SendAsync(3, ReadOnlyMemory<byte>.Empty, ct)));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("message-test.missing", Requires = [typeof(IMessageRouter)], Overrides = [BaseTarget])]
    public sealed class Missing : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMessageRouter>().For(context).On<Ping>("temporary",
                (_, _, _) => ValueTask.CompletedTask));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("message-test.undeclared", Requires = [typeof(IMessageRouter)])]
    public sealed class Undeclared : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMessageRouter>().For(context).Override<Ping>(Protocol, "base",
                (_, _, _) => ValueTask.CompletedTask));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReverseOrderedOverridesProtectTargetsRestoreHandlersAndReloadInOrder()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host(id, [Row("outer", "message-test.outer"), Row("middle", "message-test.override"), Row("base", "message-test.base")]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var router = Captured[id];
            await AssertReply(router, 3);
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("base", default).AsTask());
            Assert.Contains("middle", blocked.Message);
            Assert.Equal(new[] { "base", "middle", "outer" }, (await host.ReloadAsync("base", default)).ReloadedRowIds);
            Assert.Same(router, Captured[id]);
            await AssertReply(router, 3);
            await host.UnloadAsync("outer", default);
            await AssertReply(router, 2);
            await host.UnloadAsync("middle", default);
            await AssertReply(router, 1);
            await host.LoadAsync("middle", default);
            await AssertReply(router, 2);
        }
        finally { await host.ShutdownAsync(default); }
        Assert.False(Captured.ContainsKey(id));
    }

    [Theory]
    [InlineData("message-test.missing")]
    [InlineData("message-test.undeclared")]
    public async Task DeclarationMismatchRollsBackEffectsWithoutRemovingBase(string plugin)
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host(id, [Row("base", "message-test.base"), Row("invalid", plugin) with { Disabled = true }]);
        try
        {
            await host.StartAsync(default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("invalid", default).AsTask());
            Assert.Contains("Overrides", error.Message);
            Assert.Single(Captured[id].Handlers);
            await AssertReply(Captured[id], 1);
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Fact]
    public async Task ConflictingOverrideFailsWithoutDisplacingSelectedHandler()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host(id, [Row("base", "message-test.base"), Row("middle", "message-test.override"),
            Row("conflict", "message-test.override") with { Disabled = true }]);
        try
        {
            await host.StartAsync(default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("conflict", default).AsTask());
            Assert.Contains("currently selected", error.Message);
            Assert.Equal(2, Captured[id].Handlers.Count);
            await AssertReply(Captured[id], 2);
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTargetOrCycleFailsAndUnwindsStartup(bool cycle)
    {
        var id = Guid.NewGuid().ToString("N");
        PluginRow[] rows = cycle
            ? [Row("base", "message-test.outer"), Row("middle", "message-test.override")]
            : [Row("middle", "message-test.override")];
        var host = Host(id, rows);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default).AsTask());
        Assert.Contains("override target row(s)", error.Message);
        Assert.Contains("base", error.Message);
        Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
        Assert.False(Captured.ContainsKey(id));
    }

    private static async Task AssertReply(IMessageRouter router, ushort reply)
    {
        var session = new MessageRouterTests.Session();
        Assert.True(await router.DispatchAsync(Protocol, session, new Ping(), default));
        Assert.Equal(reply, Assert.Single(session.Replies));
    }

    private static PluginHost Host(string id, PluginRow[] rows) => new(
        [.. rows, Row(id, "message-test.capture"), Row("router", "core.message-router")],
        PluginCatalog.ScanAssemblies([typeof(MessageOverridePluginTests).Assembly, typeof(PluginHost).Assembly]), NullLoggerFactory.Instance);
    private static PluginRow Row(string id, string plugin) => new() { Id = id, Plugin = plugin, Source = "test" };
}
