using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Companion;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Companion.Tests;

public sealed partial class CompanionLoopbackTests
{
    private sealed class CommandInput(ISession session) : IGameCommandInput
    {
        internal int Calls;
        internal CommandResult Result = new(CommandStatus.Success, "done");
        internal TaskCompletionSource? Entered;
        internal TaskCompletionSource? Release;
        internal bool Fail;
        public bool OwnsSession(ISession target) => ReferenceEquals(session, target);
        public async ValueTask<CommandResult> ExecuteTextAsync(ISession target, string command, CommandSource source, CancellationToken cancellationToken)
        {
            Assert.Same(session, target);
            Assert.Equal("plaza", command);
            Interlocked.Increment(ref Calls);
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("private failure detail");
            return Result;
        }
    }

    private static PluginContext CommandContext() => new(new PluginRow { Id = "command-test", Plugin = "test", Source = "test" },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);

    private static Session CommandSession(long id = 7)
    {
        var session = new Session(id, "cn-324");
        session.RegisterFeature<IPlayerSession>(new PlayerSession(new(11, "alice", "Alice", 1)));
        return session;
    }

    [Theory]
    [InlineData(CommandStatus.Success, "success", "success")]
    [InlineData(CommandStatus.Denied, "rejected", "denied")]
    [InlineData(CommandStatus.Busy, "rejected", "busy")]
    [InlineData(CommandStatus.InvalidArguments, "rejected", "invalid_arguments")]
    [InlineData(CommandStatus.InvalidState, "rejected", "invalid_state")]
    [InlineData(CommandStatus.NotFound, "rejected", "not_found")]
    [InlineData(CommandStatus.Cancelled, "rejected", "cancelled")]
    public async Task CommandRequestsUseBoundSessionAndReplayAcrossAWebSocketReconnect(CommandStatus result, string status, string code)
    {
        var session = CommandSession();
        var input = new CommandInput(session) { Result = new(result, "outcome") };
        var context = CommandContext();
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new());
            using var route = _hub.Route(CompanionCommands.MessageType, 1, commands.HandleAsync);
            var grant = _launches.Open("alice", "cn-324");
            _launches.Bind(new(grant.LaunchId, "alice", "cn-324", session));
            await using var first = await Client.ConnectAsync(_endpoint, grant.Ticket);
            var epoch = (await first.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionCommands.MessageType] = 1 }))
                .GetProperty("epoch").GetString();
            await first.SendAsync(new { kind = "request", id = "command-1", type = CompanionCommands.MessageType,
                version = 1, epoch, payload = new { operation_id = "leave-1", command = "plaza" } });
            var reply = await first.ReceiveAsync();
            Assert.Equal("response", reply.GetProperty("kind").GetString());
            Assert.Equal("command-1", reply.GetProperty("reply_to").GetString());
            var payload = reply.GetProperty("payload");
            Assert.Equal(["code", "message", "operation_id", "status"], payload.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(status, payload.GetProperty("status").GetString());
            Assert.Equal(code, payload.GetProperty("code").GetString());
            Assert.Equal("leave-1", payload.GetProperty("operation_id").GetString());
            var renewed = _launches.Renew("alice", grant.LaunchId)!;
            await using var second = await Client.ConnectAsync(_endpoint, renewed.Ticket);
            var nextEpoch = (await second.HelloAsync("cn-324", grant.LaunchId, new() { [CompanionCommands.MessageType] = 1 }))
                .GetProperty("epoch").GetString();
            Assert.NotEqual(epoch, nextEpoch);
            await second.SendAsync(new { kind = "request", id = "command-2", type = CompanionCommands.MessageType,
                version = 1, epoch = nextEpoch, payload = new { operation_id = "leave-1", command = "plaza" } });
            var replay = await second.ReceiveAsync();
            Assert.Equal(payload.GetRawText(), replay.GetProperty("payload").GetRawText());
            Assert.Equal(1, input.Calls);
            await second.SendAsync(new { kind = "request", id = "command-3", type = CompanionCommands.MessageType,
                version = 1, epoch = nextEpoch, payload = new { operation_id = "leave-1", command = "start" } });
            Assert.Equal("operation_conflict", (await second.ReceiveAsync()).GetProperty("payload").GetProperty("code").GetString());
            Assert.Equal(1, input.Calls);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task ConcurrentDuplicatesRunOnceAndFullStorageRejectsWithoutEviction()
    {
        var context = CommandContext();
        var session = CommandSession();
        var input = new CommandInput(session) { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new() { MaxOperations = 1 });
            var caller = new CompanionCaller("alice", "cn-324", "launch", session, "en");
            var payload = JsonSerializer.SerializeToElement(new { operation_id = "one", command = "plaza" });
            var first = commands.HandleAsync(caller, payload, default).AsTask();
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = commands.HandleAsync(caller, payload, default).AsTask();
            Assert.False(second.IsCompleted);
            var occupied = Assert.IsType<CompanionCommands.Reply>(await commands.HandleAsync(caller,
                JsonSerializer.SerializeToElement(new { operation_id = "two", command = "plaza" }), default));
            Assert.Equal("busy", occupied.Code);
            input.Release.SetResult();
            Assert.Equal(await first, await second);
            Assert.Equal(1, input.Calls);
        }
        finally { input.Release.TrySetResult(); await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"operation_id\":\"one\",\"command\":\"plaza\",\"session_id\":7}")]
    [InlineData("{\"operation_id\":\"../one\",\"command\":\"plaza\"}")]
    [InlineData("{\"operation_id\":\"one\",\"command\":\"plaza\\n\"}")]
    public async Task MalformedCommandPayloadsAreRejectedBeforeGameplay(string json)
    {
        var context = CommandContext();
        try
        {
            using var commands = new CompanionCommands(context, new());
            var session = CommandSession();
            await Assert.ThrowsAsync<InvalidDataException>(() => commands.HandleAsync(new("alice", "cn-324", "launch", session, "en"),
                JsonDocument.Parse(json).RootElement.Clone(), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task FailedCommandIsCachedWithoutExposingExceptionDetails()
    {
        var context = CommandContext();
        var session = CommandSession();
        var input = new CommandInput(session) { Fail = true };
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new());
            var caller = new CompanionCaller("alice", "cn-324", "launch", session, "en");
            var payload = JsonSerializer.SerializeToElement(new { operation_id = "one", command = "plaza" });
            var result = Assert.IsType<CompanionCommands.Reply>(await commands.HandleAsync(caller, payload, default));
            Assert.Equal("error", result.Status);
            Assert.Equal("failed", result.Code);
            Assert.DoesNotContain("private", result.Message);
            Assert.Equal(result, await commands.HandleAsync(caller, payload, default));
            Assert.Equal(1, input.Calls);
        }
        finally { await Assert.ThrowsAsync<AggregateException>(() => context.UnwindAsync().AsTask()); }
    }

    private sealed class CommandClock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public async Task ReplayRetentionStartsAtCompletionAndPendingOperationsDoNotExpire()
    {
        var context = CommandContext();
        var session = CommandSession();
        var input = new CommandInput(session) { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        var clock = new CommandClock();
        try
        {
            using var commands = new CompanionCommands(context, new() { MaxOperations = 1, RetentionSeconds = 30 }, clock);
            var caller = new CompanionCaller("alice", "cn-324", "launch", session, "en");
            var payload = JsonSerializer.SerializeToElement(new { operation_id = "one", command = "plaza" });
            var first = commands.HandleAsync(caller, payload, default).AsTask();
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Seconds = 100;
            var duplicate = commands.HandleAsync(caller, payload, default).AsTask();
            Assert.False(duplicate.IsCompleted);
            Assert.Equal(1, input.Calls);
            input.Release.SetResult();
            Assert.Equal(await first, await duplicate);
            clock.Seconds = 129;
            Assert.Equal(await first, await commands.HandleAsync(caller, payload, default));
            Assert.Equal(1, input.Calls);
            clock.Seconds = 130;
            await commands.HandleAsync(caller, payload, default);
            Assert.Equal(2, input.Calls);
        }
        finally { input.Release.TrySetResult(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task UnauthenticatedAndForeignAccountCallsCannotReachTheGameInput()
    {
        var context = CommandContext();
        var session = CommandSession();
        var input = new CommandInput(session);
        context.Effect(context.Services.RegisterMany<IGameCommandInput>(input));
        try
        {
            using var commands = new CompanionCommands(context, new());
            var payload = JsonSerializer.SerializeToElement(new { operation_id = "one", command = "plaza" });
            foreach (var caller in new[]
            {
                new CompanionCaller("mallory", "cn-324", "launch", session, "en"),
                new CompanionCaller("alice", "cn-324", "launch", new Session(8, "cn-324"), "en"),
            })
                Assert.Equal("invalid_state", Assert.IsType<CompanionCommands.Reply>(await commands.HandleAsync(caller, payload, default)).Code);
            Assert.Equal(0, input.Calls);
        }
        finally { await context.UnwindAsync(); }
    }
}
