using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Skips a test with a clear message when no Python interpreter can be started.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    /// <summary>Resolves the interpreter once and sets <see cref="FactAttribute.Skip"/> when there is none.</summary>
    public PythonFactAttribute()
    {
        if (Python.Command is null)
            Skip = "No Python 3.11+ interpreter (python, python3 or py) was found on PATH; remote loader process tests need one.";
    }
}

/// <summary>Skips a test with a clear message when no Node.js runtime can be started.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    /// <summary>Resolves the runtime once and sets <see cref="FactAttribute.Skip"/> when the example cannot run.</summary>
    public NodeFactAttribute()
    {
        if (Node.Command is null)
            Skip = "No Node.js runtime (node) was found on PATH; TypeScript SDK process tests need one.";
        else if (!File.Exists(Node.ExampleEntry))
            Skip = "The TypeScript SDK is not built (missing sdk/typescript/dist/examples/echo-lobby/main.js); run 'npm ci && npm run build' in sdk/typescript.";
    }
}

/// <summary>Finds a Node.js runtime and the built TypeScript example entry point.</summary>
internal static class Node
{
    /// <summary>Gets the runtime command, or null when none runs.</summary>
    public static readonly string? Command = Probe();

    /// <summary>Gets the compiled entry point of the TypeScript echo example.</summary>
    public static string ExampleEntry => Path.Combine(Python.RepoRoot, "sdk", "typescript", "dist", "examples", "echo-lobby", "main.js");

    private static string? Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return null;
            process.WaitForExit(5000);
            if (process.HasExited && process.ExitCode == 0) return "node";
        }
        catch (System.ComponentModel.Win32Exception) { /* not installed */ }
        return null;
    }
}

/// <summary>Finds a Python interpreter and the repository paths the process tests use.</summary>
internal static class Python
{
    /// <summary>Gets the interpreter command, or null when none runs.</summary>
    public static readonly string? Command = Probe();

    /// <summary>Gets the repository root, found by walking up to the solution file.</summary>
    public static readonly string RepoRoot = FindRoot();

    /// <summary>Gets the directory holding the example plugin packages.</summary>
    public static string ExamplesDir => Path.Combine(RepoRoot, "sdk", "python", "examples");

    private static string? Probe()
    {
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (process is null) continue;
                process.WaitForExit(5000);
                if (process.HasExited && process.ExitCode == 0) return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { /* not installed under this name */ }
        }
        return null;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A logger factory that keeps every entry, in order, and can call a hook for each one.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Gets or sets a hook run synchronously for every logged message.</summary>
    public Action<string>? OnEntry { get; set; }

    /// <summary>Gets a snapshot of the formatted entries.</summary>
    public IReadOnlyList<string> Entries => [.. _entries];

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose() { }

    private sealed class Recorder(RecordingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{logLevel}: {formatter(state, exception)}";
            owner._entries.Enqueue(text);
            owner.OnEntry?.Invoke(text);
        }
    }
}

/// <summary>A session without a socket; sent bytes are never used because messages go through typed senders.</summary>
internal sealed class FakeSession(long id) : ISession
{
    public long Id => id;
    public IConnection Connection => throw new NotSupportedException();
    public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "test");
    public ConcurrentDictionary<string, object> Items { get; } = new();
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>Stands in for a dialect: provides the inbound binding and the outbound sender the echo plugin declares.</summary>
[Plugin("test.dialect", Requires = [typeof(IMessageRouter)],
    ProvidesMany = [typeof(IInboundMessageBinding<LoginRequested>), typeof(IMessageSender<LoginAccepted>)])]
public sealed class TestDialect : IPlugin
{
    /// <summary>The protocol the stand-in dialect serves.</summary>
    public const string Protocol = "test/game";

