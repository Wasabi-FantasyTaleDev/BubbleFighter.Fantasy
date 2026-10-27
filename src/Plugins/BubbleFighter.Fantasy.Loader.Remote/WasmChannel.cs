using System.IO.Pipelines;
using Wasmtime;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>A WebAssembly SDK plugin after path resolution.</summary>
/// <param name="Module">The full path of the <c>.wasm</c> file.</param>
/// <param name="MaxMemoryBytes">The most linear memory the module may grow to.</param>
internal sealed record WasmLaunch(string Module, long MaxMemoryBytes);

/// <summary>
/// Runs a WebAssembly SDK plugin inside the host on its own thread, so the same line protocol as a child process
/// applies without a process or a pipe. The module is a WASI command (it exports <c>_start</c>) and imports three
/// functions from the <c>bff</c> module:
/// <list type="bullet">
/// <item><c>recv(ptr: i32, cap: i32) -> i32</c> blocks until the host's next protocol line and copies it (without the
/// newline) to <c>ptr</c>. It returns the line length; minus the length when <c>cap</c> is too small (the line stays
/// pending, so the caller grows its buffer and calls again); 0 at end of input.</item>
/// <item><c>send(ptr: i32, len: i32)</c> emits one protocol line to the host.</item>
/// <item><c>log(ptr: i32, len: i32)</c> emits one diagnostic line, the counterpart of a process's stderr.</item>
/// </list>
/// WASI gets no arguments, environment, files or stdio: the module can reach nothing but these imports. Memory is
/// capped by <see cref="WasmLaunch.MaxMemoryBytes"/>, and <see cref="Kill"/> interrupts running guest code through the
/// engine epoch.
/// </summary>
internal sealed class WasmChannel : IPluginChannel
{
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly Pipe _error = new();
    private readonly Engine _engine;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maxLineBytes;
    private readonly Stream _guestIn;
    private readonly Stream _guestOut;
    private readonly Stream _guestErr;
    private byte[]? _pendingLine;
    private volatile bool _killed;
    private int _exitCode;

    private WasmChannel(Engine engine, int maxLineBytes)
    {
        _engine = engine;
        _maxLineBytes = maxLineBytes;
        _guestIn = new BufferedStream(_input.Reader.AsStream(), 64 * 1024);
        _guestOut = _output.Writer.AsStream();
        _guestErr = _error.Writer.AsStream();
        Input = _input.Writer.AsStream();
        Output = _output.Reader.AsStream();
        Error = _error.Reader.AsStream();
    }

    /// <inheritdoc />
    public Stream Input { get; }

    /// <inheritdoc />
    public Stream Output { get; }

    /// <inheritdoc />
    public Stream Error { get; }

    /// <inheritdoc />
    public int ExitCode => _exitCode;

    /// <summary>Compiles and instantiates the module, then starts its <c>_start</c> on a dedicated thread.</summary>
    /// <param name="launch">The module and its limits.</param>
    /// <param name="maxLineBytes">The longest protocol line in either direction.</param>
    /// <returns>The running module.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the module cannot be read, compiled or linked.</exception>
    public static WasmChannel Start(WasmLaunch launch, int maxLineBytes)
    {
        var engine = new Engine(new Config().WithEpochInterruption(true));
        var channel = new WasmChannel(engine, maxLineBytes);
        Module? module = null;
        Store? store = null;
        Instance instance;
        try
        {
            module = Module.FromFile(engine, launch.Module);
            store = new Store(engine);
            store.SetLimits(memorySize: launch.MaxMemoryBytes);
            // One epoch tick past the start interrupts the guest; only Kill ticks the engine.
            store.SetEpochDeadline(1);
            store.SetWasiConfiguration(new WasiConfiguration());
            // The instance keeps its imports in the store; the temporary linker can release its native state now.
            using var linker = new Linker(engine);
            linker.DefineWasi();
            linker.Define("bff", "recv", Function.FromCallback(store, (Caller caller, int ptr, int cap) => channel.Recv(caller, ptr, cap)));
            linker.Define("bff", "send", Function.FromCallback(store, (Caller caller, int ptr, int len) => channel.Write(caller, ptr, len, channel._guestOut)));
            linker.Define("bff", "log", Function.FromCallback(store, (Caller caller, int ptr, int len) => channel.Write(caller, ptr, len, channel._guestErr)));
            instance = linker.Instantiate(store, module);
        }
        catch (Exception ex) when (ex is WasmtimeException or IOException or UnauthorizedAccessException)
        {
            store?.Dispose();
            module?.Dispose();
            engine.Dispose();
            throw new InvalidOperationException($"WebAssembly plugin '{launch.Module}' could not be loaded: {ex.Message}", ex);
        }
        var start = instance.GetAction("_start");
        if (start is null)
        {
            store.Dispose();
            module.Dispose();
            engine.Dispose();
            throw new InvalidOperationException($"WebAssembly plugin '{launch.Module}' exports no '_start'; build it as a WASI command (wasm32-wasip1 or GOOS=wasip1).");
        }
        var thread = new Thread(() => channel.Run(start, store, module)) { IsBackground = true, Name = $"wasm:{Path.GetFileName(launch.Module)}" };
        thread.Start();
        return channel;
    }

