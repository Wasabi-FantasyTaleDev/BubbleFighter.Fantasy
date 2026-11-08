using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

public sealed partial class CompanionLoopbackTests
{
    private sealed class StateReader(ISession owner, GameSessionState state) : IGameSessionStateReader
    {
        public GameSessionState? Describe(ISession session) => ReferenceEquals(owner, session) ? state : null;
    }

    [Fact]
    public async Task StateQueryUsesTheBoundPlayerAndNoClientIdentityFields()
    {
        var context = CommandContext();
        var session = CommandSession();
        var snapshot = new GameSessionState("match", true, true, false, 4);
        context.Effect(context.Services.RegisterMany<IGameSessionStateReader>(new StateReader(session, snapshot)));
        try
        {
            var state = new CompanionSessionState(context);
            using var route = _hub.Route(CompanionSessionState.MessageType, 1, state.HandleAsync);
            var grant = _launches.Open("alice", "cn-324");
            _launches.Bind(new(grant.LaunchId, "alice", "cn-324", session));
            await using var client = await Client.ConnectAsync(_endpoint, grant.Ticket);
            var epoch = (await client.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionSessionState.MessageType] = 1 }))
                .GetProperty("epoch").GetString();
            await client.SendAsync(new { kind = "request", id = "state-1", type = CompanionSessionState.MessageType,
                version = 1, epoch, payload = new { } });
            var result = (await client.ReceiveAsync()).GetProperty("payload");
            Assert.Equal(["can_leave_match", "in_match", "is_tutorial", "mode_id", "state"],
                result.EnumerateObject().Select(field => field.Name).Order());
            Assert.Equal("match", result.GetProperty("state").GetString());
            Assert.True(result.GetProperty("can_leave_match").GetBoolean());
            Assert.True(result.GetProperty("in_match").GetBoolean());
            Assert.False(result.GetProperty("is_tutorial").GetBoolean());
            Assert.Equal(4, result.GetProperty("mode_id").GetInt32());
            await client.SendAsync(new { kind = "request", id = "state-2", type = CompanionSessionState.MessageType,
                version = 1, epoch, payload = new { account = "another-player" } });
            var rejected = await client.ReceiveAsync();
            Assert.Equal("error", rejected.GetProperty("kind").GetString());
            Assert.Equal("invalid_request", rejected.GetProperty("payload").GetProperty("code").GetString());
            Assert.Equal(GameSessionState.Unavailable, await state.HandleAsync(
                new("another-player", "cn-324", grant.LaunchId, session, "en"), JsonSerializer.SerializeToElement(new { }), default));
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task KnownLobbyScenesAreDistinctFromMissingRoomOwnership()
    {
        var context = CommandContext();
        var session = CommandSession();
        var state = new CompanionSessionState(context);
        var caller = new CompanionCaller("alice", "cn-324", "launch", session, "en");
        var empty = JsonSerializer.SerializeToElement(new { });
        try
        {
            Assert.Equal("authenticated", Assert.IsType<GameSessionState>(await state.HandleAsync(caller, empty, default)).State);
            session.Feature<IPlayerSession>().MoveTo(SessionPhase.MainMenu);
            Assert.Equal("main_menu", Assert.IsType<GameSessionState>(await state.HandleAsync(caller, empty, default)).State);
            session.Feature<IPlayerSession>().MoveTo(SessionPhase.Plaza);
            Assert.Equal("plaza", Assert.IsType<GameSessionState>(await state.HandleAsync(caller, empty, default)).State);
            session.Feature<IPlayerSession>().MoveTo(SessionPhase.Room);
            Assert.Equal(GameSessionState.Unavailable, await state.HandleAsync(caller, empty, default));
            session.Feature<IPlayerSession>().MoveTo(SessionPhase.Match);
            Assert.Equal(GameSessionState.Unavailable, await state.HandleAsync(caller, empty, default));
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task InFlightCommandRejoinsTheSameOperationAfterSocketReplacement()
    {
        var context = CommandContext();
        var session = CommandSession();
        var input = new CommandInput(session)
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        context.Effect(context.Services.RegisterMany<BubbleFighter.Fantasy.Contracts.Administration.IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new());
            using var route = _hub.Route(CompanionCommands.MessageType, 1, commands.HandleAsync);
            var grant = _launches.Open("alice", "cn-324");
            _launches.Bind(new(grant.LaunchId, "alice", "cn-324", session));
            await using var first = await Client.ConnectAsync(_endpoint, grant.Ticket);
            var epoch = (await first.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionCommands.MessageType] = 1 }))
                .GetProperty("epoch").GetString();
            await first.SendAsync(new { kind = "request", id = "before-reconnect", type = CompanionCommands.MessageType,
                version = 1, epoch, payload = new { operation_id = "one", command = "plaza" } });
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var renewed = _launches.Renew("alice", grant.LaunchId)!;
            await using var second = await Client.ConnectAsync(_endpoint, renewed.Ticket);
            var nextEpoch = (await second.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionCommands.MessageType] = 1 }))
                .GetProperty("epoch").GetString();
            await second.SendAsync(new { kind = "request", id = "after-reconnect", type = CompanionCommands.MessageType,
                version = 1, epoch = nextEpoch, payload = new { operation_id = "one", command = "plaza" } });
            var response = second.ReceiveAsync();
            input.Release.TrySetResult();
            Assert.Equal("success", (await response).GetProperty("payload").GetProperty("status").GetString());
            Assert.Equal(1, input.Calls);
        }
        finally { input.Release.TrySetResult(); await context.UnwindAsync(); }
    }
}