    internal static readonly ConcurrentDictionary<string, DialectState> States = new();

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var state = new DialectState(context.Services.Get<IMessageRouter>());
        var key = (string)context.Row.Config!["key"]!;
        States[key] = state;
        context.Effect(() => States.TryRemove(key, out _));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Binding()));
        context.Effect(context.Services.RegisterMany<IMessageSender<LoginAccepted>>(new Sender(state)));
        return ValueTask.CompletedTask;
    }

    internal sealed class DialectState(IMessageRouter router)
    {
        public IMessageRouter Router { get; } = router;
        public ConcurrentQueue<LoginAccepted> Sent { get; } = new();
        public Func<ISession, LoginAccepted, CancellationToken, ValueTask>? SendHook { get; set; }
    }

    private sealed class Binding : IInboundMessageBinding<LoginRequested>
    {
        public string Protocol => TestDialect.Protocol;
    }

    private sealed class Sender(DialectState state) : IMessageSender<LoginAccepted>
    {
        public string Protocol => TestDialect.Protocol;

        public ValueTask SendAsync(ISession session, LoginAccepted message, CancellationToken ct)
        {
            state.Sent.Enqueue(message);
            return state.SendHook?.Invoke(session, message, ct) ?? ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A real <see cref="PluginHost"/> with the message router, a stand-in dialect and a <c>loader.process</c> row that
/// starts child processes, so tests exercise the loader through the same lifecycle the server uses.
/// </summary>
internal sealed class RemoteHarness : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly string _key = Guid.NewGuid().ToString("N");
    private readonly List<PluginRow> _rows;

    /// <summary>Creates a harness whose loader row runs the given process entries.</summary>
    /// <param name="processes">The <c>plugins</c> entries of the loader row.</param>
    /// <param name="loaderSettings">Extra top-level loader config keys, such as <c>request_timeout_ms</c>.</param>
    /// <param name="loader">The loader plugin: <c>loader.process</c>, or <c>loader.wasm</c> for module entries.</param>
    /// <param name="initialRows">Rows present from startup, such as a row bound to a plugin the loader publishes.</param>
    public RemoteHarness(IEnumerable<Dictionary<string, object?>> processes, Dictionary<string, object?>? loaderSettings = null, string loader = "loader.process",
        IEnumerable<PluginRow>? initialRows = null)
    {
        var config = new Dictionary<string, object?> { ["plugins"] = processes.Cast<object?>().ToList() };
        foreach (var (name, value) in loaderSettings ?? [])
            config[name] = value;
        Loggers = new RecordingLoggerFactory();
        _rows =
        [
            new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" },
            new PluginRow { Id = "dialect", Plugin = "test.dialect", Source = "test", Config = new Dictionary<string, object?> { ["key"] = _key } },
            new PluginRow
            {
                Id = "remote", Plugin = loader, Source = "test", Config = config,
                Lifecycle = new()
                {
                    EntryReserve = 4, ServiceMetadataReserve = 64, EffectCapacity = 64,
                    DefinitionSourceReserve = 2, DefinitionReserve = 8, TeardownTimeoutMs = 10000,
                },
            },
        ];
        _rows.AddRange(initialRows ?? []);
        var catalog = PluginCatalog.ScanAssemblies([typeof(RemoteLoaderPlugin).Assembly, typeof(MessageRouterPlugin).Assembly, typeof(TestDialect).Assembly]);
        Host = new PluginHost(_rows, catalog, Loggers);
    }

    /// <summary>Gets the host under test.</summary>
    public PluginHost Host { get; }

    /// <summary>Gets the recorded log entries.</summary>
    public RecordingLoggerFactory Loggers { get; }

    /// <summary>Gets the router the dialect captured.</summary>
    public IMessageRouter Router => TestDialect.States[_key].Router;

    /// <summary>Gets the messages the stand-in sender received.</summary>
    public IReadOnlyCollection<LoginAccepted> Sent => TestDialect.States[_key].Sent;

    /// <summary>Installs an awaited sender callback to exercise host work that reenters the same SDK activation.</summary>
    public void OnSend(Func<ISession, LoginAccepted, CancellationToken, ValueTask> hook) => TestDialect.States[_key].SendHook = hook;

    /// <summary>Starts the host, which activates the loader and publishes its definitions.</summary>
    public Task StartAsync() => Host.StartAsync(default).AsTask().WaitAsync(Limit);

    /// <summary>Adds a row bound to an SDK plugin and activates it.</summary>
    /// <param name="id">The row id.</param>
    /// <param name="plugin">The plugin id the remote process described.</param>
    /// <param name="config">The row config.</param>
    public async Task AddRowAsync(string id, string plugin, Dictionary<string, object?> config)
    {
        var rows = _rows.Append(new PluginRow
        {
            Id = id, Plugin = plugin, Source = "test", Config = config, ClientBuild = new ClientBuild("kr", 812, 0),
            Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 },
        }).ToArray();
        await Host.ReconcileConfigurationAsync(rows).AsTask().WaitAsync(Limit);
    }

    /// <summary>Gets the state a row reports.</summary>
    /// <param name="rowId">The row id.</param>
    public PluginInstanceInfo Instance(string rowId) => Host.Instances.Single(row => row.RowId == rowId);

    /// <summary>Delivers a login to the router as the dialect would after decoding a client packet.</summary>
    /// <param name="account">The account name.</param>
    public Task<bool> DispatchLoginAsync(string account) => DispatchLoginFromSessionAsync(7, account);

    /// <summary>Delivers a login from a chosen session id, so tests can fill the loader's bounded session map.</summary>
    /// <param name="sessionId">The session the message arrives on.</param>
    /// <param name="account">The account name.</param>
    public Task<bool> DispatchLoginFromSessionAsync(long sessionId, string account) =>
        Router.DispatchAsync(TestDialect.Protocol, new FakeSession(sessionId), new LoginRequested(account), CancellationToken.None).AsTask().WaitAsync(Limit);

    /// <summary>Builds a loader entry that runs the SDK's echo example.</summary>
    public static Dictionary<string, object?> EchoExample() => new()
    {
        ["command"] = Python.Command,
        ["args"] = new List<object?> { "-m", "echo_lobby" },
        ["cwd"] = Python.ExamplesDir,
        ["env"] = new Dictionary<string, object?> { ["PYTHONUNBUFFERED"] = "1" },
        ["source_id"] = "echo",
    };

    /// <summary>Builds a loader entry that runs the TypeScript SDK's echo example.</summary>
    public static Dictionary<string, object?> TypeScriptEchoExample() => new()
    {
        ["command"] = Node.Command,
        ["args"] = new List<object?> { Node.ExampleEntry },
        ["source_id"] = "echo-ts",
    };

    /// <summary>Shuts the host down once and returns what it reported, or null when teardown was clean.</summary>
    public async Task<Exception?> ShutdownAsync()
    {
        if (_shutdown) return null;
        _shutdown = true;
        return await Record.ExceptionAsync(() => Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (await ShutdownAsync() is { } failure)
            throw failure;
    }

    private bool _shutdown;
}

/// <summary>A scripted stand-in plugin process, used where the real SDK would be too well behaved.</summary>
internal sealed class FakePlugin : IDisposable
{
    private const string Script = """
        import json, os, sys, time
        mode = os.environ.get("FAKE_MODE", "")
        definition = json.loads(os.environ["FAKE_DEFINITION"])
        def send(o):
            sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
        def read():
            line = sys.stdin.readline()
            return json.loads(line) if line else None
        while True:
            m = read()
            if m is None: break
            method = m.get("method")
            if method == "shutdown": break
            if "id" not in m or method is None: continue
            rid = m["id"]
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": rid, "result": {"protocol_version": 1, "sdk": {"name": "fake", "version": "0"}}})
            elif method == "describe":
                if mode == "garbage-describe":
                    sys.stdout.write("this line is not json\n"); sys.stdout.flush()
                    continue
                if mode == "non-object-describe":
                    sys.stdout.write("[1, 2, 3]\n"); sys.stdout.flush()
                    continue
                if mode == "mixed-describe":
                    bad = {"id": "fake.bad", "version": "1.0.0", "requires": ["INoSuchService"]}
                    send({"jsonrpc": "2.0", "id": rid, "result": {"definitions": [definition, bad]}})
                    continue
                if mode == "oversize-describe":
                    definition["description"] = "x" * 20000
                send({"jsonrpc": "2.0", "id": rid, "result": {"definitions": [definition]}})
            elif method == "activate":
                act = m["params"]["activation"]
                send({"jsonrpc": "2.0", "id": 1, "method": "messages.on", "params": {"activation": act, "protocol": "test/game", "type": "LoginRequested"}})
                r = read()
                if mode == "silent-activate": continue
                handle = r["result"]["handle"]
                if mode == "double-dispose":
                    send({"jsonrpc": "2.0", "id": 2, "method": "effect.dispose", "params": {"activation": act, "handle": handle}})
                    first = read()
                    send({"jsonrpc": "2.0", "id": 3, "method": "effect.dispose", "params": {"activation": act, "handle": handle}})
                    second = read()
                    send({"jsonrpc": "2.0", "method": "log", "params": {"activation": act, "level": "info", "message": "dispose-first %s dispose-second %s" % (json.dumps(first), json.dumps(second))}})
                elif mode == "unknown-method":
                    send({"jsonrpc": "2.0", "id": 4, "method": "no.such.method", "params": {}})
                    unknown = read()
                    send({"jsonrpc": "2.0", "method": "log", "params": {"activation": act, "level": "info", "message": "unknown-request %s %s" % (unknown["error"]["code"], unknown["error"]["message"])}})
                    send({"jsonrpc": "2.0", "method": "no.such.notification", "params": {}})
                send({"jsonrpc": "2.0", "id": rid, "result": {}})
            elif method == "deliver":
                if mode == "silent-deliver": continue
                if mode == "crash-deliver": os._exit(3)
                if mode == "slow-deliver": time.sleep(1.5)
                send({"jsonrpc": "2.0", "id": rid, "result": {"handled": True}})
            elif method == "deactivate":
                send({"jsonrpc": "2.0", "method": "log", "params": {"activation": m["params"]["activation"], "level": "info", "message": "deactivating"}})
                send({"jsonrpc": "2.0", "id": rid, "result": {}})
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "bff-fake-plugin-" + Guid.NewGuid().ToString("N") + ".py");

    /// <summary>Writes the script to a temporary file.</summary>
    public FakePlugin() => File.WriteAllText(_path, Script);

    /// <summary>Builds a loader entry for the script.</summary>
    /// <param name="mode">The behavior switch (see the script).</param>
    /// <param name="definition">The definition the script describes; defaults to a valid echo definition.</param>
    public Dictionary<string, object?> Entry(string mode = "", object? definition = null) => new()
    {
        ["command"] = Python.Command,
        // This stand-in uses only the standard library; local site hooks must not change its startup or protocol.
        ["args"] = new List<object?> { "-I", "-S", "-u", _path },
        ["env"] = new Dictionary<string, object?>
        {
            ["FAKE_MODE"] = mode,
            ["FAKE_DEFINITION"] = JsonSerializer.Serialize(definition ?? Echo()),
        },
        ["source_id"] = "fake",
    };

    /// <summary>The definition of a plugin that handles LoginRequested.</summary>
    /// <param name="id">The plugin id.</param>
    public static Dictionary<string, object?> Echo(string id = "fake.echo") => new()
    {
        ["id"] = id, ["version"] = "1.0.0", ["description"] = "fake", ["supported_builds"] = new[] { "kr:812" },
        ["requires"] = new[] { "IMessageRouter" }, ["optional"] = Array.Empty<string>(), ["provides"] = Array.Empty<string>(),
        ["handles"] = new[] { "LoginRequested" }, ["sends"] = new[] { "LoginAccepted" },
        ["config"] = new Dictionary<string, object?> { ["protocol"] = new Dictionary<string, object?> { ["type"] = "string", ["required"] = true } },
    };

    /// <inheritdoc />
    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* temp file; the OS cleans it up */ }
    }
}
