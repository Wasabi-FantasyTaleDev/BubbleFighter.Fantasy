using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace FantasyTale.Gateway;

/// <summary>Supplies a directory for fresh native clients; target connections carry all subsequent game traffic.</summary>
internal sealed class BfCenterSelectorPlugin : IGatewayPlugin
{
    public async ValueTask<GatewayActivation> ActivateAsync(GatewayRow row, GatewayContext context)
    {
        var options = row.Config.Deserialize(GatewayJsonContext.Strict.CenterSelectorOptions)
            ?? throw new GatewayConfigurationException("Missing center-selector configuration.");
        var profile = options.Validate();
        var log = context.Get<IGatewayLog>();
        if (options.ConnectionTrace) log = log.WithBudget(options.ConnectionTraceMaxBytes);
        var centers = await NativeCenterProbe.ResolveAsync(options, profile, log, context.Lifetime).ConfigureAwait(false);
        if (centers.Length == 1 && options.SelectorIdentity is null && options.AutomaticEntry is null
            && centers[0].UnavailableCapacity is null)
        {
            var entry = centers[0];
            return new GatewayActivation(
                new GatewayEndpoint(row.Id, "direct", entry.Host, entry.Port, profile.Id), Task.CompletedTask);
        }

        TcpListener? automatic = null;
        AutomaticCenterState? automaticState = null;
        var directoryEntries = centers;
        var unavailable = new List<(TcpListener Listener, CenterEntry Entry)>();
        if (centers.Any(entry => entry.UnavailableCapacity is not null))
        {
            context.Effect(new UnavailableListeners(unavailable));
            directoryEntries = centers.Select(entry =>
            {
                if (entry.UnavailableCapacity is null) return entry;
                var status = new TcpListener(IPAddress.Loopback, 0);
                unavailable.Add((status, entry));
                status.Start(options.ListenBacklog);
                var statusPort = ((IPEndPoint)status.LocalEndpoint).Port;
                ValidateRedirectTargets(options, statusPort);
                return new CenterEntry
                {
                    Id = entry.Id, Name = entry.Name, ShortName = entry.ShortName,
                    BackendId = entry.BackendId, NativeCenterId = entry.NativeCenterId,
                    Host = "127.0.0.1", Port = statusPort,
                };
            }).ToArray();
        }
        if (options.AutomaticEntry is { } auto)
        {
            automaticState = new AutomaticCenterState(auto,
                options.Centers.Where(entry => entry.UnavailableCapacity is not null).Select(entry => entry.Id),
                centers.Where(entry => entry.UnavailableCapacity is not null).Select(entry => entry.Id));
            context.Effect(context.Register<IGatewayControlHandler>(automaticState));
            automatic = new TcpListener(IPAddress.Loopback, 0);
            context.Effect(automatic);
            automatic.Start(options.ListenBacklog);
            var automaticPort = ((IPEndPoint)automatic.LocalEndpoint).Port;
            ValidateRedirectTargets(options, automaticPort);
            directoryEntries = [new CenterEntry
            {
                Id = auto.Id, Name = options.ResolveAutomaticName(auto), Host = "127.0.0.1", Port = automaticPort,
            }, .. directoryEntries];
        }
        var bindings = CreateBindings(profile, directoryEntries);
        if (options.ConnectionTrace)
        {
            var unavailableIds = unavailable.Select(item => item.Entry.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var role = automatic is not null && binding.Id == options.AutomaticEntry!.Id ? "automatic"
                    : unavailableIds.Contains(binding.Id) ? "unavailable" : null;
                if (role is not null)
                    log.Write($"bf-center-selector: binding role={role} port={binding.Port} group={binding.Group} sub={binding.SubKey}.");
            }
        }
        var directory = EncodeDirectory(profile, bindings);
        var identity = options.SelectorIdentity is { } requested ? new SelectorIdentityMapping
        {
            Capability = requested.Capability,
            LaunchId = requested.LaunchId,
            Build = profile.Id,
            Entries = bindings,
            Presentation = requested.Presentation,
        } : null;
        var patch = profile.Dialect.EncodeFrame(profile.PatchReply,
            profile.PatchResponse.Write(new Dictionary<string, object?>()));
        var list = profile.Dialect.EncodeFrame(profile.CenterList, directory);
        var reply = new byte[patch.Length + list.Length];
        patch.CopyTo(reply, 0);
        list.CopyTo(reply, patch.Length);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        context.Effect(listener);
        listener.Start(options.ListenBacklog);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (automatic is not null) ValidateRedirectTargets(options, port);
        Task MainAsync(CancellationToken ct) => automatic is null
            ? ServeAsync(listener, profile, options, reply, log, ct)
            : AutomaticCenterListener.RunTogetherAsync(
                token => ServeAsync(listener, profile, options, reply, log, token),
                token => AutomaticCenterListener.RunAsync(automatic, profile, options, automaticState!, log, token), ct);
        var completion = unavailable.Count == 0 ? MainAsync(context.Lifetime)
            : RunWithUnavailableAsync(MainAsync, unavailable.Select(item =>
                (Func<CancellationToken, CancellationToken, Task>)((ct, stopAccepting) => AutomaticCenterListener.RunUnavailableAsync(
                    item.Listener, profile, options, item.Entry, log, ct, stopAccepting))).ToArray(), options.HandshakeTimeoutMs, log, context.Lifetime);
        return new GatewayActivation(
            new GatewayEndpoint(row.Id, "bootstrap", "127.0.0.1", port, profile.Id) { SelectorIdentity = identity },
            completion);
    }

