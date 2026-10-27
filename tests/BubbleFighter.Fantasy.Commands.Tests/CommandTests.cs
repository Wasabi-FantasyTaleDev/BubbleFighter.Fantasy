using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Commands;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Commands.Tests;

public sealed class CommandTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Action<IPluginContext>> Plans = new();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CommandCaller Caller(PrincipalKind kind = PrincipalKind.Game, string id = "17") => new(new(kind, id), new(kind, id));

    [Plugin("test.commands", Requires = [typeof(ICommandRegistry), typeof(ICommandExecutor), typeof(IPermissionRegistry), typeof(IPermissionService)])]
    public sealed class Consumer : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        { Plans[context.Row.Id](context); return ValueTask.CompletedTask; }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly PluginHost Host;
        internal readonly string Owner = Guid.NewGuid().ToString("N");
        internal IPluginContext Context = null!;
        internal ICommandRegistry Registry => Context.Services.Get<ICommandRegistry>();
        internal ICommandExecutor Executor => Context.Services.Get<ICommandExecutor>();
        internal IPermissionRegistry Nodes => Context.Services.Get<IPermissionRegistry>();
        internal IPermissionService Permissions => Context.Services.Get<IPermissionService>();
        internal readonly ConcurrentQueue<CommandCompleted> Audit = new();
        private Harness(Dictionary<string, object?>? permissions, Dictionary<string, object?>? commands)
        {
            Plans[Owner] = ctx => { Context = ctx; ctx.Effect(ctx.Events.On<CommandCompleted>(Audit.Enqueue)); };
            Host = new PluginHost([
                new() { Id = "permissions", Plugin = "permissions", Source = "test", Config = permissions ?? [] },
                new() { Id = "commands", Plugin = "commands", Source = "test", Config = commands ?? [] },
                new() { Id = Owner, Plugin = "test.commands", Source = "test" }],
                PluginCatalog.ScanAssemblies([typeof(CommandsPlugin).Assembly, typeof(Consumer).Assembly]), NullLoggerFactory.Instance);
        }
        internal static async Task<Harness> Start(Dictionary<string, object?>? permissions = null, Dictionary<string, object?>? commands = null)
        {
            var h = new Harness(permissions, commands);
            try { await h.Host.StartAsync(default).AsTask().WaitAsync(Limit); return h; }
            catch { await h.DisposeAsync(); throw; }
        }
        internal IDisposable Add(CommandHandler? handler = null, IReadOnlyList<CommandArgument>? args = null, string name = "echo", bool allowed = true)
        {
            Context.Effect(Nodes.Register(new("test." + name, "Test operation.", allowed)));
            return Context.Effect(Registry.Register(Context, new(name, ["alias-" + name], "test." + name, "Test command.", args ?? []),
                handler ?? ((invocation, _) => ValueTask.FromResult(new CommandResult(CommandStatus.Success, string.Join('|', invocation.Arguments))))));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
            finally { Plans.TryRemove(Owner, out _); }
        }
    }

    [Theory]
    [InlineData("/echo \"two words\" -17 tail text")]
    [InlineData("!alias-echo \"two words\" -17 tail text")]
    [InlineData("！echo \"two words\" -17 tail text")]
    public async Task TextPrefixesAliasesAndStructuredArgumentsReachTheSameSchema(string text)
    {
        await using var h = await Harness.Start();
        h.Add(args: [new("name"), new("count", CommandArgumentKind.Integer), new("reason", CommandArgumentKind.RemainingText)]);
        var caller = Caller() with { Language = "zh-CN", SessionId = "verified-session" };
        var parsed = await h.Executor.ExecuteTextAsync(caller, text);
        var structured = await h.Executor.ExecuteAsync(caller, "ECHO", ["two words", "-17", "tail text"]);
        Assert.Equal(new(CommandStatus.Success, "two words|-17|tail text"), parsed);
        Assert.Equal(parsed, structured);
        Assert.All(h.Audit, e => { Assert.Equal("echo", e.Command); Assert.Equal(h.Owner, e.Owner); });
    }

    [Fact]
    public async Task MissingLanguagePreferenceRemainsEmptyForDeploymentFallback()
    {
        await using var h = await Harness.Start();
        string? observed = null;
        h.Add((invocation, _) =>
        {
            observed = invocation.Caller.Language;
            return ValueTask.FromResult(new CommandResult(CommandStatus.Success, ""));
        });
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteTextAsync(Caller() with { Language = "" }, "/echo")).Status);
        Assert.Equal("", observed);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Executor.ExecuteTextAsync(Caller() with { Language = "\n" }, "/echo").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => h.Executor.ExecuteTextAsync(Caller() with { Language = null! }, "/echo").AsTask());
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("/echo \"unterminated")]
    [InlineData("/echo 9223372036854775808")]
    [InlineData("/echo 1 2")]
    [InlineData("/echo 1\n")]
    [InlineData("/echo \"\"")]
    public async Task MalformedTextNeverInvokesAHandler(string text)
    {
        await using var h = await Harness.Start();
        var calls = 0;
        h.Add((_, _) => { calls++; return ValueTask.FromResult(new CommandResult(CommandStatus.Success, "ok")); }, [new("number", CommandArgumentKind.Integer)]);
        Assert.Equal(CommandStatus.InvalidArguments, (await h.Executor.ExecuteTextAsync(Caller(), text)).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BareConsoleInputIsExplicitAndSuperusersStillValidateArguments()
    {
        await using var h = await Harness.Start();
        h.Add(args: [new("number", CommandArgumentKind.Integer)], allowed: false);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteTextAsync(Caller(PrincipalKind.Console), "echo 42", false)).Status);
        Assert.Equal(CommandStatus.InvalidArguments, (await h.Executor.ExecuteTextAsync(Caller(PrincipalKind.Console), "/echo wrong")).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteTextAsync(Caller(), "/echo 42")).Status);
        Assert.Empty(h.Registry.List(Caller().Subject));
        Assert.Single(h.Registry.List(Caller(PrincipalKind.Console).Subject));
    }

    [Fact]
    public async Task DuplicateAliasRegistrationIsAtomicAndMetadataIsDetached()
    {
        await using var h = await Harness.Start();
        var aliases = new[] { "first" };
        h.Context.Effect(h.Nodes.Register(new("test.echo", "Echo.", true)));
        using var registration = h.Context.Effect(h.Registry.Register(h.Context, new("echo", aliases, "test.echo", "Echo.", []),
            (_, _) => ValueTask.FromResult(new CommandResult(CommandStatus.Success, "ok"))));
        aliases[0] = "changed";
        Assert.Throws<InvalidOperationException>(() => h.Registry.Register(h.Context, new("second", ["fresh", "first"], "test.echo", "Duplicate.", []), (_, _) => default));
        Assert.Equal(CommandStatus.NotFound, (await h.Executor.ExecuteAsync(Caller(), "fresh", [])).Status);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "first", [])).Status);
        Assert.Equal("first", Assert.Single(h.Registry.List(Caller().Subject)).Aliases.Single());
        registration.Dispose();
        registration.Dispose();
        Assert.Equal(CommandStatus.NotFound, (await h.Executor.ExecuteAsync(Caller(), "first", [])).Status);
    }

    [Fact]
    public async Task UnregisteredPermissionCannotBeUsedAndRemovalFailsClosed()
    {
        await using var h = await Harness.Start();
        var def = new CommandDefinition("restricted", [], "test.restricted", "Restricted.", []);
        Assert.Throws<InvalidOperationException>(() => h.Registry.Register(h.Context, def, (_, _) => default));
        using var permission = h.Context.Effect(h.Nodes.Register(new("test.restricted", "Restricted.", true)));
        h.Context.Effect(h.Registry.Register(h.Context, def, (_, _) => ValueTask.FromResult(new CommandResult(CommandStatus.Success, "ok"))));
        permission.Dispose();
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.Console), "restricted", [])).Status);
    }

    [Fact]
    public async Task DisposingRegistrationCancelsInFlightAndReleasesCapacity()
    {
        await using var h = await Harness.Start(commands: new() { ["max_concurrent"] = 1 });
        var entered = Signal();
        var stopped = Signal();
        var handle = h.Add(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
            return new(CommandStatus.Success, "unreachable");
        });
        var first = h.Executor.ExecuteAsync(Caller(), "echo", []).AsTask();
        await entered.Task.WaitAsync(Limit);
        Assert.Equal(CommandStatus.Busy, (await h.Executor.ExecuteAsync(Caller(), "echo", [])).Status);
        handle.Dispose();
        Assert.Equal(CommandStatus.Cancelled, (await first.WaitAsync(Limit)).Status);
        await stopped.Task.WaitAsync(Limit);
        h.Add(name: "next");
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "next", [])).Status);
    }

    [Fact]
    public async Task UnloadDrainsOwnedHandlersAndRemovesEveryAlias()
    {
        var h = await Harness.Start();
        var entered = Signal();
        var stopped = Signal();
        h.Add(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
            return new(CommandStatus.Success, "unreachable");
        });
        var executor = h.Executor;
        var call = executor.ExecuteAsync(Caller(), "echo", []).AsTask();
        await entered.Task.WaitAsync(Limit);
        await h.Host.UnloadAsync(h.Owner, default).AsTask().WaitAsync(Limit);
        Assert.True(stopped.Task.IsCompleted);
        Assert.Equal(CommandStatus.Cancelled, (await call.WaitAsync(Limit)).Status);
        Assert.Equal(CommandStatus.NotFound, (await executor.ExecuteAsync(Caller(), "alias-echo", [])).Status);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task OwnerMayUnregisterItsOwnCommandWithoutDeadlock()
    {
        await using var h = await Harness.Start();
        IDisposable? handle = null;
        handle = h.Add((_, _) => { handle!.Dispose(); return ValueTask.FromResult(new CommandResult(CommandStatus.Success, "removed")); });
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "echo", []).AsTask().WaitAsync(Limit)).Status);
        Assert.Equal(CommandStatus.NotFound, (await h.Executor.ExecuteAsync(Caller(), "echo", [])).Status);
    }

    [Fact]
    public async Task AuditPreservesActorAndSubjectWithoutArguments()
    {
        await using var h = await Harness.Start();
        h.Add(args: [new("secret")]);
        var caller = new CommandCaller(new(PrincipalKind.Game, "target"), new(PrincipalKind.Console, "local"));
        await h.Executor.ExecuteAsync(caller, "echo", ["secret-value"]);
        var audit = Assert.Single(h.Audit);
        Assert.Equal(caller.Actor, audit.Actor);
        Assert.Equal(caller.Subject, audit.Subject);
        Assert.DoesNotContain("secret-value", audit.ToString());
    }

    [Fact]
    public async Task CallerCancellationStopsSupervisedHandler()
    {
        await using var h = await Harness.Start();
        using var source = new CancellationTokenSource();
        var entered = Signal();
        h.Add(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(CommandStatus.Success, "unreachable"); });
        var work = h.Executor.ExecuteAsync(Caller(), "echo", [], source.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        source.Cancel();
        Assert.Equal(CommandStatus.Cancelled, (await work.WaitAsync(Limit)).Status);
    }

    [Fact]
    public async Task RegistryAndInputBoundsRejectBeforeCallingHandler()
    {
        await using var h = await Harness.Start(commands: new() { ["max_commands"] = 1, ["max_input_characters"] = 64 });
        h.Add(args: [new("value", CommandArgumentKind.RemainingText)]);
        Assert.Throws<InvalidOperationException>(() => h.Add(name: "second"));
        Assert.Equal(CommandStatus.InvalidArguments, (await h.Executor.ExecuteAsync(Caller(), "echo", [new string('x', 65)])).Status);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteTextAsync(Caller(), "/echo one two three")).Status);
    }

    [Fact]
    public async Task HandlerFaultIsObservableToCallerAndPluginOwner()
    {
        var h = await Harness.Start();
        h.Add((_, _) => throw new InvalidOperationException("handler failed"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Executor.ExecuteAsync(Caller(), "echo", []).AsTask().WaitAsync(Limit));
        Assert.Equal("handler failed", error.Message);
        Assert.Equal(CommandStatus.Failed, Assert.Single(h.Audit).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Host.UnloadAsync(h.Owner, default).AsTask().WaitAsync(Limit));
        await h.DisposeAsync();
    }

    [Fact]
    public async Task OptionalArgumentsAndSchemaValidationAreShared()
    {
        await using var h = await Harness.Start();
        h.Add(args: [new("optional", Optional: true)]);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "echo", [])).Status);
        Assert.Throws<ArgumentException>(() => h.Registry.Register(h.Context, new("bad", [], "test.echo", "Bad.", [new("optional", Optional: true), new("required")]), (_, _) => default));
        Assert.Throws<ArgumentException>(() => h.Registry.Register(h.Context, new("bad", [], "test.echo", "Bad.", [new("rest", CommandArgumentKind.RemainingText), new("other")]), (_, _) => default));
    }

    [Fact]
    public async Task PrincipalNamespacesAccessOnlyAndSuperusersStaySeparate()
    {
        await using var h = await Harness.Start(new()
        {
            ["subjects"] = new object[]
            {
                new Dictionary<string, object?> { ["kind"] = "game", ["id"] = "same", ["operator"] = true, ["deny"] = new[] { "test.*" } },
                new Dictionary<string, object?> { ["kind"] = "webui", ["id"] = "same", ["allow"] = new[] { "webui.access" } },
                new Dictionary<string, object?> { ["kind"] = "webui", ["id"] = "admin", ["allow"] = new[] { "webui.access", "webui.admin" }, ["deny"] = new[] { "test.*" } }
            }
        });
        h.Add(allowed: false);
        Assert.True(h.Permissions.IsAllowed(Caller(PrincipalKind.WebUi, "same").Subject, "webui.access"));
        Assert.False(h.Permissions.IsAllowed(Caller(PrincipalKind.WebUi, "same").Subject, "webui.admin"));
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.WebUi, "same"), "echo", [])).Status);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.Game, "same"), "echo", [])).Status);
        Assert.False(h.Permissions.IsAllowed(Caller(PrincipalKind.Game, "same").Subject, "webui.access"));
        Assert.False(h.Permissions.IsAllowed(Caller(PrincipalKind.Game, "same").Subject, "webui.admin"));
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.WebUi, "admin"), "echo", [])).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.Game, "admin"), "echo", [])).Status);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(PrincipalKind.Console), "echo", [])).Status);
    }

    [Fact]
    public async Task InheritedAndPersonalDenialsOverrideAllOrdinaryAllows()
    {
        await using var h = await Harness.Start(new()
        {
            ["groups"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "base", ["allow"] = new[] { "test.*" }, ["deny"] = new[] { "test.echo" } },
                new Dictionary<string, object?> { ["name"] = "child", ["inherits"] = new[] { "base" }, ["allow"] = new[] { "test.echo" } }
            },
            ["subjects"] = new object[]
            {
                new Dictionary<string, object?> { ["id"] = "17", ["groups"] = new[] { "child" }, ["allow"] = new[] { "test.echo" }, ["deny"] = new[] { "test.other" } }
            }
        });
        h.Add();
        h.Add(name: "other", allowed: false);
        h.Add(name: "inherited", allowed: false);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(Caller(), "echo", [])).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Executor.ExecuteAsync(Caller(), "other", [])).Status);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "inherited", [])).Status);
        Assert.Equal("inherited", Assert.Single(h.Registry.List(Caller().Subject)).Name);
    }

    [Fact]
    public async Task RemainingTextHasNoIndependentWordLimit()
    {
        await using var h = await Harness.Start();
        h.Add(args: [new("text", CommandArgumentKind.RemainingText)]);
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        Assert.Equal(new(CommandStatus.Success, text), await h.Executor.ExecuteTextAsync(Caller(), "/echo " + text));
    }

    [Fact]
    public async Task DeepAcyclicInheritanceUsesNoCallStackDepthLimit()
    {
        var groups = Enumerable.Range(0, 2000).Select(i => (object)new Dictionary<string, object?>
        {
            ["name"] = "g" + i,
            ["inherits"] = i == 0 ? Array.Empty<string>() : new[] { "g" + (i - 1) },
            ["allow"] = i == 0 ? new[] { "test.echo" } : Array.Empty<string>()
        }).ToArray();
        await using var h = await Harness.Start(new()
        {
            ["groups"] = groups,
            ["subjects"] = new object[] { new Dictionary<string, object?> { ["id"] = "17", ["groups"] = new[] { "g1999" } } }
        });
        h.Add(allowed: false);
        Assert.Equal(CommandStatus.Success, (await h.Executor.ExecuteAsync(Caller(), "echo", [])).Status);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("unknown")]
    [InlineData("webui-op")]
    [InlineData("duplicate")]
    [InlineData("pattern")]
    [InlineData("bound")]
    [InlineData("expanded")]
    public async Task InvalidPermissionConfigurationFailsAtActivation(string scenario)
    {
        Dictionary<string, object?> config = scenario switch
        {
            "cycle" => new() { ["groups"] = new object[] { new Dictionary<string, object?> { ["name"] = "a", ["inherits"] = new[] { "b" } }, new Dictionary<string, object?> { ["name"] = "b", ["inherits"] = new[] { "a" } } } },
            "unknown" => new() { ["subjects"] = new object[] { new Dictionary<string, object?> { ["id"] = "17", ["groups"] = new[] { "missing" } } } },
            "webui-op" => new() { ["subjects"] = new object[] { new Dictionary<string, object?> { ["kind"] = "webui", ["id"] = "17", ["operator"] = true } } },
            "duplicate" => new() { ["subjects"] = new object[] { new Dictionary<string, object?> { ["id"] = "17" }, new Dictionary<string, object?> { ["id"] = "17" } } },
            "pattern" => new() { ["subjects"] = new object[] { new Dictionary<string, object?> { ["id"] = "17", ["allow"] = new[] { "test.*.invalid" } } } },
            "bound" => new() { ["max_policy_entries"] = 2, ["subjects"] = new object[] { new Dictionary<string, object?> { ["id"] = "17", ["allow"] = new[] { "test.one", "test.two" } } } },
            _ => new() { ["max_policy_entries"] = 4, ["groups"] = new object[] { new Dictionary<string, object?> { ["name"] = "a", ["allow"] = new[] { "test.one" } }, new Dictionary<string, object?> { ["name"] = "b", ["inherits"] = new[] { "a" }, ["allow"] = new[] { "test.two" } } } }
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Harness.Start(config));
        Assert.Contains("permission", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
