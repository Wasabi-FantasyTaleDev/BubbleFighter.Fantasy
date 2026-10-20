using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Builds the conformance example of each compiled-language SDK once per test run, natively and as WebAssembly.</summary>
internal static class CompiledSdk
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";
    private static readonly string Out = TestTempDirectories.ForProcess("bff-sdk-tests");

    private static readonly Lazy<(string? Path, string? Skip)> GoNative = new(() =>
        GoBuild(Path.Combine(Out, "go-conformance" + Exe), wasm: false));

    private static readonly Lazy<(string? Path, string? Skip)> GoWasm = new(() =>
        GoBuild(Path.Combine(Out, "go-conformance.wasm"), wasm: true));

    private static readonly Lazy<(string? Path, string? Skip)> RustNative = new(() => RustBuild(null));

    private static readonly Lazy<(string? Path, string? Skip)> RustWasm = new(() => RustBuild("wasm32-wasip1"));
    private static readonly Lazy<(string? Path, string? Skip)> RustWallet = new(() => RustBuild(null, "services_wallet"));
    private static readonly Lazy<(string? Path, string? Skip)> RustWalletWasm = new(() => RustBuild("wasm32-wasip1", "services_wallet"));
    private static readonly Lazy<(string? Path, string? Skip)> GoWallet = new(() => GoBuild(Path.Combine(Out, "go-wallet" + Exe), false, "./examples/services-wallet"));
    private static readonly Lazy<(string? Path, string? Skip)> GoWalletWasm = new(() => GoBuild(Path.Combine(Out, "go-wallet.wasm"), true, "./examples/services-wallet"));
    private static readonly Lazy<(string? Path, string? Skip)> GoProviders = new(() => GoBuild(Path.Combine(Out, "go-providers" + Exe), false, "./tests/providers"));
    private static readonly Lazy<(string? Path, string? Skip)> GoProvidersWasm = new(() => GoBuild(Path.Combine(Out, "go-providers.wasm"), true, "./tests/providers"));
    private static readonly Lazy<(string? Path, string? Skip)> RustProviders = new(() => RustBuild(null, "providers"));
    private static readonly Lazy<(string? Path, string? Skip)> RustProvidersWasm = new(() => RustBuild("wasm32-wasip1", "providers"));
    private static readonly Lazy<(string? Path, string? Skip)> GoEvents = new(() => GoBuild(Path.Combine(Out, "go-events" + Exe), false, "./tests/events"));
    private static readonly Lazy<(string? Path, string? Skip)> GoEventsWasm = new(() => GoBuild(Path.Combine(Out, "go-events.wasm"), true, "./tests/events"));
    private static readonly Lazy<(string? Path, string? Skip)> RustEvents = new(() => RustBuild(null, "events"));
    private static readonly Lazy<(string? Path, string? Skip)> RustEventsWasm = new(() => RustBuild("wasm32-wasip1", "events"));

    internal static (string? Path, string? Skip) Events(string language, bool wasm) => language == "go"
        ? (wasm ? GoEventsWasm.Value : GoEvents.Value) : (wasm ? RustEventsWasm.Value : RustEvents.Value);

    internal static (string? Path, string? Skip) Providers(string language, bool wasm) => language == "go"
        ? (wasm ? GoProvidersWasm.Value : GoProviders.Value) : (wasm ? RustProvidersWasm.Value : RustProviders.Value);

    internal static (string? Path, string? Skip) Wallet(string language, bool wasm) => language == "go"
        ? (wasm ? GoWalletWasm.Value : GoWallet.Value) : (wasm ? RustWalletWasm.Value : RustWallet.Value);

    /// <summary>Gets the native Go conformance binary, or the reason it could not be built.</summary>
    public static (string? Path, string? Skip) Go => GoNative.Value;

    /// <summary>Gets the Go conformance module (GOOS=wasip1), or the reason it could not be built.</summary>
    public static (string? Path, string? Skip) GoModule => GoWasm.Value;

    /// <summary>Gets the native Rust conformance binary, or the reason it could not be built.</summary>
    public static (string? Path, string? Skip) Rust => RustNative.Value;

    /// <summary>Gets the Rust conformance module (wasm32-wasip1), or the reason it could not be built.</summary>
    public static (string? Path, string? Skip) RustModule => RustWasm.Value;

    private static (string?, string?) GoBuild(string output, bool wasm, string example = "./conformance")
    {
        var env = wasm ? new Dictionary<string, string> { ["GOOS"] = "wasip1", ["GOARCH"] = "wasm" } : [];
        return Build("go", Path.Combine(Python.RepoRoot, "sdk", "go"), env, out var error, "build", "-o", output, example)
            ? (output, null) : (null, error);
    }

    private static (string?, string?) RustBuild(string? target, string example = "conformance")
    {
        var dir = Path.Combine(Python.RepoRoot, "sdk", "rust");
        var targetDir = Path.Combine(Out, "rust-target");
        var args = new List<string> { "build", "--release", "--example", example, "--target-dir", targetDir };
        if (target is not null)
            args.AddRange(["--target", target]);
        if (!Build("cargo", dir, [], out var error, [.. args]))
            return (null, target is null ? error : $"{error} (install the target with 'rustup target add {target}')");
        var exe = target is null
            ? Path.Combine(targetDir, "release", "examples", example + Exe)
            : Path.Combine(targetDir, target, "release", "examples", example + ".wasm");
        return File.Exists(exe) ? (exe, null) : (null, $"cargo built no {exe}");
    }

    private static bool Build(string tool, string directory, Dictionary<string, string> env, out string error, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(tool) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            foreach (var (key, value) in env) info.Environment[key] = value;
            using var process = Process.Start(info)!;
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(300_000))
            {
                process.Kill(entireProcessTree: true);
                error = $"{tool} build timed out";
                return false;
            }
            error = process.ExitCode == 0 ? "" : $"{tool} build failed: {stderr.Result}";
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error = $"No {tool} toolchain on PATH; the {tool} SDK tests need one.";
            return false;
        }
    }
}

