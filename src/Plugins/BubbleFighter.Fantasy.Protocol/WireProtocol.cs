using System.IO.Pipelines;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// The framed connection protocol. On connect it writes the bootstrap greeting, then
/// decrypts the stream with the client-to-server cipher into a bounded intermediate
/// pipe, parses frames from that pipe, and dispatches each packet through the
/// <see cref="IPacketRouter"/> under this protocol's name. Frame integrity failures close
/// the connection, and a packet handler that throws closes only this connection. When
/// <see cref="ProtocolOptions.EnforceClientVersion"/> is on and the protocol requires the
/// patch handshake, the first inbound frame must be the row's patch request
/// (<c>gcpReqGamePatch</c> on kr-1256, <c>gcpFirstConnect</c> on the legacy builds).
/// </summary>
internal sealed class WireProtocol : IConnectionProtocol
{
    private readonly ClientProfile _profile;
    private readonly IWireDialect _dialect;
    private readonly byte[] _greeting;
    private readonly CancellationToken _lifetime;
    private readonly ProtocolOptions _options;
    private readonly IPacketRouter _router;
    private readonly IOpcodeTable _opcodes;
    private readonly IEventBus _events;
    private readonly ISessionRegistry? _sessions;
    // One per protocol row: the ceiling on what all of the row's sessions hold queued together.
    private readonly OutboundBudget _budget;
    private readonly VersionVerifiedScope _versionVerified;
    private readonly ILogger _logger;
    private readonly bool _requiresPatchHandshake;
    private readonly ushort _patchRequestOpcode;
    private readonly ushort[] _preHandshakeOpcodes;
    private readonly ushort[] _alternateHandshakeSequence;

    public WireProtocol(
        string name,
        IWireDialect dialect,
        byte[] greeting,
        ProtocolOptions options,
        IPacketRouter router,
        IOpcodeTable opcodes,
        IEventBus events,
        ISessionRegistry? sessions,
        VersionVerifiedScope versionVerified,
        ILogger logger,
        CancellationToken lifetime,
        bool requiresPatchHandshake)
    {
        Name = name;
        _profile = dialect.Profile;
        _dialect = dialect;
        _greeting = greeting;
        _lifetime = lifetime;
        _options = options;
        _router = router;
        _opcodes = opcodes;
        _events = events;
        _sessions = sessions;
        _versionVerified = versionVerified;
        _logger = logger;
        _budget = new OutboundBudget(_options.TotalSendQueueBytes, _logger);
        _requiresPatchHandshake = requiresPatchHandshake;
        _patchRequestOpcode = requiresPatchHandshake ? opcodes.C2S(options.PatchRequestName) : default(ushort);
        _preHandshakeOpcodes = requiresPatchHandshake ? ResolveNames(opcodes, options.PreHandshakeOpcodes, "pre_handshake_opcodes") : [];
        _alternateHandshakeSequence = requiresPatchHandshake ? ResolveNames(opcodes, options.AlternateHandshakeSequence, "alternate_handshake_sequence") : [];
    }

    /// <summary>Resolves configured packet names through this build's game C2S view, so one profile line serves every build.</summary>
    private static ushort[] ResolveNames(IOpcodeTable opcodes, IReadOnlyList<string> names, string key) =>
        names.Select(name => ushort.TryParse(name, out _)
            ? throw new ConfigException($"{key} lists '{name}', a number; write the packet name, which each client build resolves to its own opcode.")
            : opcodes.Find(PacketDirection.ClientToServer, name)
                ?? throw new ConfigException($"{key} lists '{name}', which opcode table '{opcodes.Profile.Id}' has no game-connection C2S row for.")).ToArray();

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask RunAsync(IConnection connection, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        try { await RunConnectionAsync(connection, linked.Token).ConfigureAwait(false); }
        finally
        {
            if (_lifetime.IsCancellationRequested)
                await connection.CloseAsync("protocol unloading").ConfigureAwait(false);
        }
    }

