using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Exercises a host sender that must finish a nested SDK callback before answering the original handler.</summary>
[Collection("SDK compilation")]
public sealed class SdkReentryTests
{
    /// <summary>Every supported SDK and execution mode completes nested routing through actual mounted rows.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task SenderCanAwaitNestedHandlerWithoutBlockingReader(string language, bool wasm)
    {
        var entry = Entry(language, wasm);
        await using var harness = new RemoteHarness([entry], new() { ["max_in_flight"] = 2, ["max_reentry_depth"] = 2 },
            loader: wasm ? "loader.wasm" : "loader.process");
        await harness.StartAsync();
        var nested = 0;
        harness.OnSend(async (session, message, ct) =>
        {
            if (message.Account == "outer")
            {
                Assert.True(await harness.Router.DispatchAsync(TestDialect.Protocol, session, new LoginRequested("inner"), ct));
                Interlocked.Increment(ref nested);
            }
        });
        await harness.AddRowAsync("echo", "conformance.echo", new()
        {
            ["protocol"] = TestDialect.Protocol, ["greeting"] = "hello", ["extra_protocols"] = new List<object?>(),
        });
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.True(await harness.DispatchLoginAsync("outer"));
        Assert.Equal(1, nested);
        Assert.Equal(["outer", "inner"], harness.Sent.Select(message => message.Account));
        Assert.True(await harness.DispatchLoginAsync("later"));
        Assert.Equal(3, harness.Sent.Count);
        Assert.Null(await harness.ShutdownAsync());
    }

    /// <summary>The loader's depth setting reaches the guest, which rejects nested work and remains usable.</summary>
    [Theory]
    [InlineData("go")]
    [InlineData("rust")]
    public async Task ConfiguredDepthRejectsNestedCallbackAndReleasesCapacity(string language)
    {
        await using var harness = new RemoteHarness([Entry(language, true)],
            new() { ["max_in_flight"] = 4, ["max_reentry_depth"] = 1 }, loader: "loader.wasm");
        await harness.StartAsync();
        harness.OnSend(async (session, _, ct) =>
        {
            Assert.True(await harness.Router.DispatchAsync(TestDialect.Protocol, session, new LoginRequested("inner"), ct));
        });
        await harness.AddRowAsync("echo", "conformance.echo", new()
        {
            ["protocol"] = TestDialect.Protocol, ["greeting"] = "hello", ["extra_protocols"] = new List<object?>(),
        });
        var error = await Record.ExceptionAsync(() => harness.DispatchLoginAsync("outer"));
        Assert.NotNull(error);
        Assert.Contains("max_reentry_depth", error.ToString());
        Assert.Single(harness.Sent);
        harness.OnSend((_, _, _) => ValueTask.CompletedTask);
        Assert.True(await harness.DispatchLoginAsync("later"));
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.Null(await harness.ShutdownAsync());
    }

    private static Dictionary<string, object?> Entry(string language, bool wasm)
    {
        if (language is "go" or "rust")
        {
            var built = (language, wasm) switch
            {
                ("go", false) => CompiledSdk.Go,
                ("go", true) => CompiledSdk.GoModule,
                ("rust", false) => CompiledSdk.Rust,
                _ => CompiledSdk.RustModule,
            };
            Assert.True(built.Path is not null, built.Skip);
            return new() { [wasm ? "module" : "command"] = built.Path, ["source_id"] = "nested" };
        }
        var command = language == "python" ? Python.Command : Node.Command;
        Assert.NotNull(command);
        return new()
        {
            ["command"] = command, ["source_id"] = "nested",
            ["args"] = new List<object?> { Path.Combine(Python.RepoRoot,
                language == "python" ? "sdk/python/tests/conformance_plugins.py" : "sdk/typescript/dist/tests/conformance-plugins.js") },
        };
    }
}
