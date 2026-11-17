using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Pins the duplicate-login policy: one live connection per account, settled either by closing the
/// older connection (<c>duplicate_login: kick_old</c>, the default) or by refusing the newer login
/// (<c>duplicate_login: reject_new</c>). Covers the option value, the scope's claim semantics and
/// the guard that closes the older connection.
/// </summary>
public sealed class DuplicateLoginTests
{
    private static readonly PlayerIdentity Player = new(7, "bftest", "bftest", 1);

    [Fact]
    public void DefaultPolicyIsKickOld()
    {
        var options = new LobbyOptions();

        Assert.Equal("kick_old", options.DuplicateLogin);
        Assert.Equal(DuplicateLoginBehavior.KickOld, options.DuplicateLoginPolicy);
    }

    [Theory]
    [InlineData("kick_old", DuplicateLoginBehavior.KickOld)]
    [InlineData("reject_new", DuplicateLoginBehavior.RejectNew)]
    public void BothDocumentedValuesParse(string value, DuplicateLoginBehavior expected)
    {
        var options = new LobbyOptions { DuplicateLogin = value };

        Assert.Empty(options.Validate());
        Assert.Equal(expected, options.DuplicateLoginPolicy);
    }

    [Fact]
    public void UnknownValueFailsValidation()
    {
        var options = new LobbyOptions { DuplicateLogin = "kick_them" };

        var error = Assert.Single(options.Validate());
        Assert.Contains("duplicate_login", error.ErrorMessage);
        Assert.Contains("kick_them", error.ErrorMessage);
        Assert.Contains(nameof(LobbyOptions.DuplicateLogin), error.MemberNames);
    }

