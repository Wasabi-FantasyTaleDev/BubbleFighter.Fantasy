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
/// patch handshake, the first inbound frame must be <c>gcpReqGamePatch</c>.
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
    private readonly VersionVerifiedScope _versionVerified;
    private readonly ILogger _logger;
    private readonly bool _requiresPatchHandshake;

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
        _requiresPatchHandshake = requiresPatchHandshake;
    }

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
        await connection.Output.WriteAsync(_greeting, ct).ConfigureAwait(false);

        var cipher = _dialect.CreateDecryptor(PacketDirection.ClientToServer);
        // Keep one outbound cipher for the entire framed stream after the plaintext preamble.
        var outbound = _dialect.CreateEncryptor(PacketDirection.ServerToClient);
        using var session = new WireSession(connection, _profile, Name, _dialect, outbound, _events,
            _options.MaxSessionFeatures, _options.SendQueueCapacity, _options.SendQueueBytes, _logger);
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
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }

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
        while (!ct.IsCancellationRequested && !connection.IsClosed)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

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

                var preHandshake = handshakePending && _options.PreHandshakeOpcodes.Contains(packet.Opcode);
                if (handshakePending && packet.Opcode != WireOpcodes.C2S.GamePatch && !preHandshake)
                {
                    if (!_options.RequirePatchHandshakeFirst)
                    {
                        // A later connection opening with something else is a
                        // reconnect, not a fault. Let the frame through and stop
                        // waiting for a handshake that is not coming.
                        handshakePending = false;
                    }
                    else
                    {
                        _events.Emit(new ClientVersionMismatchEvent(session, -1, _profile.ClientVersionMajor));
                        _logger.LogWarning(
                            "Session {SessionId} from {Remote} sent opcode {Opcode} before the patch handshake.",
                            session.Id, connection.RemoteEndPoint, packet.Opcode);
                        await connection.CloseAsync("first packet must be gcpReqGamePatch").ConfigureAwait(false);
                        return;
                    }
                }

                if (!await DispatchAsync(connection, session, packet, ct).ConfigureAwait(false))
                    return;

                if (handshakePending && !preHandshake)
                {
                    handshakePending = false;
                    if (!connection.IsClosed)
                        _versionVerified.Mark(connection.Id);
                }
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
                break;
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