/// <summary>Skips a test when the native Go SDK example cannot be built.</summary>
public sealed class GoFactAttribute : FactAttribute
{
    /// <summary>Builds the example once and sets <see cref="FactAttribute.Skip"/> when it fails.</summary>
    public GoFactAttribute() => Skip = CompiledSdk.Go.Skip;
}

/// <summary>Skips a test when the Go SDK example cannot be built as a module.</summary>
public sealed class GoWasmFactAttribute : FactAttribute
{
    /// <summary>Builds the module once and sets <see cref="FactAttribute.Skip"/> when it fails.</summary>
    public GoWasmFactAttribute() => Skip = CompiledSdk.GoModule.Skip;
}

/// <summary>Skips a test when the native Rust SDK example cannot be built.</summary>
public sealed class RustFactAttribute : FactAttribute
{
    /// <summary>Builds the example once and sets <see cref="FactAttribute.Skip"/> when it fails.</summary>
    public RustFactAttribute() => Skip = CompiledSdk.Rust.Skip;
}

/// <summary>Skips a test when the Rust SDK example cannot be built as a module.</summary>
public sealed class RustWasmFactAttribute : FactAttribute
{
    /// <summary>Builds the module once and sets <see cref="FactAttribute.Skip"/> when it fails.</summary>
    public RustWasmFactAttribute() => Skip = CompiledSdk.RustModule.Skip;
}

/// <summary>
/// The Go and Rust SDKs run through real loader rows, as child processes (<c>loader.process</c>) and as WebAssembly modules
/// inside the host (<c>loader.wasm</c>): describe publishes the definition, activate registers the handler, a dispatched
/// login is delivered, the reply reaches the dialect's sender, and unload is clean. The modules also replay every shared
/// fixture in tests/fixtures/sdk-protocol through <see cref="WasmChannel"/>, the same scripts the process SDKs pass.
///
/// </summary>
[Collection("SDK compilation")]
public sealed class CompiledSdkTests
{
    private static Dictionary<string, object?> Process(string path, string source) => new()
    {
        ["command"] = path,
        ["args"] = new List<object?>(),
        ["source_id"] = source,
    };

    private static Dictionary<string, object?> Module(string path, string source) => new()
    {
        ["module"] = path,
        ["source_id"] = source,
    };

    private static async Task RoundTrip(Dictionary<string, object?> entry, string loader)
    {
        await using var harness = new RemoteHarness([entry], loader: loader);
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "conformance.echo",
            new Dictionary<string, object?> { ["protocol"] = TestDialect.Protocol, ["greeting"] = "hi" });

        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.True(await harness.DispatchLoginAsync("alice"));

