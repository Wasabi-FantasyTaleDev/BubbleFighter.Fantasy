using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts authenticated native UDP envelopes to the owning build's existing movement routes.</summary>
internal sealed class UdpMovementTransport(IUdpDatagramEndpoint endpoint, IMatchUdpRelayDirectory directory,
    PacketTables tables, string build, IOpcodeTable relay, ILogger logger) : IMatchUdpRelayControl
{
    public string Protocol => directory.Protocol;
    public ushort BoundPort => checked((ushort)endpoint.LocalEndPoint.Port);
    public int IdentityBytes => PacketCodec.Write(tables, build, "UdpRelayProbeReply", new Dictionary<string, object?>()).Length;

    public ValueTask OfferAsync(ISession session, ushort port, ReadOnlyMemory<byte> identity, CancellationToken ct) =>
        session.SendAsync(relay.S2C("rspUdpBypassInfo"), PacketCodec.Write(tables, build, "UdpRelayOffer",
            new Dictionary<string, object?>
            {
                ["unknown_0"] = port,
                ["unknown_2"] = identity.Length == 4
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(identity.Span)
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity.Span),
            }), ct);

    public ValueTask WithdrawAsync(ISession session, CancellationToken ct) =>
        session.SendAsync(relay.S2C("rspUdpBypassInfo"), PacketCodec.Write(tables, build, "UdpRelayOffer",
            new Dictionary<string, object?>()), ct);

    internal static UdpMovementTransport Mount(IPluginContext context, string udpProtocol, string relayProtocol,
        PacketTables tables, string build, IOpcodeTable relay, int keepaliveMs,
        Func<ISession, ushort, ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive)
    {
        if (!tables.CapabilityOf(build, "udp_relay_movement"))
            throw new ConfigException($"UDP relay movement is not audited for '{build}'.");
        ProtocolDatagramCodec.ValidateHeader(tables, build);
        var endpoints = context.Services.GetAll<IUdpDatagramEndpoint>().Where(value => value.Protocol == udpProtocol).ToArray();
        var directories = context.Services.GetAll<IMatchUdpRelayDirectory>().Where(value => value.Protocol == relayProtocol).ToArray();
        if (endpoints.Length != 1 || directories.Length != 1)
            throw new ConfigException($"UDP '{udpProtocol}' requires exactly one endpoint and '{relayProtocol}' directory; found {endpoints.Length}/{directories.Length}.");
        var transport = new UdpMovementTransport(endpoints[0], directories[0], tables, build, relay, context.Logger);
        if (keepaliveMs is < 100 or > 60000)
            throw new ConfigException("udp_keepalive_ms must be between 100 and 60000.");
        transport.Register(context, receive);
        context.Effect(context.Services.RegisterMany<IMatchUdpRelayControl>(transport));
        context.RunTaskAsync("udp-relay-keepalive", token => transport.KeepaliveAsync(token, keepaliveMs))
            .AsTask().GetAwaiter().GetResult();
        return transport;
    }

    internal ValueTask SendAsync(ISession session, ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (directory.TryGetEndpoint(session, out var remote) && remote is not null)
        {
            if (!relay.TryGet(PacketDirection.ServerToClient, opcode, out var entry) || entry is null)
                throw new ConfigException($"UDP movement has no native relay opcode {opcode}.");
            var udpOpcode = relay.ForConnection(OpcodeConnection.Udp).S2C(entry.Name);
            return endpoint.SendAsync(ProtocolDatagramCodec.Write(tables, build, udpOpcode, body.Span), remote, ct);
        }
        return session.SendAsync(opcode, body, ct);
    }

    // Probes only bound sessions, which are a subset of the registrations, so the loop sleeps on the directory's
    // registration presence and holds no timer while nothing is registered; with every build loaded the probe used to
    // wake once per keepalive period per build on an empty server. The period and the probe are unchanged.
    private async ValueTask KeepaliveAsync(CancellationToken lifetime, int milliseconds)
    {
        PeriodicTimer? timer = null;
        try
        {
            var opcode = relay.ForConnection(OpcodeConnection.Udp).S2C("rspUdpBypassVerify_");
            while (true)
            {
                var registered = directory.WhenAnyRegistered();
                if (!registered.IsCompleted)
                {
                    // A periodic timer fires whether or not anyone waits on it, so it is released, not just ignored.
                    timer?.Dispose();
                    timer = null;
                    await registered.WaitAsync(lifetime).ConfigureAwait(false);
                }
                timer ??= new PeriodicTimer(TimeSpan.FromMilliseconds(milliseconds));
                if (!await timer.WaitForNextTickAsync(lifetime).ConfigureAwait(false))
                    return;
                foreach (var session in directory.BoundSessions())
                    if (directory.TryGetTarget(session, out var remote, out _) && remote is not null)
                    {
                        try
                        {
                            await endpoint.SendAsync(ProtocolDatagramCodec.Write(tables, build, opcode, []), remote, lifetime).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning(ex, "UDP relay liveness probe failed for session {Session}.", session.Id); }
                    }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            timer?.Dispose();
        }
    }

    private void Register(IPluginContext context,
        Func<ISession, ushort, ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive)
    {
        var udp = relay.ForConnection(OpcodeConnection.Udp);
        var probe = udp.C2S("rcpUdpBypassAck");
        var full = udp.C2S("rcpUdpBypassWanderLean");
        var optimized = udp.C2S("rcpUdpBypassWanderLeanOptimize");
        var verify = udp.C2S("rcpUdpBypassVerifyAck");
        var challenge = udp.S2C("rspUdpBypassVerify_");
        var ready = relay.S2C("rspUdpBypassReady_");
        PacketLayoutContract.RequireEmpty(tables, build, "UdpRelayDisable", "Native UDP disable");
        context.Effect(context.Services.Get<IPacketRouter>().Register(directory.Protocol, relay.C2S("rcpReqUdpBypassDisable"),
            (session, packet, cancellationToken) =>
            {
                _ = PacketCodec.Read(tables, build, "UdpRelayDisable", packet.Payload.Span);
                var before = directory.StateOf(session);
                directory.Disable(session);
                // The client's own TCP fallback; the state it left says whether its UDP ever reached the server.
                logger.LogInformation("Session {Session} asked for TCP fallback while its UDP path was {State}.", session.Id, before);
                return ValueTask.CompletedTask;
            }));
        var nativeFull = relay.C2S("gppWanderLean");
        var nativeOptimized = relay.C2S("gppWanderLeanOptimize");
        var identityBytes = PacketCodec.Write(tables, build, "UdpRelayProbeReply", new Dictionary<string, object?>()).Length;
        if (identityBytes is not (4 or 8))
            throw new ConfigException("Native UDP relay identities must contain four or eight bytes.");
        context.AcquireEffectAsync(acquisitionToken => ValueTask.FromResult<IAsyncDisposable>(endpoint.Register(async (remote, payload, ct) =>
        {
            try
            {
                var frame = ProtocolDatagramCodec.Read(tables, build, payload);
                if (frame.Opcode == probe)
                {
                    if (frame.Body.Length != identityBytes || !directory.TryBind(frame.Body.Span, remote, out var bound)
                        || bound is null || bound.Profile.Id != build)
                    {
                        logger.LogDebug("Rejected unbound UDP probe from {Remote}.", remote);
                        return;
                    }
                    // kr-812 sub_80FA20 and tw-454 sub_A77C50 answer UDP 19 with 20002.
                    await endpoint.SendAsync(ProtocolDatagramCodec.Write(tables, build, challenge, []), remote, ct).ConfigureAwait(false);
                    logger.LogDebug("UDP relay probe bound for session {Session}; awaiting challenge reply.", bound.Id);
                    return;
                }
                if (frame.Opcode != full && frame.Opcode != optimized && frame.Opcode != verify)
                {
                    logger.LogWarning("Unknown UDP relay opcode {Opcode} from {Remote}.", frame.Opcode, remote);
                    return;
                }
                if (frame.Body.Length < identityBytes || !directory.TryResolve(frame.Body.Span[..identityBytes], remote, out var session)
                    || session is null || session.Profile.Id != build)
                    return;
                if (frame.Opcode == verify)
                {
                    if (frame.Body.Length != identityBytes)
                        throw new PacketFormatException("UDP verification contains trailing data.", identityBytes);
                    if (directory.Verify(session))
                    {
                        await session.SendAsync(ready, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                        logger.LogInformation("UDP relay verified for session {Session}.", session.Id);
                    }
                    return;
                }
                if (directory.StateOf(session) != MatchUdpRelayState.Verified) return;
                var body = frame.Body[identityBytes..];
                _ = PacketCodec.Read(tables, build, frame.Opcode == full ? "MovementReport" : "MovementSample", body.Span);
                await receive(session, frame.Opcode == full ? nativeFull : nativeOptimized, body, ct).ConfigureAwait(false);
            }
            catch (PacketFormatException ex)
            {
                logger.LogWarning("Rejected malformed UDP relay datagram from {Remote}: {Error}.", remote, ex.Message);
            }
        }))).AsTask().GetAwaiter().GetResult();
    }
}