    [Fact]
    public void UnknownValueThrowsWhenThePolicyIsRead()
    {
        var options = new LobbyOptions { DuplicateLogin = "kick_them" };

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = options.DuplicateLoginPolicy;
        });
        Assert.Contains("duplicate_login", error.Message);
    }

    [Fact]
    public void ClaimKeepsTheSameAccountOnTheSameConnection()
    {
        using var scope = new PlayerSessionScope();
        var (session, _) = NewSession(1);

        var first = scope.Claim(session, Player, takeOver: false);
        var again = scope.Claim(session, Player with { Nickname = "again" }, takeOver: true);

        Assert.Equal(LoginClaimStatus.Attached, first.Status);
        Assert.Equal(LoginClaimStatus.Existing, again.Status);
        Assert.Same(first.Session, again.Session);
        Assert.Null(again.Replaced);
        Assert.Same(first.Session, session.Feature<IPlayerSession>());
    }

    [Fact]
    public void ClaimRefusesADifferentAccountOnTheSameConnection()
    {
        using var scope = new PlayerSessionScope();
        var (session, _) = NewSession(1);
        scope.Claim(session, Player, takeOver: false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            scope.Claim(session, Player with { UserNum = 8 }, takeOver: true));

        Assert.Contains("refusing login as 8", error.Message);
        Assert.Equal(7, session.Feature<IPlayerSession>().Identity.UserNum);
    }

    [Fact]
    public void ClaimWithoutTakeOverLeavesTheOlderConnectionHoldingTheAccount()
    {
        using var scope = new PlayerSessionScope();
        var (first, _) = NewSession(1);
        var (second, _) = NewSession(2);
        var held = scope.Claim(first, Player, takeOver: false);

        var refused = scope.Claim(second, Player, takeOver: false);

        Assert.Equal(LoginClaimStatus.RefusedDuplicate, refused.Status);
        Assert.Null(refused.Session);
        Assert.Same(first, refused.Replaced);
        Assert.Same(held.Session, first.Feature<IPlayerSession>());
        Assert.False(second.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public void ClaimWithTakeOverReleasesTheOlderConnectionAndAttachesTheNewOne()
    {
        using var scope = new PlayerSessionScope();
        var (first, _) = NewSession(1);
        var (second, _) = NewSession(2);
        scope.Claim(first, Player, takeOver: false);

        var claim = scope.Claim(second, Player, takeOver: true);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Same(first, claim.Replaced);
        Assert.False(first.TryFeature<IPlayerSession>(out _));
        Assert.Same(claim.Session, second.Feature<IPlayerSession>());
        Assert.Equal(7, second.Feature<IPlayerSession>().Identity.UserNum);
    }

    [Fact]
    public void ClosingAConnectionFreesItsAccountForAnotherLogin()
    {
        using var scope = new PlayerSessionScope();
        var (first, _) = NewSession(1);
        var (second, _) = NewSession(2);
        scope.Claim(first, Player, takeOver: false);

        scope.Remove(first.Connection.Id);
        var claim = scope.Claim(second, Player, takeOver: false);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Null(claim.Replaced);
        Assert.False(first.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public void DisposeReleasesEveryHeldAccount()
    {
        var scope = new PlayerSessionScope();
        var (first, _) = NewSession(1);
        var (second, _) = NewSession(2);
        scope.Claim(first, Player, takeOver: false);
        scope.Claim(second, Player with { UserNum = 8 }, takeOver: false);

        scope.Dispose();

        Assert.False(first.TryFeature<IPlayerSession>(out _));
        Assert.False(second.TryFeature<IPlayerSession>(out _));
        var (third, _) = NewSession(3);
        Assert.Equal(LoginClaimStatus.Attached, scope.Claim(third, Player, takeOver: false).Status);
    }

    [Fact]
    public async Task KickOldClosesTheOlderConnectionAndLogsTheTakeover()
    {
        using var scope = new PlayerSessionScope();
        var logger = new RecordingLogger();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.KickOld, logger);
        var (first, firstConnection) = NewSession(1);
        var (second, secondConnection) = NewSession(2);
        await guard.ClaimAsync(first, Player);

        var claim = await guard.ClaimAsync(second, Player);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Same(first, claim.Replaced);
        Assert.Single(firstConnection.CloseReasons);
        Assert.Contains("duplicate login", firstConnection.CloseReasons[0]);
        Assert.Contains("session 2", firstConnection.CloseReasons[0]);
        Assert.Empty(secondConnection.CloseReasons);
        Assert.False(secondConnection.IsClosed);
        Assert.False(first.TryFeature<IPlayerSession>(out _));
        Assert.Same(claim.Session, second.Feature<IPlayerSession>());
        Assert.Contains(logger.Messages, message => message.Contains("took over account 7"));
        Assert.Contains(logger.Messages, message => message.Contains("duplicate_login kick_old"));
    }

    [Fact]
    public async Task KickOldDoesNotCloseAnythingForAReloginOnTheSameConnection()
    {
        using var scope = new PlayerSessionScope();
        var logger = new RecordingLogger();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.KickOld, logger);
        var (session, connection) = NewSession(1);

        await guard.ClaimAsync(session, Player);
        var claim = await guard.ClaimAsync(session, Player);

        Assert.Equal(LoginClaimStatus.Existing, claim.Status);
        Assert.Empty(connection.CloseReasons);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task KickOldHandsTheAccountToTheNewestLogin()
    {
        using var scope = new PlayerSessionScope();
        var logger = new RecordingLogger();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.KickOld, logger);
        var (first, firstConnection) = NewSession(1);
        var (second, secondConnection) = NewSession(2);
        var (third, thirdConnection) = NewSession(3);
        await guard.ClaimAsync(first, Player);
        await guard.ClaimAsync(second, Player);

        var claim = await guard.ClaimAsync(third, Player);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Same(second, claim.Replaced);
        Assert.Single(firstConnection.CloseReasons);
        Assert.Single(secondConnection.CloseReasons);
        Assert.Empty(thirdConnection.CloseReasons);
        // Each close names the login that took the account over, so the order is observable.
        Assert.Contains("session 2", firstConnection.CloseReasons[0]);
        Assert.Contains("session 3", secondConnection.CloseReasons[0]);
        Assert.False(first.TryFeature<IPlayerSession>(out _));
        Assert.False(second.TryFeature<IPlayerSession>(out _));
        Assert.Equal(7, third.Feature<IPlayerSession>().Identity.UserNum);
    }

    [Fact]
    public async Task RejectNewLeavesTheOlderConnectionOpenAndRefusesTheNewLogin()
    {
        using var scope = new PlayerSessionScope();
        var logger = new RecordingLogger();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.RejectNew, logger);
        var (first, firstConnection) = NewSession(1);
        var (second, secondConnection) = NewSession(2);
        var held = await guard.ClaimAsync(first, Player);

        var claim = await guard.ClaimAsync(second, Player);

        Assert.Equal(LoginClaimStatus.RefusedDuplicate, claim.Status);
        Assert.Null(claim.Session);
        Assert.Same(first, claim.Replaced);
        Assert.Empty(firstConnection.CloseReasons);
        Assert.Empty(secondConnection.CloseReasons);
        Assert.False(firstConnection.IsClosed);
        Assert.Same(held.Session, first.Feature<IPlayerSession>());
        Assert.False(second.TryFeature<IPlayerSession>(out _));
        Assert.Contains(logger.Messages, message => message.Contains("login refused"));
        Assert.Contains(logger.Messages, message => message.Contains("duplicate_login reject_new"));
    }

    [Fact]
    public async Task RejectNewStillServesTheConnectionThatHoldsTheAccount()
    {
        using var scope = new PlayerSessionScope();
        var logger = new RecordingLogger();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.RejectNew, logger);
        var (session, connection) = NewSession(1);

        var first = await guard.ClaimAsync(session, Player);
        var again = await guard.ClaimAsync(session, Player);

        Assert.Equal(LoginClaimStatus.Attached, first.Status);
        Assert.Equal(LoginClaimStatus.Existing, again.Status);
        Assert.Same(first.Session, again.Session);
        Assert.Empty(connection.CloseReasons);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task KickOldClosesTheAccountOnAnotherBuild()
    {
        var (builds, kr812, cn717, logger) = TwoBuilds(DuplicateLoginBehavior.KickOld);
        var (first, firstConnection) = NewSession(1, "kr-812");
        var (second, secondConnection) = NewSession(2, "cn-717");
        builds.Add(first);
        builds.Add(second);
        await kr812.ClaimAsync(first, Player);

        var claim = await cn717.ClaimAsync(second, Player);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Null(claim.Replaced);
        var reason = Assert.Single(firstConnection.CloseReasons);
        Assert.Contains("taken over on build cn-717 by session 2", reason);
        Assert.Empty(secondConnection.CloseReasons);
        Assert.Equal(7, second.Feature<IPlayerSession>().Identity.UserNum);
        Assert.Contains(logger.Messages, message => message.Contains("from build kr-812"));
    }

    [Fact]
    public async Task RejectNewRefusesALoginOnAnotherBuild()
    {
        var (builds, kr812, cn717, logger) = TwoBuilds(DuplicateLoginBehavior.RejectNew);
        var (first, firstConnection) = NewSession(1, "kr-812");
        var (second, secondConnection) = NewSession(2, "cn-717");
        builds.Add(first);
        builds.Add(second);
        var held = await kr812.ClaimAsync(first, Player);

        var claim = await cn717.ClaimAsync(second, Player);

        Assert.Equal(LoginClaimStatus.RefusedDuplicate, claim.Status);
        Assert.Same(first, claim.Replaced);
        Assert.Empty(firstConnection.CloseReasons);
        Assert.Empty(secondConnection.CloseReasons);
        Assert.Same(held.Session, first.Feature<IPlayerSession>());
        Assert.False(second.TryFeature<IPlayerSession>(out _));
        Assert.Contains(logger.Messages, message => message.Contains("already live on build kr-812"));
    }

    [Fact]
    public async Task AnAccountClosedOnAnotherBuildDoesNotCount()
    {
        var (builds, kr812, cn717, _) = TwoBuilds(DuplicateLoginBehavior.RejectNew);
        var (first, firstConnection) = NewSession(1, "kr-812");
        var (second, _) = NewSession(2, "cn-717");
        builds.Add(first);
        builds.Add(second);
        await kr812.ClaimAsync(first, Player);
        await firstConnection.CloseAsync("logged out");

        var claim = await cn717.ClaimAsync(second, Player);

        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
    }

    [Fact]
    public async Task ASecondLoginOnTheSameBuildIsLeftToThatBuild()
    {
        var builds = new SessionList();
        using var scope = new PlayerSessionScope();
        var guard = new DuplicateLoginGuard(scope, DuplicateLoginBehavior.KickOld, new RecordingLogger(), new AccountBuildLock(builds));
        var (first, firstConnection) = NewSession(1, "kr-812");
        var (second, _) = NewSession(2, "kr-812");
        builds.Add(first);
        builds.Add(second);
        await guard.ClaimAsync(first, Player);

        var claim = await guard.ClaimAsync(second, Player);

        Assert.Same(first, claim.Replaced);
        // Closed once, by the build's own takeover, not again as a session on another build.
        Assert.Single(firstConnection.CloseReasons);
    }

    /// <summary>Two builds' lobby guards sharing one server-wide account lock over one session list.</summary>
    private static (SessionList Sessions, DuplicateLoginGuard Kr812, DuplicateLoginGuard Cn717, RecordingLogger Logger) TwoBuilds(
        DuplicateLoginBehavior behavior)
    {
        var sessions = new SessionList();
        var logger = new RecordingLogger();
        var shared = new AccountBuildLock(sessions);
        return (sessions, new DuplicateLoginGuard(new PlayerSessionScope(), behavior, logger, shared),
            new DuplicateLoginGuard(new PlayerSessionScope(), behavior, logger, shared), logger);
    }

    private sealed class SessionList : ISessionRegistry
    {
        private readonly List<ISession> _sessions = [];

        public IReadOnlyList<ISession> All => [.. _sessions];

        public void Add(ISession session) => _sessions.Add(session);

        public void Remove(ISession session) => _sessions.Remove(session);
    }

    private static (FakeSession Session, FakeConnection Connection) NewSession(long id, string build = "test")
    {
        var connection = new FakeConnection(id);
        return (new FakeSession(id, connection, build), connection);
    }

    private sealed class FakeSession(long id, FakeConnection connection, string build) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        public long Id { get; } = id;

        public IConnection Connection { get; } = connection;

        public ClientProfile Profile { get; } = new(build, "kr", 812, 0, 0, "test");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection(long id) : IConnection
    {
        private readonly List<string> _closeReasons = [];

        public long Id { get; } = id;

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);

        public PipeReader Input => throw new NotSupportedException();

        public PipeWriter Output => throw new NotSupportedException();

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public bool IsClosed { get; private set; }

        /// <summary>Gets every reason this connection was closed with, in order.</summary>
        public IReadOnlyList<string> CloseReasons => _closeReasons;

        public ValueTask CloseAsync(string reason)
        {
            _closeReasons.Add(reason);
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        /// <summary>Gets the formatted entries, in order.</summary>
        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _messages.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
