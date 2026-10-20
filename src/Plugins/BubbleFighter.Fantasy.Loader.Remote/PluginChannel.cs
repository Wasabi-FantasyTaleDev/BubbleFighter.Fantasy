using System.Diagnostics;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>
/// The byte streams of one running SDK plugin, whatever runs it: a child process (<c>loader.process</c>) or a
/// WebAssembly module inside the host (<c>loader.wasm</c>). The protocol on top is identical: one JSON-RPC object per
/// line on <see cref="Input"/> and <see cref="Output"/>, free-form diagnostics on <see cref="Error"/>.
/// </summary>
internal interface IPluginChannel : IDisposable
{
    /// <summary>Gets the stream the host writes protocol lines to.</summary>
    Stream Input { get; }

    /// <summary>Gets the stream the host reads the plugin's protocol lines from.</summary>
    Stream Output { get; }

    /// <summary>Gets the stream the host reads the plugin's diagnostic lines from.</summary>
    Stream Error { get; }

    /// <summary>Gets the exit code once <see cref="WaitForExitAsync"/> has completed.</summary>
    int ExitCode { get; }

    /// <summary>Completes when the plugin has ended.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes at exit.</returns>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Signals end of input; a well-behaved plugin then exits.</summary>
    void CloseInput();

    /// <summary>Ends the plugin without waiting for it.</summary>
    void Kill();
}

/// <summary>An SDK plugin running as a child process speaking over stdio.</summary>
internal sealed class ProcessChannel : IPluginChannel
{
    private readonly Process _process;

    private ProcessChannel(Process process) => _process = process;

    /// <inheritdoc />
    public Stream Input => _process.StandardInput.BaseStream;

    /// <inheritdoc />
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <inheritdoc />
    public Stream Error => _process.StandardError.BaseStream;

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <summary>Starts the child process with redirected stdio.</summary>
    /// <param name="launch">The resolved command line.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot start.</exception>
    public static ProcessChannel Start(ProcessLaunch launch)
    {
        var info = new ProcessStartInfo(launch.Command)
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Args)
            info.ArgumentList.Add(argument);
        foreach (var (key, value) in launch.Env)
            info.Environment[key] = value;
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidOperationException($"SDK plugin process '{launch.Command}' could not start in '{launch.WorkingDirectory}': {ex.Message}", ex);
        }
        if (process is null)
            throw new InvalidOperationException($"SDK plugin process '{launch.Command}' did not start.");
        return new ProcessChannel(process);
    }

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <inheritdoc />
    public void CloseInput() => _process.StandardInput.Close();

    /// <inheritdoc />
    public void Kill() => _process.Kill(entireProcessTree: true);

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