        var sent = Assert.Single(harness.Sent);
        Assert.Equal("alice", sent.Account);
        Assert.Equal(300001, sent.CharacterItemId);
        Assert.Contains(harness.Loggers.Entries, entry => entry.Contains("hi alice"));
        Assert.Null(await harness.ShutdownAsync());
    }

    /// <summary>The Go SDK makes the same round trip as the Python and TypeScript ones, as a process.</summary>
    [GoFact]
    public Task GoSdkRoundTrips() => RoundTrip(Process(CompiledSdk.Go.Path!, "conformance-go"), "loader.process");

    /// <summary>The Rust SDK makes the same round trip, as a process.</summary>
    [RustFact]
    public Task RustSdkRoundTrips() => RoundTrip(Process(CompiledSdk.Rust.Path!, "conformance-rust"), "loader.process");

    /// <summary>The same Go plugin built for wasip1 makes the round trip inside the host.</summary>
    [GoWasmFact]
    public Task GoModuleRoundTrips() => RoundTrip(Module(CompiledSdk.GoModule.Path!, "conformance-go-wasm"), "loader.wasm");

    /// <summary>The same Rust plugin built for wasm32-wasip1 makes the round trip inside the host.</summary>
    [RustWasmFact]
    public Task RustModuleRoundTrips() => RoundTrip(Module(CompiledSdk.RustModule.Path!, "conformance-rust-wasm"), "loader.wasm");

    /// <summary>The Go module refuses SDK tasks with the documented WebAssembly error instead of running them inline.</summary>
    [GoWasmFact]
    public Task GoModuleRefusesTasks() => RefuseTasks(CompiledSdk.GoModule.Path!, "conformance-go-wasm");

    /// <summary>The Rust module refuses SDK tasks with the documented WebAssembly error instead of running them inline.</summary>
    [RustWasmFact]
    public Task RustModuleRefusesTasks() => RefuseTasks(CompiledSdk.RustModule.Path!, "conformance-rust-wasm");

    /// <summary>Runs a login whose echo handler asks to start a task and checks the refusal reaches the reply.</summary>
    private static async Task RefuseTasks(string module, string source)
    {
        await using var harness = new RemoteHarness([Module(module, source)], loader: "loader.wasm");
        await harness.StartAsync();
        await harness.AddRowAsync("echo", "conformance.echo",
            new Dictionary<string, object?> { ["protocol"] = TestDialect.Protocol, ["greeting"] = "hi" });

        Assert.True(await harness.DispatchLoginAsync("task"));
        var sent = Assert.Single(harness.Sent);
        Assert.Contains("tasks are not supported in WebAssembly mode", sent.Account, StringComparison.Ordinal);
        Assert.Equal(PluginState.Active, harness.Instance("echo").State);
        Assert.Null(await harness.ShutdownAsync());
    }

    /// <summary>The Go module passes every shared protocol fixture.</summary>
    [GoWasmFact]
    public Task GoModulePassesTheFixtures() => FixtureReplay.RunAllAsync(CompiledSdk.GoModule.Path!);

    /// <summary>The Rust module passes every shared protocol fixture.</summary>
    [RustWasmFact]
    public Task RustModulePassesTheFixtures() => FixtureReplay.RunAllAsync(CompiledSdk.RustModule.Path!);

    /// <summary>The Go module routes out-of-order nested responses and releases bounded callbacks on shutdown.</summary>
    [GoWasmFact]
    public Task GoModulePassesInlineReentryFixtures() => FixtureReplay.RunAllAsync(CompiledSdk.GoModule.Path!, "sdk-protocol-inline");

    /// <summary>The Rust module routes out-of-order nested responses and releases bounded callbacks on shutdown.</summary>
    [RustWasmFact]
    public Task RustModulePassesInlineReentryFixtures() => FixtureReplay.RunAllAsync(CompiledSdk.RustModule.Path!, "sdk-protocol-inline");

    /// <summary>
    /// A profile can name an SDK plugin from startup: the row is admitted although its plugin is published only when the
    /// loader activates, waits as Pending, then activates and serves logins.
    ///
    /// </summary>
    [RustWasmFact]
    public async Task ARowBoundAtStartupActivatesOnceTheLoaderPublishes()
    {
        var echo = new PluginRow
        {
            Id = "echo", Plugin = "conformance.echo", Source = "test", ClientBuild = new ClientBuild("kr", 812, 0),
            Config = new Dictionary<string, object?> { ["protocol"] = TestDialect.Protocol, ["greeting"] = "hi" },
        };
        await using var harness = new RemoteHarness([Module(CompiledSdk.RustModule.Path!, "startup-wasm")], loader: "loader.wasm", initialRows: [echo]);
        await harness.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (harness.Instance("echo").State != PluginState.Active && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(harness.Instance("echo").State == PluginState.Active, harness.Instance("echo").Error);
        Assert.True(await harness.DispatchLoginAsync("alice"));
        Assert.Equal("alice", Assert.Single(harness.Sent).Account);
        Assert.Null(await harness.ShutdownAsync());
    }

    /// <summary>A loader.wasm entry that names a missing module fails the row's config, naming the file.</summary>
    [Fact]
    public void AMissingModuleIsAConfigError()
    {
        var row = new PluginRow
        {
            Id = "wasm", Plugin = "loader.wasm", Source = "test",
            Config = new Dictionary<string, object?> { ["plugins"] = new List<object?> { Module("no-such-plugin.wasm", "x") } },
        };
        var error = Assert.Throws<ConfigException>(() => RemoteLoaderConfig.Parse(row, wasm: true));
        Assert.Contains("no-such-plugin.wasm", error.Message);
    }
}