    private void Run(Action start, Store store, Module module)
    {
        try
        {
            start();
            _exitCode = 0;
        }
        catch (WasmtimeException ex) when (ex.ExitCode is { } code)
        {
            _exitCode = code;
        }
        catch (Exception ex)
        {
            _exitCode = 1;
            if (!_killed)
                WriteDiagnostic($"module trapped: {ex.Message}");
        }
        finally
        {
            store.Dispose();
            module.Dispose();
            _output.Writer.Complete();
            _error.Writer.Complete();
            _input.Reader.Complete();
            _exited.TrySetResult();
        }
    }

    private int Recv(Caller caller, int ptr, int cap)
    {
        var line = _pendingLine ?? ReadLine();
        _pendingLine = null;
        if (line is null)
            return 0;
        if (line.Length > cap)
        {
            _pendingLine = line;
            return -line.Length;
        }
        line.CopyTo(Memory(caller).GetSpan(ptr, line.Length));
        return line.Length;
    }

    private byte[]? ReadLine()
    {
        var buffer = new MemoryStream();
        while (!_killed)
        {
            var value = _guestIn.ReadByte();
            if (value < 0)
                return null;
            if (value == '\n')
            {
                if (buffer.Length == 0)
                    continue;
                return buffer.ToArray();
            }
            if (buffer.Length >= _maxLineBytes)
                throw new InvalidDataException($"a host line exceeded max_line_bytes {_maxLineBytes}");
            buffer.WriteByte((byte)value);
        }
        return null;
    }

    private void Write(Caller caller, int ptr, int len, Stream target)
    {
        if (len < 0 || len > _maxLineBytes)
            throw new InvalidOperationException($"a plugin line of {len} bytes is outside 0..max_line_bytes {_maxLineBytes}");
        var bytes = Memory(caller).GetSpan(ptr, len).ToArray();
        target.Write(bytes);
        target.WriteByte((byte)'\n');
        target.Flush();
    }

    private void WriteDiagnostic(string text)
    {
        try
        {
            _guestErr.Write(System.Text.Encoding.UTF8.GetBytes(text + "\n"));
            _guestErr.Flush();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* the reader is gone */ }
    }

    private static Memory Memory(Caller caller) =>
        caller.GetMemory("memory") ?? throw new InvalidOperationException("the module exports no 'memory'");

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

    /// <inheritdoc />
    public void CloseInput() => _input.Writer.Complete();

    /// <inheritdoc />
    public void Kill()
    {
        _killed = true;
        _input.Writer.Complete();
        _engine.IncrementEpoch();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_exited.Task.IsCompleted)
            Kill();
        // The guest thread disposes its store; the engine goes once the guest has stopped.
        _ = _exited.Task.ContinueWith(_ => _engine.Dispose(), TaskScheduler.Default);
    }
}
