using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Commands;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class SharedGameCommandTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Action<IPluginContext>> Plans = new();

    [Plugin("test.shared-game-commands", Requires = [typeof(ICommandExecutor), typeof(ICommandRegistry),
        typeof(IPermissionRegistry), typeof(IGameCommandEndpoints)])]
    public sealed class Adapter : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Plans[context.Row.Id](context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly RoomFlowTests.Rig Rig = new();
        internal readonly string Owner = Guid.NewGuid().ToString("N");
        internal readonly PluginHost Host;
        internal IPluginContext Context = null!;
        internal ICommandExecutor Executor => Context.Services.Get<ICommandExecutor>();

        internal Harness(bool denyMode = false, Action<IPluginContext>? configure = null, bool enabled = true)
        {
            Plans[Owner] = context =>
            {
                Context = context;
                Rig.Flow.BindCommands(context, Owner, enabled);
                configure?.Invoke(context);
            };
            Dictionary<string, object?> policy = denyMode ? new()
            {
                ["subjects"] = new object[] { new Dictionary<string, object?>
                {
                    ["kind"] = "game", ["id"] = "22", ["deny"] = new[] { "game.room.mode" }
                } }
            } : [];
            Host = new PluginHost([
                new() { Id = "permissions", Plugin = "permissions", Source = "test", Config = policy },
                new() { Id = "commands", Plugin = "commands", Source = "test" },
                new() { Id = "commands.game", Plugin = "commands.game", Source = "test" },
                new() { Id = Owner, Plugin = "test.shared-game-commands", Source = "test" }
            ], PluginCatalog.ScanAssemblies([typeof(CommandsPlugin).Assembly, typeof(Adapter).Assembly]), NullLoggerFactory.Instance);
        }

        internal async Task Start() => await Host.StartAsync(default).AsTask().WaitAsync(Limit);

        public async ValueTask DisposeAsync()
        {
            try { await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
            finally { Plans.TryRemove(Owner, out _); await Rig.Registry.StopAllAsync(); }
        }
    }

    [Theory]
    [InlineData("/mode 8")]
    [InlineData("!set 8")]
    [InlineData("！mode 8")]
    public async Task OrdinaryGuestChangesModeThroughEveryChatPrefix(string text)
    {
        await using var h = new Harness();
        await h.Start();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await h.Rig.CreateAsync(host);
        await h.Rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);

        await h.Rig.Flow.HandleChatAsync(guest, new(0, text), default);

        Assert.Equal(8, h.Rig.Registry.Find(roomId)!.ModeId);
        Assert.Single(h.Rig.SettingsChanged.To(host));
        Assert.Single(h.Rig.SettingsChanged.To(guest));
        Assert.Empty(h.Rig.Chat.Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EnablingSharedCommandsRequiresBothServices(int available)
    {
        await using var h = new Harness();
        await h.Start();
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "missing-services", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            if (available == 1) context.Effect(services.Register<ICommandExecutor>(h.Executor));
            if (available == 2) context.Effect(services.Register<IGameCommandEndpoints>(h.Context.Services.Get<IGameCommandEndpoints>()));
            var error = Assert.Throws<ConfigException>(() => h.Rig.Flow.BindCommands(context, "missing/game", enabled: true));
            Assert.Contains("requires both commands and commands.game", error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithdrawingScopeDuringLookupCannotInvokeItsSnapshotOrReplacement(bool replace)
    {
        var player = RoomFlowTests.Rig.Player(33, "player");
        var old = new LookupEndpoint(player);
        var replacement = new LookupEndpoint(player);
        IDisposable? registration = null;
        await using var h = new Harness(configure: context =>
        {
            var endpoints = context.Services.Get<IGameCommandEndpoints>();
            registration = context.Effect(endpoints.Register(context, old));
            old.OnLookup = () =>
            {
                registration.Dispose();
                if (replace) context.Effect(endpoints.Register(context, replacement));
            };
        });
        await h.Start();
        var principal = new CommandPrincipal(PrincipalKind.Game, "33");
        var caller = new CommandCaller(principal, principal, SessionId: "33");

        var result = await h.Executor.ExecuteTextAsync(caller, "/start");

        Assert.Equal(CommandStatus.InvalidState, result.Status);
        Assert.Equal(0, old.Executions);
        Assert.Equal(0, replacement.Executions);
        if (replace)
        {
            Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteTextAsync(caller, "/start")).Status);
            Assert.Equal(1, replacement.Executions);
        }
    }

    private sealed class LookupEndpoint(ISession session) : IGameCommandEndpoint
    {
        internal Action? OnLookup;
        internal int Executions;
        public string Scope => "withdrawn/game";
        public ISession? FindSession(long sessionId)
        {
            if (sessionId != session.Id) return null;
            var callback = OnLookup;
            OnLookup = null;
            callback?.Invoke();
            return session;
        }
        public ValueTask<CommandResult> ExecuteAsync(ISession target, CommandInvocation invocation, CancellationToken cancellationToken)
        {
            Executions++;
            return ValueTask.FromResult(new CommandResult(CommandStatus.Success, ""));
        }
    }

    [Fact]
    public async Task PermissionDenialDoesNotFallBackToLocalModeHandling()
    {
        await using var h = new Harness(denyMode: true);
        await h.Start();
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await h.Rig.CreateAsync(guest);

        await h.Rig.Flow.HandleChatAsync(guest, new(0, "!set 8"), default);

        Assert.Equal(4, h.Rig.Registry.Find(roomId)!.ModeId);
        Assert.Equal("command.result.denied:", Assert.Single(h.Rig.Notices.To(guest)).Text);
        Assert.Empty(h.Rig.SettingsChanged.Sent);
        Assert.Empty(h.Rig.Chat.Sent);
    }

    [Fact]
    public async Task GuestCommandStartsRoomButNativeGuestStartRemainsRefused()
    {
        await using var h = new Harness();
        await h.Start();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await h.Rig.CreateAsync(host);
        await h.Rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);

        await h.Rig.Flow.HandleStartAsync(guest, new RoomStartRequested { Force = true }, default);
        Assert.Equal(RoomPhase.Waiting, h.Rig.Registry.Find(roomId)!.Phase);
        await h.Rig.Flow.HandleChatAsync(guest, new(0, "/start"), default);

        Assert.Equal(RoomPhase.Starting, h.Rig.Registry.Find(roomId)!.Phase);
        Assert.Single(h.Rig.Starting.To(host));
        Assert.Single(h.Rig.Starting.To(guest));
    }

    [Fact]
    public async Task ConfiguredModeCatalogRejectsUnsupportedModeWithoutMutation()
    {
        await using var h = new Harness();
        await h.Start();
        h.Rig.Flow.Modes = new ModeCatalog([4, 8]);
        var player = RoomFlowTests.Rig.Player(11, "player");
        var roomId = await h.Rig.CreateAsync(player);

        await h.Rig.Flow.HandleChatAsync(player, new(0, "/mode 77"), default);

        Assert.Equal(4, h.Rig.Registry.Find(roomId)!.ModeId);
        Assert.Equal("command.set.unsupported:77", Assert.Single(h.Rig.Notices.To(player)).Text);
        Assert.Empty(h.Rig.SettingsChanged.Sent);
    }

    [Fact]
    public async Task RegisteredPluginCommandIsInvokedFromActualRoomChat()
    {
        CommandInvocation? observed = null;
        await using var h = new Harness(configure: context =>
        {
            context.Effect(context.Services.Get<IPermissionRegistry>().Register(new("test.greet", "Greet a player.", true)));
            context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
                new("greet", [], "test.greet", "Greet a player.", []), (invocation, _) =>
                {
                    observed = invocation;
                    return ValueTask.FromResult(new CommandResult(CommandStatus.Success, "hello"));
                }));
        });
        await h.Start();
        var player = RoomFlowTests.Rig.Player(11, "player");
        await h.Rig.CreateAsync(player);

        await h.Rig.Flow.HandleChatAsync(player, new(0, "/greet"), default);

        Assert.NotNull(observed);
        Assert.Equal(new CommandPrincipal(PrincipalKind.Game, "11"), observed.Caller.Subject);
        Assert.Equal("11", observed.Caller.SessionId);
        Assert.Equal("hello", Assert.Single(h.Rig.Notices.To(player)).Text);
        Assert.Empty(h.Rig.Chat.Sent);
    }

    [Fact]
    public async Task SeparateGameplayScopesShareNamesButOnlyChangeTheirOwnRoom()
    {
        var other = new RoomFlowTests.Rig();
        try
        {
            await using var h = new Harness(configure: context => other.Flow.BindCommands(context, "other/game", enabled: true));
            await h.Start();
            var first = RoomFlowTests.Rig.Player(11, "first");
            var second = RoomFlowTests.Rig.Player(22, "second");
            var firstId = await h.Rig.CreateAsync(first);
            var secondId = await other.CreateAsync(second);

            await other.Flow.HandleChatAsync(second, new(0, "/mode 8"), default);

            Assert.Equal(4, h.Rig.Registry.Find(firstId)!.ModeId);
            Assert.Equal(8, other.Registry.Find(secondId)!.ModeId);
            Assert.Empty(h.Rig.SettingsChanged.Sent);
            Assert.Single(other.SettingsChanged.To(second));
        }
        finally { await other.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task StartCommandUsesStoredSelectionWhenTheServerDrawsMaps()
    {
        await using var h = new Harness();
        await h.Start();
        var rig = new RoomFlowTests.Rig(options: new InGameOptions { ServerMapDraw = true });
        try
        {
            rig.Flow.BindCommands(h.Context, "draw/game", enabled: true);
            var player = RoomFlowTests.Rig.Player(33, "player");
            var roomId = await rig.CreateAsync(player);

            await rig.Flow.HandleChatAsync(player, new(0, "/start"), default);

            Assert.Equal(RoomPhase.Starting, rig.Registry.Find(roomId)!.Phase);
            Assert.Equal("rel_Village07_Easy02", Assert.Single(rig.Starting.To(player)).MapKey);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task StartCommandReportsRefusalWhenThereIsNoMapSelection()
    {
        await using var h = new Harness();
        await h.Start();
        var rig = new RoomFlowTests.Rig(options: new InGameOptions { ServerMapDraw = true });
        try
        {
            rig.Flow.BindCommands(h.Context, "draw/game", enabled: true);
            var player = RoomFlowTests.Rig.Player(33, "player");
            await rig.Flow.HandleCreateAsync(player, new("room", "", 4, ""), default);
            var principal = new CommandPrincipal(PrincipalKind.Game, "33");

            var result = await h.Executor.ExecuteTextAsync(new(principal, principal, SessionId: "33"), "/start");

            Assert.Equal(CommandStatus.InvalidState, result.Status);
            Assert.Equal(RoomPhase.Waiting, rig.Registry.TryGet(player.Id)!.Phase);
            Assert.Empty(rig.Starting.Sent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task DisabledSharedDispatchKeepsTheExistingHostOnlyCommands()
    {
        await using var h = new Harness(enabled: false);
        await h.Start();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await h.Rig.CreateAsync(host);
        await h.Rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);

        await h.Rig.Flow.HandleChatAsync(guest, new(0, "!set 8"), default);
        Assert.Equal(4, h.Rig.Registry.Find(roomId)!.ModeId);
        Assert.Equal("command.set.host_only:", Assert.Single(h.Rig.Notices.To(guest)).Text);
        await h.Rig.Flow.HandleChatAsync(host, new(0, "!set 8"), default);
        Assert.Equal(8, h.Rig.Registry.Find(roomId)!.ModeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalStartRechecksMasterAfterQueuedOwnershipTransfer(bool automatic)
    {
        var rig = new RoomFlowTests.Rig(options: automatic ? new InGameOptions { AutoStartModeIds = [4] } : null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var host = RoomFlowTests.Rig.Player(11, "host");
            var guest = RoomFlowTests.Rig.Player(22, "guest");
            var roomId = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(roomId, ""), default);
            var room = rig.Registry.Find(roomId)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = room.Executor.ExecuteAsync(async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            });
            await entered.Task.WaitAsync(Limit);
            var transfer = room.Executor.ExecuteAsync(_ =>
            {
                lock (room.Gate) Assert.True(room.TransferMaster(room.FindByGame(guest.Id)!.Slot));
                return ValueTask.CompletedTask;
            });
            var command = rig.Flow.HandleChatAsync(host, new(0, "!start"), default).AsTask();
            release.TrySetResult();
            await Task.WhenAll(blocked, transfer, command).WaitAsync(Limit);

            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.False(room.AutoStartArmed);
            Assert.Empty(rig.Starting.Sent);
            Assert.Empty(rig.AutoStart.Sent);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task UnloadingGameplayOwnerCancelsAndDrainsAnActiveRoutedCommand()
    {
        var player = RoomFlowTests.Rig.Player(11, "player");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(configure: context =>
            context.Effect(context.Services.Get<IGameCommandEndpoints>().Register(context,
                new WaitingEndpoint(player, entered, stopped))));
        await h.Start();
        var principal = new CommandPrincipal(PrincipalKind.Game, "11");
        var invocation = h.Executor.ExecuteTextAsync(new(principal, principal, SessionId: "11"), "/start").AsTask();
        await entered.Task.WaitAsync(Limit);

        await h.Host.UnloadAsync(h.Owner, default).AsTask().WaitAsync(Limit);

        Assert.True(stopped.Task.IsCompleted);
        Assert.Equal(CommandStatus.Cancelled, (await invocation.WaitAsync(Limit)).Status);
    }

    private sealed class WaitingEndpoint(ISession session, TaskCompletionSource entered, TaskCompletionSource stopped) : IGameCommandEndpoint
    {
        public string Scope => "waiting/game";
        public ISession? FindSession(long sessionId) => sessionId == session.Id ? session : null;
        public async ValueTask<CommandResult> ExecuteAsync(ISession target, CommandInvocation invocation, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { stopped.TrySetResult(); }
            return new(CommandStatus.Success, "unreachable");
        }
    }

    [Fact]
    public async Task SessionCannotBeUsedForAnotherAccountOrAfterOwnerUnload()
    {
        await using var h = new Harness();
        await h.Start();
        var player = RoomFlowTests.Rig.Player(11, "player");
        var roomId = await h.Rig.CreateAsync(player);
        var wrong = new CommandPrincipal(PrincipalKind.Game, "22");
        var result = await h.Executor.ExecuteTextAsync(new(wrong, wrong, SessionId: "11"), "/mode 8");
        Assert.Equal(CommandStatus.InvalidState, result.Status);
        Assert.Equal(4, h.Rig.Registry.Find(roomId)!.ModeId);

        var executor = h.Executor;
        await h.Host.UnloadAsync(h.Owner, default).AsTask().WaitAsync(Limit);
        var actual = new CommandPrincipal(PrincipalKind.Game, "11");
        result = await executor.ExecuteTextAsync(new(actual, actual, SessionId: "11"), "/mode 8");
        Assert.Equal(CommandStatus.InvalidState, result.Status);
        Assert.Equal(4, h.Rig.Registry.Find(roomId)!.ModeId);
    }
}