    private sealed class UnavailableListeners(List<(TcpListener Listener, CenterEntry Entry)> listeners) : IDisposable
    {
        public void Dispose()
        {
            for (var i = listeners.Count - 1; i >= 0; i--) listeners[i].Listener.Stop();
        }
    }

    internal static void ValidateRedirectTargets(CenterSelectorOptions options, int localPort)
    {
        if (options.Centers.Any(entry => IPAddress.TryParse(entry.Host, out var ip) && IPAddress.IsLoopback(ip) && entry.Port == localPort))
            throw new GatewayConfigurationException("Automatic Center cannot redirect to a Gateway listener.");
    }

    private static async Task RunWithUnavailableAsync(Func<CancellationToken, Task> main,
        Func<CancellationToken, CancellationToken, Task>[] auxiliaries, int retirementTimeoutMs,
        IGatewayLog log, CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var accepting = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var tasks = auxiliaries.Select(auxiliary => ObserveAsync(ct => auxiliary(ct, accepting.Token))).ToArray();
        var completion = ObserveAsync(async ct =>
        {
            await main(ct).ConfigureAwait(false);
            accepting.Cancel();
            // kr-812 sub_8184B0 / cn-324 sub_703B50 remove auxiliaries only after their uniqueness reply.
            // Directory EOF does not establish that every accepted auxiliary has completed that exchange.
            log.Write("bf-center-selector: unavailable auxiliary retirement started.");
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(retirementTimeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Unavailable Center retirement deadline expired.");
            }
        });
        try { await Task.WhenAll(tasks.Append(completion)).ConfigureAwait(false); }
        finally { linked.Cancel(); }

        async Task ObserveAsync(Func<CancellationToken, Task> run)
        {
            try { await run(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
            catch { linked.Cancel(); throw; }
        }
    }

    internal static byte[] EncodeDirectory(BubbleFighterBuildProfile profile, CenterEntry[] entries)
        => EncodeDirectory(profile, CreateBindings(profile, entries));

    internal static SelectorIdentityEntry[] CreateBindings(BubbleFighterBuildProfile profile, CenterEntry[] entries)
    {
        // kr-812 sub_8E7110 / cn-324 sub_78D580 connect the initial group and skip the reserved sub-key.
        // Omitting the bootstrap endpoint prevents it from becoming a selectable game connection.
        var next = profile.ReservedCenter;
        var bindings = new SelectorIdentityEntry[entries.Length];
        var index = 0;
        foreach (var entry in entries)
        {
            entry.Validate();
            bindings[index++] = new SelectorIdentityEntry(entry.Id, entry.Name, entry.BackendId ?? "",
                entry.NativeCenterId ?? 0, profile.Group, checked(++next), entry.Host, entry.Port);
        }
        return bindings;
    }

    private static byte[] EncodeDirectory(BubbleFighterBuildProfile profile, SelectorIdentityEntry[] bindings)
    {
        var gates = new List<IReadOnlyDictionary<string, object?>>(bindings.Length);
        foreach (var binding in bindings)
        {
            var address = BinaryPrimitives.ReadUInt32LittleEndian(IPAddress.Parse(binding.Host).GetAddressBytes());
            gates.Add(profile.ExperimentalDirectory
                ? new Dictionary<string, object?>
                {
                    ["unknown_4"] = binding.Group,
                    ["unknown_8"] = binding.SubKey,
                    ["unknown_12"] = 0,
                    ["unknown_16"] = address,
                    ["unknown_20"] = binding.Port,
                }
                : new Dictionary<string, object?>
                {
                    ["key"] = binding.Group,
                    ["sub_key"] = binding.SubKey,
                    ["unknown_12"] = 0,
                    ["address"] = address,
                    ["port"] = binding.Port,
                });
        }
        return profile.Directory.Write(new Dictionary<string, object?> { ["gates"] = gates });
    }

    private static async Task ServeAsync(TcpListener listener, BubbleFighterBuildProfile profile,
        CenterSelectorOptions options, byte[] reply, IGatewayLog log, CancellationToken lifetime)
    {
        var trace = new ConnectionTrace(log, options.ConnectionTrace, "bootstrap",
            options.ConnectionTrace ? ((IPEndPoint)listener.LocalEndpoint).Port : 0);
        trace.Write("listening");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        if (options.ConnectTimeoutMs > 0) connect.CancelAfter(options.ConnectTimeoutMs);
        var phase = "connect";
        try
        {
            using var client = await listener.AcceptTcpClientAsync(connect.Token).ConfigureAwait(false);
            listener.Stop();
            trace.Write("accepted");
            client.NoDelay = true;
            phase = "handshake";
            log.Write("bf-center-selector: bootstrap accepted.");
            using var selection = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (options.SelectionTimeoutMs > 0)
                selection.CancelAfter(options.SelectionTimeoutMs);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(selection.Token);
            handshake.CancelAfter(options.HandshakeTimeoutMs);
            var stream = client.GetStream();
            await stream.WriteAsync(profile.Dialect.BuildServerPreamble(profile.GreetingStatus, profile.BootstrapCommandLine), handshake.Token).ConfigureAwait(false);
            log.Write("bf-center-selector: native greeting sent.");
            using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ClientToServer, options.MaxPayloadBytes);
            var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ServerToClient);
            var receive = new byte[options.ReceiveBufferBytes];
            var patchSeen = false;
            var markerSeen = false;
            var keepAliveSeen = false;
            var observationsSeen = new bool[profile.BootstrapObservations.Length];
            while (true)
            {
                var read = await stream.ReadAsync(receive, patchSeen ? selection.Token : handshake.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    trace.Write("eof");
                    if (!patchSeen || decoder.PendingBytes != 0)
                    {
                        log.Write($"bf-center-selector: incomplete bootstrap EOF; phase={phase}, pending_bytes={decoder.PendingBytes}.");
                        throw new EndOfStreamException("Game closed an incomplete bootstrap exchange.");
                    }
                    // This is a release signal, not proof of a successful login to a target server.
                    log.Write("bf-center-selector: bootstrap connection released.");
                    return;
                }
                foreach (var item in decoder.Feed(receive.AsSpan(0, read)))
                {
                    if (item is not WireStreamItem.Frame frame)
                        throw new PacketFormatException("Unexpected client preamble.", 0);
                    if (Array.Find(profile.BootstrapExchanges, exchange => exchange.Request == frame.Value.Opcode) is { } exchange)
                    {
                        exchange.RequestLayout.ValidatePayload(frame.Value.Payload, options.MaxStringCharacters, options.MaxPayloadBytes / sizeof(uint));
                        var response = profile.Dialect.EncodeFrame(exchange.Reply, exchange.ReplyPayload);
                        encryptor.Apply(response);
                        await stream.WriteAsync(response, patchSeen ? selection.Token : handshake.Token).ConfigureAwait(false);
                        log.Write($"bf-center-selector: native startup exchange answered; opcode={frame.Value.Opcode}.");
                    }
                    else if (patchSeen && Array.FindIndex(profile.BootstrapObservations,
                        observation => observation.Opcode == frame.Value.Opcode) is var observationIndex && observationIndex >= 0)
                    {
                        try
                        {
                            profile.BootstrapObservations[observationIndex].Layout.ValidatePayload(
                                frame.Value.Payload, options.MaxStringCharacters, options.MaxPayloadBytes / sizeof(uint));
                        }
                        finally { CryptographicOperations.ZeroMemory(frame.Value.Payload); }
                        if (!observationsSeen[observationIndex])
                        {
                            observationsSeen[observationIndex] = true;
                            log.Write($"bf-center-selector: native auxiliary request observed; opcode={frame.Value.Opcode}.");
                        }
                    }
                    else if (!patchSeen && profile.PreHandshakeOpcodes.Contains(frame.Value.Opcode))
                    {
                        log.Write($"bf-center-selector: pre-handshake opcode accepted; opcode={frame.Value.Opcode}.");
                    }
                    else if (frame.Value.Opcode == profile.PatchRequest)
                    {
                        if (patchSeen)
                        {
                            log.Write($"bf-center-selector: rejected bootstrap frame; phase={phase}, opcode={frame.Value.Opcode}, payload_bytes={frame.Value.Payload.Length}.");
                            throw new PacketFormatException("Duplicate first-connect packet.", 0);
                        }
                        profile.FirstConnect.ValidateFirstConnect(frame.Value.Payload, profile.Major, options.MaxStringCharacters);
                        encryptor.Apply(reply);
                        // kr-812 sub_8178B0 enters the startup stage; sub_817960 installs the directory.
                        await stream.WriteAsync(reply, handshake.Token).ConfigureAwait(false);
                        patchSeen = true;
                        phase = "selection";
                        handshake.CancelAfter(Timeout.Infinite);
                        log.Write("bf-center-selector: native directory sent.");
                        trace.Write("directory-sent");
                    }
                    else if (profile.Marker == frame.Value.Opcode && !markerSeen && frame.Value.Payload.Length == 0)
                    {
                        markerSeen = true;
                        log.Write("bf-center-selector: native marker accepted.");
                    }
                    else if (patchSeen && profile.KeepAlivePing == frame.Value.Opcode && frame.Value.Payload.Length == 0)
                    {
                        // cn-324 sub_78B580 rearms its timer after the empty send without awaiting a response.
                        // Keep any configured selection deadline fixed and log only the first ping.
                        if (!keepAliveSeen)
                        {
                            keepAliveSeen = true;
                            log.Write("bf-center-selector: native keep-alive accepted.");
                        }
                    }
                    else
                    {
                        log.Write($"bf-center-selector: rejected bootstrap frame; phase={phase}, opcode={frame.Value.Opcode}, payload_bytes={frame.Value.Payload.Length}.");
                        throw new PacketFormatException($"Unexpected bootstrap opcode {frame.Value.Opcode}.", 0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            trace.Write("deadline");
            throw new TimeoutException($"The game's {phase} deadline expired.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            trace.Write("cancelled");
            throw;
        }
        catch (Exception error) when (error is PacketFormatException or EndOfStreamException or IOException
            or SocketException or InvalidOperationException)
        {
            trace.Write("failed");
            log.Write($"bf-center-selector: bootstrap failure; phase={phase}, kind={error.GetType().Name}.");
            throw;
        }
        finally { trace.Write("closed"); }
    }
}
