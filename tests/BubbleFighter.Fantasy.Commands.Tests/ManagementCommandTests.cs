using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Commands;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Localization;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Commands.Tests;

public sealed class ManagementCommandTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Action<IPluginContext>> Plans = new();
    private static CommandCaller ConsoleCaller => new(new(PrincipalKind.Console, "local"), new(PrincipalKind.Console, "local"));
    private static CommandCaller GameCaller(Session session) => new(
        new(PrincipalKind.Game, session.Feature<IPlayerSession>().Identity.UserNum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new(PrincipalKind.Game, session.Feature<IPlayerSession>().Identity.UserNum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        SessionId: session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Plugin("test.management.owner", Requires = [typeof(IOnlineCommandScopes), typeof(ICommandExecutor)])]
    public sealed class Owner : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        { Plans[context.Row.Id](context); return ValueTask.CompletedTask; }
    }

    [Plugin("test.management.directory", Provides = [typeof(IOnlinePlayers), typeof(ISessionRegistry)],
        ProvidesMany = [typeof(IMessageSender<ServerNotice>)])]
    public sealed class DirectoryProvider : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        { Plans[context.Row.Id](context); return ValueTask.CompletedTask; }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly string OwnerId = Guid.NewGuid().ToString("N");
        internal readonly PluginHost Host;
        internal IPluginContext Context = null!;
        internal ICommandExecutor Executor = null!;
        internal IOnlineCommandScopes Scopes = null!;

        private Harness(int deadline, bool op, Session? adapterPlayer, string[]? senderProtocols)
        {
            Plans[OwnerId] = context =>
            {
                Context = context;
                Executor = context.Services.Get<ICommandExecutor>();
                Scopes = context.Services.Get<IOnlineCommandScopes>();
            };
            var rows = new List<PluginRow> {
                new() { Id = "permissions", Plugin = "permissions", Source = "test", Config = new Dictionary<string, object?>
                { ["subjects"] = new object[] { new Dictionary<string, object?> { ["kind"] = "game", ["id"] = "17", ["operator"] = op } } } },
                new() { Id = "commands", Plugin = "commands", Source = "test" },
                new() { Id = "localization", Plugin = "localization", Source = "test", Config = new Dictionary<string, object?>
                {
                    ["resources_path"] = Path.Combine(AppContext.BaseDirectory, "game-messages.json"),
                    ["launcher_languages"] = new object[] { new Dictionary<string, object?> { ["launcher_language"] = "zh-CN", ["language"] = "zh-Hans" } }
                } },
                new() { Id = "management", Plugin = "commands.management", Source = "test", Config = new Dictionary<string, object?> { ["disconnect_notice_timeout_ms"] = deadline } },
                new() { Id = OwnerId, Plugin = "test.management.owner", Source = "test" }
            };
            if (adapterPlayer is not null)
            {
                Plans[OwnerId + "provider"] = context =>
                {
                    context.Effect(context.Services.Register<IOnlinePlayers>(new Directory { Current = adapterPlayer }));
                    context.Effect(context.Services.Register<ISessionRegistry>(new Sessions(adapterPlayer)));
                    foreach (var protocol in senderProtocols ?? ["deployment/custom-game"])
                        context.Effect(context.Services.RegisterMany<IMessageSender<ServerNotice>>(new Sender(protocol)));
                };
                rows.Add(new() { Id = OwnerId + "provider", Plugin = "test.management.directory", Source = "test" });
                rows.Add(new() { Id = OwnerId + "adapter", Plugin = "commands.players", Source = "test",
                    Config = new Dictionary<string, object?> { ["protocol"] = "deployment/custom-game" } });
            }
            Host = new PluginHost(rows, PluginCatalog.ScanAssemblies([typeof(CommandsPlugin).Assembly, typeof(Owner).Assembly, typeof(LocalizationPlugin).Assembly]), NullLoggerFactory.Instance);
        }

        internal static async Task<Harness> Start(int deadline = 1000, bool op = true, Session? adapterPlayer = null, string[]? senderProtocols = null)
        {
            var harness = new Harness(deadline, op, adapterPlayer, senderProtocols);
            try { await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit); return harness; }
            catch { await harness.DisposeAsync(); throw; }
        }

        internal IDisposable Add(FakeScope scope) => Context.Effect(Scopes.Register(Context, scope));
        internal Task<CommandResult> Run(string command, params string[] arguments) =>
            Executor.ExecuteAsync(ConsoleCaller, command, arguments).AsTask().WaitAsync(Limit);

        public async ValueTask DisposeAsync()
        {
            try { await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
            finally { Plans.TryRemove(OwnerId, out _); Plans.TryRemove(OwnerId + "provider", out _); }
        }
    }

    [Fact]
    public async Task CountsAccountsAcrossScopesIncludingMainMenuAndAnnouncesToEveryConnection()
    {
        await using var h = await Harness.Start();
        var first = new Session(1, 17, "first");
        var second = new Session(2, 18, "second");
        var anotherBuild = new Session(3, 17, "first");
        h.Add(new("one", first, second));
        h.Add(new("two", anotherBuild));
        Assert.Equal(SessionPhase.MainMenu, first.Feature<IPlayerSession>().Phase);
        var count = await h.Run("list");
        Assert.Equal(CommandStatus.Success, count.Status);
        Assert.Contains("2", count.Message);
        Assert.Equal(CommandStatus.Success, (await h.Run("notice", "Announcement text")).Status);
        foreach (var session in new[] { first, second, anotherBuild }) Assert.Equal("Announcement text", Assert.Single(session.Notices));
    }

    [Fact]
    public async Task OrdinaryPlayersCanCountButCannotAnnounceOrDisconnect()
    {
        await using var h = await Harness.Start(op: false);
        var session = new Session(1, 17, "player");
        h.Add(new("one", session));
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(GameCaller(session), "list", [])).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(GameCaller(session), "notice", ["text"])).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(GameCaller(session), "disconnect", ["player"])).Status);
        Assert.Empty(session.Notices);
        Assert.False(session.Connection.IsClosed);
    }

    [Fact]
    public async Task SpoofedGameIdentityDoesNotReceivePlayerDirectoryResults()
    {
        await using var h = await Harness.Start();
        var session = new Session(1, 17, "player");
        h.Add(new("one", session));
        var spoof = GameCaller(session) with { Subject = new(PrincipalKind.Game, "18") };
        Assert.Equal(CommandStatus.InvalidState, (await h.Executor.ExecuteAsync(spoof, "list", [])).Status);
    }

    [Fact]
    public async Task DisconnectWaitsForNoticeFenceAndUsesTargetsLauncherLanguage()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 18, "target") { WaitForDrain = true };
        target.RegisterFeature<ISessionLanguage>(new Language("zh-CN"));
        h.Add(new("one", target));
        var pending = h.Run("disconnect", "target", "maintenance");
        await target.DrainStarted.Task.WaitAsync(Limit);
        Assert.False(target.Connection.IsClosed);
        Assert.Equal("你的连接已由管理员断开。原因：maintenance", Assert.Single(target.Notices));
        target.ReleaseDrain.TrySetResult();
        Assert.Equal(CommandStatus.Success, (await pending).Status);
        Assert.Equal(new[] { "notice", "drain", "close" }, target.Order);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("drain")]
    [InlineData("deadline")]
    public async Task NotificationFailureStillClosesOnlyTheSelectedTarget(string failure)
    {
        await using var h = await Harness.Start(deadline: 30);
        var target = new Session(1, 18, "target") { FailSend = failure == "send", FailDrain = failure == "drain", WaitForDrain = failure == "deadline" };
        var other = new Session(2, 19, "other");
        h.Add(new("one", target, other));
        var result = await h.Run("disconnect", "target");
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Contains("notification", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.Connection.IsClosed);
        Assert.False(other.Connection.IsClosed);
    }

    [Fact]
    public async Task ReplacementLoginDuringDrainIsNotDisconnected()
    {
        await using var h = await Harness.Start();
        var old = new Session(1, 18, "target") { WaitForDrain = true };
        var scope = new FakeScope("one", old);
        h.Add(scope);
        var pending = h.Run("disconnect", "target");
        await old.DrainStarted.Task.WaitAsync(Limit);
        var replacement = new Session(2, 18, "target");
        scope.Players = [replacement];
        old.ReleaseDrain.TrySetResult();
        Assert.Equal(CommandStatus.InvalidState, (await pending).Status);
        Assert.False(old.Connection.IsClosed);
        Assert.False(replacement.Connection.IsClosed);
    }

    [Fact]
    public async Task SelfDisconnectDoesNotRequestAnotherChatReplyOnTheClosedConnection()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 17, "target");
        h.Add(new("one", target));
        var result = await h.Executor.ExecuteAsync(GameCaller(target), "disconnect", ["target"]);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Empty(result.Message);
        Assert.True(target.Connection.IsClosed);
        Assert.Single(target.Notices);
    }

    [Fact]
    public async Task DuplicateNicknameAcrossScopesIsRejectedBeforeNotification()
    {
        await using var h = await Harness.Start();
        var first = new Session(1, 17, "same");
        var second = new Session(2, 18, "same");
        h.Add(new("one", first));
        h.Add(new("two", second));
        Assert.Equal(CommandStatus.InvalidState, (await h.Run("disconnect", "same")).Status);
        Assert.Empty(first.Notices);
        Assert.Empty(second.Notices);
    }

    [Fact]
    public async Task OverlongNotificationIsRejectedBeforeAnySendOrClose()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 17, "target");
        h.Add(new("one", target));
        Assert.Equal(CommandStatus.InvalidArguments, (await h.Run("notice", new string('x', 512))).Status);
        Assert.Equal(CommandStatus.InvalidArguments, (await h.Run("disconnect", "target", new string('x', 511))).Status);
        Assert.Empty(target.Notices);
        Assert.False(target.Connection.IsClosed);
    }

    [Fact]
    public async Task OverlappingScopeOwnershipFailsBeforeAnyNotification()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 17, "target");
        h.Add(new("one", target));
        h.Add(new("two", target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run("notice", "text"));
        Assert.Empty(target.Notices);
        await h.Host.UnloadAsync(h.OwnerId, default).AsTask().WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Host.UnloadAsync("management", default).AsTask().WaitAsync(Limit));
    }

    [Fact]
    public async Task RemovedScopeIsUnavailableAndCannotBeAddressed()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 17, "target");
        var registration = h.Add(new("one", target));
        registration.Dispose();
        Assert.Equal(CommandStatus.InvalidState, (await h.Run("disconnect", "target")).Status);
        Assert.Empty(target.Notices);
    }

    [Fact]
    public async Task ScopeUnloadCancelsOwnedWorkAndDoesNotCloseAfterOwnershipEnds()
    {
        await using var h = await Harness.Start();
        var target = new Session(1, 17, "target") { WaitForDrain = true };
        h.Add(new("one", target));
        var pending = h.Run("disconnect", "target");
        await target.DrainStarted.Task.WaitAsync(Limit);
        await h.Host.UnloadAsync(h.OwnerId, default).AsTask().WaitAsync(Limit);
        Assert.Equal(CommandStatus.InvalidState, (await pending).Status);
        Assert.False(target.Connection.IsClosed);
        Assert.Equal(CommandStatus.InvalidState, (await h.Run("list")).Status);
    }

    [Fact]
    public async Task AnnouncementFailureIsReportedAndDoesNotSkipOtherTargets()
    {
        await using var h = await Harness.Start();
        var failed = new Session(1, 17, "failed") { FailSend = true };
        var target = new Session(2, 18, "target");
        h.Add(new("one", failed, target));
        Assert.Equal(CommandStatus.Failed, (await h.Run("notice", "text")).Status);
        Assert.Equal("text", Assert.Single(target.Notices));
    }

    [Fact]
    public async Task PlayerAdapterExcludesRelayCopiesAndReplacedConnectionsWithoutGuessingProfileProtocol()
    {
        var authoritative = new Session(1, 17, "player");
        var relay = new Session(2, 17, "player");
        var old = new Session(3, 17, "player");
        var directory = new Directory { Current = authoritative };
        var registry = new Sessions(authoritative, relay, old);
        var sender = new Sender("deployment/custom-game");
        var scope = new OnlinePlayerCommandScope("scope", directory, registry, sender);
        Assert.Same(authoritative, Assert.Single(scope.Snapshot()));
        Assert.False(scope.IsCurrent(relay, 17));
        Assert.False(scope.IsCurrent(old, 17));
        Assert.False(scope.IsCurrent(authoritative, 18));
        await scope.SendNoticeAsync(authoritative, "text", default);
        Assert.Same(authoritative, Assert.Single(sender.Recipients));
        directory.Current = old;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.SendNoticeAsync(authoritative, "late", default).AsTask());
        Assert.Same(old, Assert.Single(scope.Snapshot()));
        await old.Connection.CloseAsync("test");
        Assert.Empty(scope.Snapshot());
    }

    [Fact]
    public async Task PlayerPluginSelectsConfiguredProtocolAndRemovesOwnedScopeOnUnload()
    {
        var player = new Session(1, 17, "player");
        await using var h = await Harness.Start(adapterPlayer: player,
            senderProtocols: ["unrelated/relay", "deployment/custom-game"]);
        Assert.Equal(CommandStatus.Success, (await h.Run("list")).Status);
        Assert.Equal(CommandStatus.Success, (await h.Run("notice", "text")).Status);
        await h.Host.UnloadAsync(h.OwnerId + "adapter", default).AsTask().WaitAsync(Limit);
        Assert.Equal(CommandStatus.InvalidState, (await h.Run("list")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicateConfiguredEncoderFailsPlayerPluginActivation(bool duplicate)
    {
        var player = new Session(1, 17, "player");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Harness.Start(adapterPlayer: player,
            senderProtocols: duplicate ? ["deployment/custom-game", "deployment/custom-game"] : ["unrelated/relay"]));
        Assert.Contains("exactly one notice encoder", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class Directory : IOnlinePlayers
    {
        internal Session? Current;
        public ISession? FindByUserNum(int userNum) => Current?.Feature<IPlayerSession>().Identity.UserNum == userNum ? Current : null;
        public ISession? FindByNickname(string nickname) => Current?.Feature<IPlayerSession>().Identity.Nickname == nickname ? Current : null;
    }

    private sealed class Sessions(params ISession[] sessions) : ISessionRegistry
    {
        public IReadOnlyList<ISession> All => sessions;
        public void Add(ISession session) => throw new NotSupportedException();
        public void Remove(ISession session) => throw new NotSupportedException();
    }

    private sealed class Sender(string protocol) : IMessageSender<ServerNotice>
    {
        internal readonly List<ISession> Recipients = [];
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, ServerNotice message, CancellationToken ct)
        { Recipients.Add(session); return ValueTask.CompletedTask; }
    }

    private sealed class FakeScope(string name, params Session[] players) : IOnlineCommandScope
    {
        internal Session[] Players = players;
        public string Scope => name;
        public IReadOnlyList<ISession> Snapshot() => Players;
        public bool IsCurrent(ISession session, int accountId) => !session.Connection.IsClosed &&
            Players.Any(player => ReferenceEquals(player, session) && player.Feature<IPlayerSession>().Identity.UserNum == accountId);
        public ValueTask SendNoticeAsync(ISession session, string text, CancellationToken cancellationToken) =>
            ((Session)session).Notice(text, cancellationToken);
    }

    private sealed class Session : ISessionWithFeatures, ISessionOutputDrain
    {
        private readonly Connection _connection;
        internal readonly List<string> Notices = [];
        internal readonly List<string> Order = [];
        internal readonly TaskCompletionSource DrainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WaitForDrain { get; init; }
        internal bool FailDrain { get; init; }
        internal bool FailSend { get; init; }
        internal Session(long id, int accountId, string name)
        {
            Id = id;
            _connection = new Connection(id, Order);
            this.SetLobbyPlayer(accountId, name);
        }
        public long Id { get; }
        public IConnection Connection => _connection;
        public ClientProfile Profile { get; } = new("test", "kr", 1, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
        internal ValueTask Notice(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSend) throw new IOException("Synthetic notification failure.");
            Notices.Add(text);
            Order.Add("notice");
            return ValueTask.CompletedTask;
        }
        public async ValueTask DrainOutputAsync(CancellationToken cancellationToken)
        {
            Order.Add("drain");
            DrainStarted.TrySetResult();
            if (FailDrain) throw new IOException("Synthetic drain failure.");
            if (WaitForDrain) await ReleaseDrain.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record Language(string LauncherLanguage) : ISessionLanguage;

    private sealed class Connection(long id, List<string> order) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { order.Add("close"); IsClosed = true; return ValueTask.CompletedTask; }
    }
}