/// <summary>Plays the shared JSON-lines fixtures against a module through <see cref="WasmChannel"/>, as sdk/python/tests/run_conformance.py does against a process.</summary>
internal static class FixtureReplay
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Replays every fixture against a fresh instance of the module.</summary> <param
    /// name="module">The.wasm file.</param> <param name="fixtureDirectory">The shared fixture set, relative to
    /// tests/fixtures.</param>
    public static async Task RunAllAsync(string module, string fixtureDirectory = "sdk-protocol")
    {
        var dir = Path.Combine(Python.RepoRoot, "tests", "fixtures", fixtureDirectory);
        var files = Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
            await RunAsync(module, file);
    }

    private static async Task RunAsync(string module, string file)
    {
        var lines = File.ReadAllLines(file).Where(line => line.Trim().Length > 0).ToList();
        var ignore = JsonNode.Parse(lines[0])!["ignore"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
        using var channel = WasmChannel.Start(new WasmLaunch(module, 64L * 1024 * 1024), 1 << 20);
        using var output = new StreamReader(channel.Output, Encoding.UTF8);
        int? expectExit = null;
        foreach (var raw in lines.Skip(1))
        {
            var step = JsonNode.Parse(raw)!.AsObject();
            if (step["close_input"]?.GetValue<bool>() == true)
            {
                channel.CloseInput();
                continue;
            }
            if (step["expect_exit"] is { } code)
            {
                expectExit = code.GetValue<int>();
                break;
            }
            var message = step["msg"]!;
            if (step["dir"]!.GetValue<string>() == "h2p")
            {
                await channel.Input.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"));
                await channel.Input.FlushAsync();
                continue;
            }
            var line = await output.ReadLineAsync().WaitAsync(Limit);
            Assert.True(line is not null, $"{Path.GetFileName(file)}: the module ended before sending {message.ToJsonString()}");
            var actual = Strip(JsonNode.Parse(line!)!, ignore);
            var expected = Strip(message.DeepClone(), ignore);
            Assert.True(JsonNode.DeepEquals(expected, actual),
                $"{Path.GetFileName(file)}: expected {expected.ToJsonString()} but the module sent {actual.ToJsonString()}");
        }
        if (expectExit is null)
            channel.CloseInput();
        await channel.WaitForExitAsync(default).WaitAsync(Limit);
        Assert.Equal(expectExit ?? 0, channel.ExitCode);
        Assert.Null(await output.ReadLineAsync().WaitAsync(Limit));
    }

    private static JsonNode Strip(JsonNode node, List<string> ignore)
    {
        foreach (var path in ignore)
        {
            var parts = path.Split('.');
            JsonNode? current = node;
            foreach (var part in parts[..^1])
                current = current is JsonObject obj && obj.TryGetPropertyValue(part, out var next) ? next : null;
            if (current is JsonObject parent)
                parent.Remove(parts[^1]);
        }
        return node;
    }
}
