using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace BubbleFighter.Fantasy.Scenario.Tests.Harness;

/// <summary>
/// One simulated game connection. It keeps every received frame (bounded) so a scenario can wait
/// for a reply that arrived after a known point and read its payload, serialises sends (the C2S
/// cipher is a stream), and can run a keepalive loop alongside the scenario.
/// </summary>
internal sealed class SimClient : IAsyncDisposable
{
    private const int MaxFrames = 100_000;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly IWireDialect _dialect;
    private readonly IStreamTransform _encryptor;
    private readonly List<SimFrame> _frames = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _readFailure;
    private bool _closed;
    private DateTime _lastSend = DateTime.MinValue;
    private Task? _keepalive;

    private SimClient(string name, TcpClient tcp, NetworkStream stream, IWireDialect dialect,
        WireStreamDecoder decoder)
    {
        Name = name;
        _tcp = tcp;
        _stream = stream;
        _dialect = dialect;
        _encryptor = dialect.CreateEncryptor(PacketDirection.ClientToServer);
        _readLoop = Task.Run(() => ReadLoopAsync(decoder));
    }

    /// <summary>Scenario-facing client name, used in markers and logs.</summary>
    public string Name { get; }

    /// <summary>Number of S2C frames received so far; pass it to a wait call to wait for later frames only.</summary>
    public int Cursor
    {
        get { lock (_gate) return _frames.Count; }
    }

    /// <summary>Connects, reads the server preamble, and starts decoding.</summary>
    public static async Task<SimClient> ConnectAsync(string name, DnsEndPoint target, IWireDialect dialect,
        int maxPayloadBytes, CancellationToken ct)
    {
        // Resolve no remote names: this helper can only drive this test run's loopback server.
        if (!IPAddress.TryParse(target.Host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("Scenario connections require a loopback IP address.", nameof(target));
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(address, target.Port, ct);
        var stream = tcp.GetStream();

        var decoder = new WireStreamDecoder(dialect, PacketDirection.ServerToClient, maxPayloadBytes);
        var preambleLen = dialect.PreambleLength(PacketDirection.ServerToClient);
        if (preambleLen > 0)
        {
            var preamble = new byte[preambleLen];
            await stream.ReadExactlyAsync(preamble, ct);

            decoder.Feed(preamble);
        }
        if (dialect.PreambleLength(PacketDirection.ClientToServer) > 0)
            throw new NotSupportedException($"Dialect '{dialect.Id}' expects a C2S preamble; sim has no source for it.");
        return new SimClient(name, tcp, stream, dialect, decoder);
    }

    /// <summary>
    /// Sends one frame. Sends from the scenario and the keepalive loop share one cipher stream, so
    /// they are serialised; consecutive sends are spaced by at least <paramref name="minGapMs"/>.
    /// </summary>
    public async Task SendAsync(ushort opcode, byte[] payload, CancellationToken ct, int minGapMs = 300)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            var wait = _lastSend + TimeSpan.FromMilliseconds(minGapMs) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            var plaintext = _dialect.EncodeFrame(opcode, payload);
            _encryptor.Apply(plaintext);
            await _stream.WriteAsync(plaintext, ct);

            _lastSend = DateTime.UtcNow;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Waits for the first frame at or after <paramref name="from"/> that matches
    /// <paramref name="match"/>. Throws <see cref="TimeoutException"/> when none arrives in time and
    /// <see cref="IOException"/> when the connection closes first.
    /// </summary>
    public async Task<SimFrame> WaitAsync(int from, Func<SimFrame, bool> match, TimeSpan timeout, string what, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var scanned = from;
        while (true)
        {
            Task pulse;
            lock (_gate)
            {
                for (; scanned < _frames.Count; scanned++)
                    if (match(_frames[scanned]))
                        return _frames[scanned];
                if (_closed)
                    throw new IOException($"{Name}: connection closed while waiting for {what}.", _readFailure);
                pulse = _pulse.Task;
            }
            try
            {
                await pulse.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{Name}: no {what} within {timeout.TotalSeconds:0.#}s.");
            }
        }
    }

    /// <summary>Waits for an S2C frame with <paramref name="opcode"/> arriving at or after <paramref name="from"/>.</summary>
    public Task<SimFrame> WaitAsync(int from, ushort opcode, TimeSpan timeout, CancellationToken ct) =>
        WaitAsync(from, f => f.Opcode == opcode, timeout, $"S2C {opcode}", ct);

    /// <summary>Frames received at or after <paramref name="from"/>.</summary>
    public IReadOnlyList<SimFrame> Since(int from)
    {
        lock (_gate)
            return _frames.GetRange(from, _frames.Count - from);
    }

    /// <summary>
    /// Starts sending <paramref name="frames"/> every <paramref name="period"/> until the client is
    /// disposed, mirroring the periodic requests a live client makes while idle.
    /// </summary>
    public void StartKeepalive(TimeSpan period, IReadOnlyList<(ushort Opcode, byte[] Payload)> frames)
    {
        if (_keepalive is not null || frames.Count == 0)
            return;
        _keepalive = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(period, _cts.Token);
                    foreach (var (opcode, payload) in frames)
                        await SendAsync(opcode, payload, _cts.Token);
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested) { }
            catch (IOException) { }
        });
    }

    /// <summary>Stops the keepalive and read loop before releasing the loopback test socket.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_keepalive is not null)
        {
            try { await _keepalive.ConfigureAwait(false); } catch { /* stopping */ }
        }
        try { await _readLoop.ConfigureAwait(false); } catch { /* connection closed */ }

        _stream.Dispose();
        _tcp.Dispose();
        _cts.Dispose();
        _sendGate.Dispose();
    }

    private async Task ReadLoopAsync(WireStreamDecoder decoder)
    {
        var buf = new byte[16384];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buf, _cts.Token);
                if (read == 0)
                    break;
                var chunk = buf[..read];

                foreach (var item in decoder.Feed(chunk))
                {
                    if (item is not WireStreamItem.Frame { Value: var frame })
                        continue;
                    lock (_gate)
                    {
                        if (_frames.Count >= MaxFrames)
                            throw new IOException($"{Name}: frame limit {MaxFrames} reached.");
                        _frames.Add(new SimFrame(_frames.Count, frame.Opcode, frame.Payload));
                    }
                }
                Pulse();
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception error)
        {
            _readFailure = error;
        }
        lock (_gate)
            _closed = true;
        Pulse();
    }

    private void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }
}
