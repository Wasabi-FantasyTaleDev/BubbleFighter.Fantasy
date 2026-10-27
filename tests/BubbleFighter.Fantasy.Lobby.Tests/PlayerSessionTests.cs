using System.Collections.Concurrent;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class PlayerSessionTests
{
    private static readonly PlayerIdentity Player = new(7, "user", "nick", 3);

    [Fact]
    public void LauncherLanguageIsConnectionLocalAndReleasedEvenBeforeAuthentication()
    {
        var scope = new PlayerSessionScope();
        var pending = new FeatureSession(91);
        var other = new FeatureSession(92);
        scope.SetLauncherLanguage(pending, "zh-CN");
        scope.SetLauncherLanguage(other, "en");
        scope.SetLauncherLanguage(pending, null);
        scope.SetLauncherLanguage(pending, "zh-CN");
        Assert.Equal("zh-CN", pending.Feature<ISessionLanguage>().LauncherLanguage);
        Assert.Equal("en", other.Feature<ISessionLanguage>().LauncherLanguage);
        Assert.Throws<InvalidOperationException>(() => scope.SetLauncherLanguage(pending, "ko"));
        scope.Attach(pending, Player);
        Assert.Equal("zh-CN", pending.Feature<ISessionLanguage>().LauncherLanguage);
        scope.Remove(pending.Connection.Id);
        Assert.False(pending.TryFeature<ISessionLanguage>(out _));
        scope.Dispose();
        Assert.False(other.TryFeature<ISessionLanguage>(out _));
    }

    [Fact]
    public void NicknameChangePreservesAccountSceneAndGeneration()
    {
        var session = new PlayerSession(Player);
        session.MoveTo(SessionPhase.MainMenu);
        session.MoveTo(SessionPhase.Room);
        var generation = session.Generation;
        session.ChangeNickname("TestName001");
        Assert.Equal(Player with { Nickname = "TestName001" }, session.Identity);
        Assert.Equal(SessionPhase.Room, session.Phase);
        Assert.Equal(generation, session.Generation);
    }

    [Theory]
    [InlineData(SessionPhase.Authenticated, SessionPhase.MainMenu)]
    [InlineData(SessionPhase.MainMenu, SessionPhase.Plaza)]
    [InlineData(SessionPhase.MainMenu, SessionPhase.Room)]
    [InlineData(SessionPhase.Plaza, SessionPhase.MainMenu)]
    [InlineData(SessionPhase.Plaza, SessionPhase.Room)]
    [InlineData(SessionPhase.Room, SessionPhase.Plaza)]
    [InlineData(SessionPhase.Room, SessionPhase.Match)]
    [InlineData(SessionPhase.Match, SessionPhase.Room)]
    [InlineData(SessionPhase.Match, SessionPhase.Plaza)]
    public void AllowedTransitions(SessionPhase from, SessionPhase to) => Assert.True(PlayerSession.IsAllowed(from, to));

    [Theory]
    [InlineData(SessionPhase.Authenticated, SessionPhase.Plaza)]
    [InlineData(SessionPhase.Authenticated, SessionPhase.Room)]
    [InlineData(SessionPhase.MainMenu, SessionPhase.Match)]
    [InlineData(SessionPhase.Plaza, SessionPhase.Match)]
    [InlineData(SessionPhase.Room, SessionPhase.MainMenu)]
    [InlineData(SessionPhase.MainMenu, SessionPhase.Authenticated)]
    public void RefusedTransitions(SessionPhase from, SessionPhase to) => Assert.False(PlayerSession.IsAllowed(from, to));

    [Fact]
    public void MoveToCountsGenerationsAndRejectsSkippingTheLoginBurst()
    {
        var session = new PlayerSession(Player);
        Assert.Equal(SessionPhase.Authenticated, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.MoveTo(SessionPhase.Plaza));
        Assert.Equal(0, session.Generation);
        Assert.Equal(1, session.MoveTo(SessionPhase.MainMenu));
        Assert.Equal(1, session.MoveTo(SessionPhase.MainMenu));
        Assert.Equal(2, session.MoveTo(SessionPhase.Plaza));
        Assert.Equal(SessionPhase.Plaza, session.Phase);
    }

    [Fact]
    public void ScopeKeepsTheFirstLoginAndRefusesAnotherAccountOnTheSameConnection()
    {
        using var scope = new PlayerSessionScope();
        var session = new FeatureSession(1);
        var first = scope.Attach(session, Player);
        Assert.Same(first, session.Feature<IPlayerSession>());
        Assert.Same(first, scope.Attach(session, Player with { Nickname = "again" }));
        Assert.Throws<InvalidOperationException>(() => scope.Attach(session, Player with { UserNum = 8 }));
        Assert.Same(first, session.Feature<IPlayerSession>());
    }

    [Fact]
    public void LoginOwnershipSurvivesSharedStateAndMovesOnlyWithAReplacementLogin()
    {
        using var scope = new PlayerSessionScope();
        var first = new FeatureSession(41);
        var relay = new FeatureSession(42);
        var replacement = new FeatureSession(43);
        var state = scope.Attach(first, Player);
        using var shared = relay.RegisterFeature<IPlayerSession>(state);
        Assert.Equal(first.Connection.Id, relay.Feature<IPlayerSession>().LoginConnectionId);
        var claim = scope.Claim(replacement, Player, takeOver: true);
        Assert.Equal(LoginClaimStatus.Attached, claim.Status);
        Assert.Equal(replacement.Connection.Id, claim.Session!.LoginConnectionId);
        Assert.False(first.TryFeature<IPlayerSession>(out _));
        Assert.Equal(first.Connection.Id, relay.Feature<IPlayerSession>().LoginConnectionId);
        scope.Remove(replacement.Connection.Id);
        Assert.False(replacement.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public void ScopeReleasesRegistrationsOnCloseAndOnDispose()
    {
        var scope = new PlayerSessionScope();
        var closed = new FeatureSession(1);
        var open = new FeatureSession(2);
        scope.Attach(closed, Player);
        scope.Attach(open, Player with { UserNum = 9 });

        scope.Remove(closed.Connection.Id);
        Assert.False(closed.TryFeature<IPlayerSession>(out _));
        Assert.True(open.TryFeature<IPlayerSession>(out _));

        scope.Dispose();
        Assert.False(open.TryFeature<IPlayerSession>(out _));
    }

    private sealed class FeatureSession(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        public long Id { get; } = id;

        public IConnection Connection { get; } = new Conn(id);

        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Conn(long id) : IConnection
    {
        public long Id { get; } = id;

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);

        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();

        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public bool IsClosed => false;

        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
