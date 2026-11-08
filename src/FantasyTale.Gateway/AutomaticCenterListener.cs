using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace FantasyTale.Gateway;

/// <summary>Serves one native auxiliary connection and redirects only its selected-login request.</summary>
internal static class AutomaticCenterListener
{
    internal static Task<AutomaticCenterOutcome> RunAsync(TcpListener listener, BubbleFighterBuildProfile profile,
        CenterSelectorOptions selector, AutomaticCenterState state, IGatewayLog log, CancellationToken lifetime)
        => RunCoreAsync(listener, profile, selector, state, null, log, lifetime);

    internal static Task<AutomaticCenterOutcome> RunUnavailableAsync(TcpListener listener, BubbleFighterBuildProfile profile,
        CenterSelectorOptions selector, CenterEntry entry, IGatewayLog log, CancellationToken lifetime,
        CancellationToken stopAccepting = default)
        => RunCoreAsync(listener, profile, selector, null, entry, log, lifetime, stopAccepting);

    private static async Task<AutomaticCenterOutcome> RunCoreAsync(TcpListener listener, BubbleFighterBuildProfile profile,
        CenterSelectorOptions selector, AutomaticCenterState? state, CenterEntry? unavailableEntry, IGatewayLog log, CancellationToken lifetime,
        CancellationToken stopAccepting = default)
    {
        var options = selector.AutomaticEntry;
        var label = unavailableEntry is null ? "automatic" : "unavailable";
        var trace = new ConnectionTrace(log, selector.ConnectionTrace, label,
            selector.ConnectionTrace ? ((IPEndPoint)listener.LocalEndpoint).Port : 0);
        trace.Write("listening");
        var protocol = profile.Automatic!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var phase = "connect";
        var receive = new byte[selector.ReceiveBufferBytes];
        TcpClient? client = null;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime, stopAccepting))
            {
                if (selector.ConnectTimeoutMs > 0) connect.CancelAfter(selector.ConnectTimeoutMs);
                client = await listener.AcceptTcpClientAsync(connect.Token).ConfigureAwait(false);
            }
            listener.Stop();
            trace.Write("accepted");
            using var accepted = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (selector.SelectionTimeoutMs > 0) accepted.CancelAfter(selector.SelectionTimeoutMs);
            using var acceptedRegistration = accepted.Token.Register(() => deadline.Cancel());
            client.NoDelay = true;
            if (unavailableEntry is null) log.Write("bf-center-selector: automatic auxiliary accepted.");
            phase = "status";
            deadline.CancelAfter(selector.HandshakeTimeoutMs);
            var stream = client.GetStream();
            await stream.WriteAsync(profile.Dialect.BuildServerPreamble(profile.GreetingStatus, profile.BootstrapCommandLine), deadline.Token).ConfigureAwait(false);
            using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ClientToServer, selector.MaxPayloadBytes);
            var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ServerToClient);
            var statusSeen = false;
            var unavailableStatus = unavailableEntry is not null;
            var markerSeen = false;
            var retired = false;
            var redirectSent = false;
            var releaseAcknowledged = false;
            while (true)
            {
                var read = await stream.ReadAsync(receive, deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    trace.Write("eof");
                    if (!statusSeen || decoder.PendingBytes != 0 || redirectSent && protocol.Release is not null && !releaseAcknowledged)
                        throw new EndOfStreamException("Automatic Center closed an incomplete native exchange.");
                    log.Write($"bf-center-selector: {label} source released.");
                    return redirectSent ? AutomaticCenterOutcome.RedirectReleased : AutomaticCenterOutcome.UnselectedReleased;
                }
                if (retired && (!redirectSent || protocol.Release is null || releaseAcknowledged))
                    throw new PacketFormatException("Unexpected data after automatic Center retirement.", 0);
                var items = decoder.Feed(receive.AsSpan(0, read));
                try
                {
                    foreach (var item in items)
                    {
                        if (item is not WireStreamItem.Frame frame)
                            throw new PacketFormatException("Unexpected automatic Center state.", 0);
                        if (retired)
                        {
                            var release = protocol.Release;
                            if (!redirectSent || release is null || releaseAcknowledged || frame.Value.Opcode != release.Request)
                                throw new PacketFormatException("Unexpected automatic Center release frame.", 0);
                            release.Acknowledgement.ValidateLogin(frame.Value.Payload, selector.MaxStringCharacters, options?.MaxLoginAdapters ?? selector.MaxPayloadBytes / sizeof(uint));
                            // kr-1256 sub_9A6BA0 acknowledges promotion on the old source with C2S 55;
                            // kr-1256 sub_9D2FF0 case 10 then closes that source without replacing current.
                            await SendAsync(release.Reply, release.Completion.Write(new Dictionary<string, object?>())).ConfigureAwait(false);
                            releaseAcknowledged = true;
                            trace.Write("promotion-acknowledged");
                            log.Write($"bf-center-selector: {label} source promotion acknowledged.");
                        }
                        else if (!statusSeen && !markerSeen && profile.Marker == frame.Value.Opcode && frame.Value.Payload.Length == 0)
                        {
                            // cn-324 sub_7001F0 sends empty gcpStartGPK through cn-324 sub_718480 before its status request.
                            markerSeen = true;
                        }
                        else if (frame.Value.Opcode == protocol.StatusRequest && frame.Value.Payload.Length == 0)
                        {
                            AutomaticCenterCandidate? candidate = null;
                            if (unavailableEntry is null && options!.UnavailableCapacity is null)
                                candidate = await SelectAsync(commit: false).ConfigureAwait(false);
                            else if (!unavailableStatus)
                            {
                                try { candidate = state!.Select(DateTimeOffset.UtcNow, commit: false); }
                                catch (AutomaticCenterUnavailableException) { unavailableStatus = true; }
                            }
                            // cn-324 sub_408AE0/sub_408800 reject clicks only above a load ratio of one.
                            // Doubling the configured capacity avoids rounding back to one in native float arithmetic.
                            var capacity = candidate?.Capacity ?? unavailableEntry?.UnavailableCapacity ?? options!.UnavailableCapacity!.Value;
                            await SendAsync(protocol.StatusReply, protocol.Status.Write(new Dictionary<string, object?>
                            {
                                ["center_id"] = unavailableEntry?.NativeCenterId ?? options!.NativeCenterId,
                                ["name"] = unavailableEntry?.Name ?? selector.ResolveAutomaticName(options!),
                                ["users"] = candidate?.Users ?? checked(capacity * 2), ["capacity"] = capacity,
                            })).ConfigureAwait(false);
                            if (!statusSeen)
                            {
                                trace.Write("status-sent");
                                if (unavailableEntry is null)
                                    log.Write($"bf-center-selector: automatic native status sent; available={!unavailableStatus}.");
                                statusSeen = true;
                                phase = "selection";
                                deadline.CancelAfter(Timeout.Infinite);
                            }
                        }
                        else if (statusSeen && frame.Value.Opcode == protocol.UniquenessRequest && frame.Value.Payload.Length == sizeof(uint))
                        {
                            trace.Write("uniqueness-received");
                            // kr-812 sub_8184B0 / cn-324 sub_703B50: true retires an unselected auxiliary; false fails login.
                            await SendAsync(protocol.UniquenessReply, protocol.Availability.Write(new Dictionary<string, object?> { ["unavailable"] = true })).ConfigureAwait(false);
                            trace.Write("uniqueness-replied");
                            retired = true;
                            state?.Finish();
                            phase = "release";
                            deadline.CancelAfter(options?.RedirectTimeoutMs ?? selector.HandshakeTimeoutMs);
                        }
                        else if (statusSeen && frame.Value.Opcode == protocol.LoginRequest)
                        {
                            protocol.Login.ValidateLogin(frame.Value.Payload, selector.MaxStringCharacters, options?.MaxLoginAdapters ?? selector.MaxPayloadBytes / sizeof(uint));
                            CryptographicOperations.ZeroMemory(frame.Value.Payload);
                            trace.Write("login-received");
                            if (unavailableEntry is null) log.Write("bf-center-selector: automatic native login received.");
                            if (unavailableStatus) return AutomaticCenterOutcome.Unavailable;
                            var selected = await SelectAsync(commit: true).ConfigureAwait(false);
                            var worker = selector.Centers.Single(entry => entry.Id == selected.CenterId);
                            var move = profile.Dialect.EncodeFrame(protocol.MoveReply, protocol.Move.Write(new Dictionary<string, object?>
                            {
                                ["result"] = 0, ["ipv4_word"] = BinaryPrimitives.ReadUInt32LittleEndian(IPAddress.Parse(worker.Host).GetAddressBytes()),
                                ["port"] = worker.Port,
                            }));
                            var promotion = profile.Dialect.EncodeFrame(protocol.PromotePending, protocol.Promotion.Write(new Dictionary<string, object?>()));
                            var burst = new byte[move.Length + promotion.Length];
                            move.CopyTo(burst, 0);
                            promotion.CopyTo(burst, move.Length);
                            encryptor.Apply(burst);
                            // kr-812 sub_81B680 / cn-324 sub_705710 create pending;
                            // kr-812 sub_81C550 / cn-324 sub_705980 promote it before target patch/login.
                            await stream.WriteAsync(burst, deadline.Token).ConfigureAwait(false);
                            redirectSent = true;
                            retired = true;
                            phase = "release";
                            deadline.CancelAfter(options?.RedirectTimeoutMs ?? selector.HandshakeTimeoutMs);
                            log.Write($"bf-center-selector: {label} native redirect sent.");
                            trace.Write("redirect-sent");
                        }
                        else
                        {
                            log.Write($"bf-center-selector: rejected {label} frame; opcode={frame.Value.Opcode}, payload_bytes={frame.Value.Payload.Length}.");
                            throw new PacketFormatException("Unexpected automatic Center opcode or payload.", 0);
                        }
                    }
                }
                finally
                {
                    foreach (var item in items.OfType<WireStreamItem.Frame>())
                        CryptographicOperations.ZeroMemory(item.Value.Payload);
                    CryptographicOperations.ZeroMemory(receive);
                }
                if (retired && (!redirectSent || protocol.Release is null || releaseAcknowledged))
                {
                    var incomplete = decoder.PendingBytes != 0;
                    decoder.Dispose();
                    if (incomplete)
                        throw new PacketFormatException("Incomplete data after automatic Center retirement.", 0);
                }
            }

            async ValueTask SendAsync(ushort opcode, byte[] payload)
            {
                var bytes = profile.Dialect.EncodeFrame(opcode, payload);
                encryptor.Apply(bytes);
                await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            }

            async Task<AutomaticCenterCandidate> SelectAsync(bool commit)
            {
                try { return state!.Select(DateTimeOffset.UtcNow, commit); }
                catch (AutomaticCenterUnavailableException)
                {
                    phase = "freshness";
                    log.Write($"bf-center-selector: {label} awaiting fresh snapshot.");
                    var remaining = options!.GrantExpiresAtUtc - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) throw;
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(selector.HandshakeTimeoutMs, remaining.TotalMilliseconds)));
                    var candidate = await state!.WaitSelectAsync(commit, deadline.Token).ConfigureAwait(false);
                    phase = statusSeen ? "selection" : "status";
                    deadline.CancelAfter(statusSeen ? Timeout.Infinite : selector.HandshakeTimeoutMs);
                    return candidate;
                }
            }
        }
        catch (OperationCanceledException) when (stopAccepting.IsCancellationRequested && !lifetime.IsCancellationRequested && phase == "connect")
        {
            listener.Stop();
            trace.Write("accept-stopped");
            return AutomaticCenterOutcome.UnselectedReleased;
        }
        catch (Exception error) when (error is AutomaticCenterUnavailableException
            || error is OperationCanceledException && !lifetime.IsCancellationRequested && phase == "freshness")
        {
            trace.Write("unavailable");
            log.Write($"bf-center-selector: {label} unavailable; manual bootstrap retained.");
            return AutomaticCenterOutcome.Unavailable;
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            trace.Write("deadline");
            throw new TimeoutException($"Automatic Center {phase} deadline expired.");
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
            log.Write($"bf-center-selector: {label} failure; phase={phase}, kind={error.GetType().Name}.");
            throw;
        }
        finally
        {
            // EOF must not precede retirement: the parent can refresh immediately after socket closure.
            state?.Finish();
            client?.Dispose();
            trace.Write("closed");
            CryptographicOperations.ZeroMemory(receive);
        }
    }

    internal static async Task RunTogetherAsync(Func<CancellationToken, Task> bootstrap,
        Func<CancellationToken, Task<AutomaticCenterOutcome>> automatic, CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var handoff = new CancellationTokenSource();
        using var bootstrapLifetime = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, handoff.Token);
        var tasks = new[] { CompleteBootstrapAsync(), CompleteAutomaticAsync() };
        var first = await Task.WhenAny(tasks).ConfigureAwait(false);
        if (!first.IsCompletedSuccessfully) linked.Cancel();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally { linked.Cancel(); }

        async Task CompleteBootstrapAsync()
        {
            try { await bootstrap(bootstrapLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (handoff.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                // kr-812 sub_81C550 / cn-324 sub_705980 already promoted pending; retire only the obsolete directory.
            }
        }

        async Task CompleteAutomaticAsync()
        {
            var outcome = await automatic(linked.Token).ConfigureAwait(false);
            if (outcome == AutomaticCenterOutcome.RedirectReleased) handoff.Cancel();
        }
    }
}

/// <summary>Separates a completed selected redirect from auxiliary closure that must preserve manual selection.</summary>
internal enum AutomaticCenterOutcome
{
    RedirectReleased,
    UnselectedReleased,
    Unavailable,
}
