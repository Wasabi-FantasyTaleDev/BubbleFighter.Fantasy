using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>End-to-end tests of <c>loader.process</c> against real child processes.</summary>
public sealed class RemoteLoaderTests
{
    private static Dictionary<string, object?> EchoConfig() => new() { ["protocol"] = TestDialect.Protocol, ["greeting"] = "hi" };

    /// <summary>initialize and describe run at loader activation and the described plugin becomes a published definition.</summary>
    [PythonFact]
    public async Task InitializeAndDescribePublishADefinition()
    {
        await using var harness = new RemoteHarness([RemoteHarness.EchoExample()]);
        await harness.StartAsync();

        var definition = Assert.Single(harness.Host.Definitions, item => item.Id == "example.echo-lobby");
        Assert.Equal("1.0.0", definition.Version);
        Assert.Contains("kr:812", definition.SupportedBuilds);
        Assert.Contains(harness.Host.DefinitionSources, source => source.SourceId == "echo" && source.State == PluginDefinitionSourceState.Published);
    }

    /// <summary>activate, messages.on, deliver and messages.send make a full round trip through the real router.</summary>
    [PythonFact]
    public async Task ActivateRegistersAHandlerAndDeliveryRoundTrips()
    {
        await using var harness = new RemoteHarness([RemoteHarness.EchoExample()]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "example.echo-lobby", EchoConfig());

        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.Contains(harness.Router.Handlers, handler => handler.OwnerRowId == "echo" && handler.Protocol == TestDialect.Protocol);

        Assert.True(await harness.DispatchLoginAsync("alice"));

        var sent = Assert.Single(harness.Sent);
        Assert.Equal("alice", sent.Account);
        Assert.Equal("alice", sent.Nickname);
        Assert.Equal(300001, sent.CharacterItemId);
        Assert.Contains(harness.Loggers.Entries, entry => entry.Contains("hi alice"));
    }