    private async ValueTask RunConnectionAsync(IConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var greetingWrite = await connection.Output.WriteAsync(_greeting, ct).ConfigureAwait(false);
        if (greetingWrite.IsCanceled || greetingWrite.IsCompleted)
        {
            await connection.CloseAsync("bootstrap greeting write stopped").ConfigureAwait(false);
            return;
        }
        _events.Emit(new BootstrapGreetingWrittenEvent(connection.Id, _profile.Id, Name, _dialect.Id,
            _greeting.Length, (ushort)_options.BootstrapStatus));

        var cipher = _dialect.CreateDecryptor(PacketDirection.ClientToServer);
        // Keep one outbound cipher for the entire framed stream after the plaintext preamble.
        var outbound = _dialect.CreateEncryptor(PacketDirection.ServerToClient);
        using var session = new WireSession(connection, _profile, Name, _dialect, outbound, _events,
            _options.MaxSessionFeatures, _options.SendQueueCapacity, _options.SendQueueBytes, _budget, _logger);
        _sessions?.Add(session);
        var frames = new Pipe(new PipeOptions(
            pauseWriterThreshold: _options.InboundBufferBytes,
            resumeWriterThreshold: Math.Max(1, _options.InboundBufferBytes / 2),
            useSynchronizationContext: false));
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(connection, frames.Writer, cipher, pumpStop.Token);
        try
        {
            await ParseAsync(connection, session, frames.Reader, ct).ConfigureAwait(false);
        }
        finally
        {
            // Frames not yet written when the connection ends are dropped with it.
            await session.StopAsync().ConfigureAwait(false);
            session.Dispose();
            _sessions?.Remove(session);
            pumpStop.Cancel();
            await frames.Reader.CompleteAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {Id} inbound pump terminated abnormally.", connection.Id);
            }
        }
    }

    /// <summary>
    /// Copies raw inbound bytes into the bounded frame pipe, decrypting each segment in
    /// place as it is copied. The pipe's pause threshold stops this pump when the parser
    /// falls behind, propagating backpressure to the socket.
    /// </summary>
    private async Task PumpAsync(IConnection connection, PipeWriter writer, IStreamTransform cipher, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !connection.IsClosed)
            {
                ReadResult result;
                try
                {
                    result = await connection.Input.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException error)
                {
                    _logger.LogWarning(error, "Connection {Id} input failed before the peer completed its stream.", connection.Id);
                    await writer.CompleteAsync(error).ConfigureAwait(false);
                    return;
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested || connection.IsClosed) { break; }
                catch (InvalidOperationException) when (ct.IsCancellationRequested || connection.IsClosed) { break; }

                foreach (var segment in result.Buffer)
                {
                    var memory = writer.GetMemory(segment.Length);
                    segment.CopyTo(memory);
                    cipher.Apply(memory.Span[..segment.Length]);
                    writer.Advance(segment.Length);
                }
                connection.Input.AdvanceTo(result.Buffer.End);
                var flush = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (result.IsCompleted || flush.IsCompleted || flush.IsCanceled)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection {Id} inbound pump failed.", connection.Id);
            await connection.CloseAsync("inbound stream failure").ConfigureAwait(false);
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ParseAsync(IConnection connection, ISession session, PipeReader reader, CancellationToken ct)
    {
        var handshakePending = _requiresPatchHandshake && _options.EnforceClientVersion;
        var alternateHandshakeIndex = 0;
        var firstFrame = true;
        // Closing is what ends the pending read below. The timer is released by the first frame; disposing it again
        // when the method leaves is harmless.
        using var firstFrameDeadline = new Timer(
            static state =>
            {
                var (pending, seconds) = ((IConnection, int))state!;
                _ = pending.CloseAsync($"no complete frame within first_frame_seconds ({seconds})").AsTask();
            },
            (connection, _options.FirstFrameSeconds), TimeSpan.FromSeconds(_options.FirstFrameSeconds), Timeout.InfiniteTimeSpan);
        while (!ct.IsCancellationRequested && !connection.IsClosed)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException error)
            {
                _logger.LogWarning(error, "Connection {Id} inbound frame stream ended with an input error.", connection.Id);
                await connection.CloseAsync("inbound transport read failed").ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested || connection.IsClosed) { break; }
            catch (InvalidOperationException) when (ct.IsCancellationRequested || connection.IsClosed) { break; }

            var buffer = result.Buffer;
            while (true)
            {
                InboundPacket packet;
                try
                {
                    var previousLength = buffer.Length;
                    if (!_dialect.TryReadFrame(ref buffer, _options.MaxPayloadBytes, out var frame))
                        break;
                    if (buffer.Length >= previousLength)
                        throw new PacketFormatException("Dialect returned a frame without consuming input", 0);
                    packet = new InboundPacket(frame.Opcode, frame.Payload);
                }
                catch (PacketFormatException ex)
                {
                    await connection.CloseAsync(ex.Message).ConfigureAwait(false);
                    return;
                }

                if (firstFrame)
                {
                    firstFrame = false;
                    firstFrameDeadline.Dispose();
                    _events.Emit(new FirstInboundFrameEvent(connection.Id, _profile.Id, Name,
                        packet.Opcode, packet.Payload.Length));
                }

                var preHandshake = handshakePending && _preHandshakeOpcodes.Contains(packet.Opcode);
                // cn-324 sub_7001F0 sends the GPK marker before either the Center query or a retained-player move.
                // A permitted move still requires its handler to authorize continuation.
                if (handshakePending && !preHandshake && alternateHandshakeIndex > 0
                    && packet.Opcode != _alternateHandshakeSequence[alternateHandshakeIndex])
                {
                    await connection.CloseAsync("alternate handshake sequence was interrupted").ConfigureAwait(false);
                    return;
                }
                if (handshakePending && alternateHandshakeIndex < _alternateHandshakeSequence.Length
                    && packet.Opcode == _alternateHandshakeSequence[alternateHandshakeIndex]
                    && packet.Payload.Length != 0)
                {
                    await connection.CloseAsync("alternate handshake sequence packets must have empty payloads").ConfigureAwait(false);
                    return;
                }
                if (handshakePending && packet.Opcode != _patchRequestOpcode && !preHandshake)
                {
                    var sequence = _alternateHandshakeSequence;
                    if (sequence.Length > 0 && alternateHandshakeIndex < sequence.Length && packet.Opcode == sequence[alternateHandshakeIndex])
                    {
                        alternateHandshakeIndex++;
                        if (alternateHandshakeIndex == sequence.Length)
                            handshakePending = false;
                    }
                    else if (!_options.RequirePatchHandshakeFirst)
                    {
                        handshakePending = false;
                    }
                    else
                    {
                        _events.Emit(new ClientVersionMismatchEvent(session, -1, _profile.ClientVersionMajor));
                        _logger.LogWarning(
                            "Session {SessionId} from {Remote} sent opcode {Opcode} outside the patch or configured alternate handshake sequence.",
                            session.Id, connection.RemoteEndPoint, packet.Opcode);
                        await connection.CloseAsync($"first packet must be {_options.PatchRequestName} or match the configured alternate sequence").ConfigureAwait(false);
                        return;
                    }
                }

                if (preHandshake)
                {
                    using var continuation = new InitialHandshakeContinuation();
                    using var registration = session.RegisterFeature<IInitialHandshakeContinuation>(continuation);
                    if (!await DispatchAsync(connection, session, packet, ct).ConfigureAwait(false))
                        return;
                    if (continuation.Complete() && !connection.IsClosed)
                        handshakePending = false;
                }
                else if (!await DispatchAsync(connection, session, packet, ct).ConfigureAwait(false))
                    return;

                if (handshakePending && !preHandshake && alternateHandshakeIndex == 0)
                {
                    handshakePending = false;
                    if (!connection.IsClosed)
                        _versionVerified.Mark(connection.Id);
                }
            }
            // The pump stops reading once this much is buffered, so a frame that is still incomplete here can never
            // complete. The row refuses limits that allow it; ending the connection keeps a wrong pair from
            // parking it forever with the buffer held.
            if (buffer.Length >= _options.InboundBufferBytes)
            {
                await connection.CloseAsync(
                    $"an incomplete frame filled inbound_buffer_bytes ({buffer.Length} buffered bytes)").ConfigureAwait(false);
                return;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                await connection.CloseAsync(buffer.IsEmpty ? "peer completed inbound stream"
                    : $"peer ended with an incomplete frame ({buffer.Length} buffered bytes)").ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary>Dispatches one packet; returns <see langword="false"/> when the loop must stop.</summary>
    private async ValueTask<bool> DispatchAsync(IConnection connection, ISession session, InboundPacket packet, CancellationToken ct)
    {
        try
        {
            await _router.DispatchAsync(Name, session, packet, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            var description = _opcodes.Describe(PacketDirection.ClientToServer, packet.Opcode);
            _logger.LogError(ex,
                "Handler for {Packet} failed on session {SessionId} ({Remote}); closing the connection.",
                description, session.Id, connection.RemoteEndPoint);
            await connection.CloseAsync($"handler failure: {description}").ConfigureAwait(false);
            return false;
        }
    }
}