    /// <summary>The TypeScript SDK example makes the same activate/deliver/send round trip as the Python one.</summary>
    [NodeFact]
    public async Task TypeScriptSdkExampleRoundTrips()
    {
        await using var harness = new RemoteHarness([RemoteHarness.TypeScriptEchoExample()]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo-ts", "example.echo-lobby-ts", EchoConfig());

        Assert.Equal(PluginState.Active, harness.Instance("echo-ts").State);
        Assert.Contains(harness.Router.Handlers, handler => handler.OwnerRowId == "echo-ts" && handler.Protocol == TestDialect.Protocol);

        Assert.True(await harness.DispatchLoginAsync("alice"));

        var sent = Assert.Single(harness.Sent);
        Assert.Equal("alice", sent.Account);
        Assert.Equal("alice", sent.Nickname);
        Assert.Equal(300001, sent.CharacterItemId);
        Assert.Contains(harness.Loggers.Entries, entry => entry.Contains("hi alice"));
    }

    /// <summary>The SDK's own disposers (effect.dispose calls) complete during deactivate without waiting on the row's unwind.</summary>
    [PythonFact]
    public async Task UnloadingARowServedByTheSdkCompletesCleanly()
    {
        await using var harness = new RemoteHarness([RemoteHarness.EchoExample()]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "example.echo-lobby", EchoConfig());

        await harness.Host.UnloadAsync("echo", default).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(harness.Router.Handlers, handler => handler.OwnerRowId == "echo");
        Assert.Equal(PluginState.Unloaded, harness.Instance("echo").State);
    }

    /// <summary>The plugin runs its disposers while the host still holds the handler; the host then releases the rest.</summary>
    [PythonFact]
    public async Task UnloadDeactivatesThePluginBeforeTheHostReleasesItsHandles()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry()]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });
        var handlersWhenDeactivating = -1;
        harness.Loggers.OnEntry = entry =>
        {
            if (entry.Contains("deactivating"))
                handlersWhenDeactivating = harness.Router.Handlers.Count(handler => handler.OwnerRowId == "echo");
        };

        await harness.Host.UnloadAsync("echo", default).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, handlersWhenDeactivating);
        Assert.DoesNotContain(harness.Router.Handlers, handler => handler.OwnerRowId == "echo");
        Assert.False(await harness.DispatchLoginAsync("alice"));
    }

    /// <summary>An unknown host contract name in a definition fails the loader with a ConfigException naming plugin and field.</summary>
    [PythonFact]
    public async Task UnknownContractNameIsAConfigException()
    {
        using var fake = new FakePlugin();
        var definition = FakePlugin.Echo();
        definition["requires"] = new[] { "IMessageRouter", "INoSuchService" };
        await using var harness = new RemoteHarness([fake.Entry(definition: definition)]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        var config = FindConfigException(error);
        Assert.Contains("fake.echo", config.Message);
        Assert.Contains("requires", config.Message);
        Assert.Contains("INoSuchService", config.Message);
    }

    /// <summary>An unknown message type name in handles fails the loader with a ConfigException.</summary>
    [PythonFact]
    public async Task UnknownMessageTypeNameIsAConfigException()
    {
        using var fake = new FakePlugin();
        var definition = FakePlugin.Echo();
        definition["handles"] = new[] { "NoSuchMessage" };
        await using var harness = new RemoteHarness([fake.Entry(definition: definition)]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        var config = FindConfigException(error);
        Assert.Contains("handles", config.Message);
        Assert.Contains("NoSuchMessage", config.Message);
    }

    /// <summary>A row whose config does not match the described schema fails activation with the offending keys.</summary>
    [PythonFact]
    public async Task RowConfigIsValidatedAgainstTheDescribedSchema()
    {
        await using var harness = new RemoteHarness([RemoteHarness.EchoExample()]);
        await harness.StartAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.AddRowAsync("echo", "example.echo-lobby", new() { ["greeting"] = 5, ["bogus"] = true }));

        var config = FindConfigException(error);
        Assert.Contains("bogus", config.Message);
        Assert.Contains("protocol", config.Message);
        Assert.Contains("greeting", config.Message);
    }

    /// <summary>A deliver request nobody answers times out, the handler fails and the row is faulted.</summary>
    [PythonFact]
    public async Task RequestTimeoutFaultsTheRow()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("silent-deliver")], new() { ["request_timeout_ms"] = 500 });
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => harness.DispatchLoginAsync("alice"));

        Assert.Contains("request_timeout_ms", Flatten(error));
        await WaitForAsync(() => harness.Loggers.Entries.Any(entry => entry.Contains("faulted")));
        Assert.Contains(harness.Loggers.Entries, entry => entry.Contains("SDK plugin 'fake.echo' faulted"));
        await AssertRowReportedFaultedAsync(harness);
    }

    /// <summary>A process that exits while a row is active faults that row, and a new activation is refused.</summary>
    [PythonFact]
    public async Task ProcessCrashFaultsTheRow()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("crash-deliver")]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        await Assert.ThrowsAnyAsync<Exception>(() => harness.DispatchLoginAsync("alice"));

        await WaitForAsync(() => harness.Loggers.Entries.Any(entry => entry.Contains("SDK plugin 'fake.echo' faulted")));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Host.ReconcileConfigurationAsync([.. harness.Host.Instances.Select(info => new PluginRow { Id = info.RowId, Plugin = info.Plugin, Source = "test" })]).AsTask());
        Assert.NotNull(refused);
        await AssertRowReportedFaultedAsync(harness);
    }

    /// <summary>A describe line over max_line_bytes breaks the protocol and fails the loader instead of being truncated.</summary>
    [PythonFact]
    public async Task OversizeLineFailsTheLoader()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("oversize-describe")], new() { ["max_line_bytes"] = 4096 });

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        Assert.Contains("4096", Flatten(error));
    }

    /// <summary>More requests than max_in_flight are refused outright rather than queued.</summary>
    [PythonFact]
    public async Task InFlightBoundRefusesFurtherRequests()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("slow-deliver")], new() { ["max_in_flight"] = 1 });
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        var first = harness.DispatchLoginAsync("a");
        await Task.Delay(300);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => harness.DispatchLoginAsync("b"));

        Assert.Contains("max_in_flight", Flatten(error));
        Assert.True(await first);
        Assert.Contains(harness.Loggers.Entries, entry => entry.StartsWith("Warning") && entry.Contains("in flight"));
    }

    /// <summary>A stdout line that is not JSON breaks the protocol: the loader fails loudly and registers nothing.</summary>
    [PythonFact]
    public async Task GarbageOnStdoutFailsTheLoaderWithoutPublishing()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("garbage-describe")]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        var text = Flatten(error);
        Assert.Contains("broke the protocol", text);
        Assert.Contains("not JSON", text);
        Assert.Contains("stdout is reserved", text);
        AssertNothingWasRegistered(harness);
    }

    /// <summary>A stdout line that is JSON but not an object is rejected as a protocol line before any definition is parsed.</summary>
    [PythonFact]
    public async Task NonObjectJsonLineOnStdoutFailsTheLoaderWithoutPublishing()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("non-object-describe")]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        Assert.Contains("must be a JSON object", Flatten(error));
        AssertNothingWasRegistered(harness);
    }

    /// <summary>A batch whose second definition is invalid is rejected whole: the valid first one is never published.</summary>
    [PythonFact]
    public async Task OneInvalidDefinitionRejectsTheWholeBatch()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("mixed-describe")]);

        var error = await Assert.ThrowsAnyAsync<Exception>(harness.StartAsync);

        var config = FindConfigException(error);
        Assert.Contains("fake.bad", config.Message);
        Assert.Contains("INoSuchService", config.Message);
        AssertNothingWasRegistered(harness);
    }

    /// <summary>An unknown method name is answered with a JSON-RPC MethodNotFound error and changes no host state.</summary>
    [PythonFact]
    public async Task UnknownMethodNameIsAnsweredWithMethodNotFoundAndChangesNothing()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("unknown-method")]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        // The fake reports both refusals from inside its activate call and only then answers it, and the host reads
        // stdout lines in order, so the entry is already recorded when AddRowAsync returns.
        var reported = Assert.Single(harness.Loggers.Entries, entry => entry.Contains("unknown-request"));
        Assert.Contains("-32601", reported);
        Assert.Contains("unknown method 'no.such.method'", reported);
        // An unknown notification is not an error at all; it is logged and dropped.
        Assert.Contains(harness.Loggers.Entries, entry => entry.Contains("unknown notification 'no.such.notification'"));

        // Nothing about the refusal is sticky: the row is active, its handler still serves and nothing faulted.
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.Contains(harness.Router.Handlers, handler => handler.OwnerRowId == "echo");
        Assert.True(await harness.DispatchLoginAsync("alice"));
        Assert.DoesNotContain(harness.Loggers.Entries, entry => entry.Contains("SDK plugin 'fake.echo' faulted"));
    }

    /// <summary>Disposing one handle twice is loud: the second call is refused, the first already released the handler.</summary>
    [PythonFact]
    public async Task DisposingTheSameHandleTwiceIsRefused()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry("double-dispose")]);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        // The fake reports both disposals from inside its activate call and only then answers it, so both responses
        // are already recorded when AddRowAsync returns.
        var reported = Assert.Single(harness.Loggers.Entries, entry => entry.Contains("dispose-first"));
        Assert.Contains("\"result\": {}", reported);
        Assert.Contains("-32602", reported);
        Assert.Contains("already disposed", reported);

        // The first dispose removed the registration; the row itself stays active and owns the remaining release.
        Assert.DoesNotContain(harness.Router.Handlers, handler => handler.OwnerRowId == "echo");
        Assert.False(await harness.DispatchLoginAsync("alice"));
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
    }

    /// <summary>The session map refuses a new session at capacity rather than evicting or silently dropping its message.</summary>
    [PythonFact]
    public async Task SessionMapAtCapacityRefusesANewSessionWithoutEvictingTheOldOne()
    {
        using var fake = new FakePlugin();
        await using var harness = new RemoteHarness([fake.Entry()], new() { ["max_sessions"] = 1 });
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "fake.echo", new() { ["protocol"] = TestDialect.Protocol });

        // One remembered session; a repeat delivery from that session does not consume a second slot.
        Assert.True(await harness.DispatchLoginFromSessionAsync(7, "alice"));
        Assert.True(await harness.DispatchLoginFromSessionAsync(7, "bob"));

        // A second session is refused loudly, and the refusal neither evicts the old entry nor admits the newcomer.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DispatchLoginFromSessionAsync(8, "mallory"));
        Assert.Contains("session map is full", refused.Message);
        Assert.Contains("max_sessions", refused.Message);
        Assert.Contains("must close before another is delivered", refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DispatchLoginFromSessionAsync(8, "mallory"));

        // The first session is still the remembered one, and the row never faulted over the refusal.
        Assert.True(await harness.DispatchLoginFromSessionAsync(7, "carol"));
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
    }

    /// <summary>Asserts that a loader that failed during acquisition registered no definition and no published source.</summary>
    private static void AssertNothingWasRegistered(RemoteHarness harness)
    {
        Assert.NotEqual(PluginState.Active, harness.Instance("remote").State);
        Assert.DoesNotContain(harness.Host.Definitions, definition => definition.Id == "fake.echo");
        Assert.DoesNotContain(harness.Host.DefinitionSources, source => source.State == PluginDefinitionSourceState.Published);
        Assert.DoesNotContain(harness.Host.DefinitionSources, source => source.DefinitionIds.Contains("fake.echo"));
    }

    /// <summary>The kernel's own record of the fault: the row's owned watch task failed, which shutdown reports instead of a clean stop.</summary>
    private static async Task AssertRowReportedFaultedAsync(RemoteHarness harness)
    {
        var shutdown = await harness.ShutdownAsync();
        Assert.NotNull(shutdown);
        Assert.Contains("owned work failed", Flatten(shutdown));
    }

    private static ConfigException FindConfigException(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException!)
            if (current is ConfigException config)
                return config;
        throw new Xunit.Sdk.XunitException("No ConfigException in the chain: " + Flatten(error));
    }

    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null; current = current.InnerException!)
            parts.Add(current.Message);
        return string.Join(" | ", parts);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException("Condition not met within 15 seconds.");
            await Task.Delay(50);
        }
    }
}
