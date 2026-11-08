using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;

namespace FantasyTale.Gateway;

/// <summary>Checks native auxiliary readiness before exposing a real endpoint to the client's persistent directory.</summary>
internal static class NativeCenterProbe
{
    internal static async Task<CenterEntry[]> ResolveAsync(CenterSelectorOptions options, BubbleFighterBuildProfile profile,
        IGatewayLog log, CancellationToken cancellationToken)
    {
        if (options.NativeProbeTimeoutMs == 0) return options.Centers;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.NativeProbeTimeoutMs);
        var results = options.Centers.ToArray();
        var ready = new bool[results.Length];
        var reasons = Enumerable.Repeat("deadline", results.Length).ToArray();
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, results.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = options.NativeProbeParallelism, CancellationToken = deadline.Token,
            }, async (index, token) =>
            {
                if (results[index].UnavailableCapacity is not null)
                {
                    reasons[index] = "parent-unavailable";
                    return;
                }
                try
                {
                    await ProbeAsync(results[index], options, profile, token).ConfigureAwait(false);
                    ready[index] = true;
                    reasons[index] = "ready";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or SocketException or PacketFormatException)
                {
                    reasons[index] = error is PacketFormatException ? "invalid-status" : "connection-failed";
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < results.Length; index++)
        {
            var entry = results[index];
            if (options.ConnectionTrace)
                log.Write($"bf-center-selector: native probe center={entry.NativeCenterId} result={reasons[index]}.");
            if (ready[index] || entry.UnavailableCapacity is not null) continue;
            // tw-454 sub_A7FAD0 removes auxiliaries only on a uniqueness reply; sub_B53670 repeatedly reports leftovers.
            // A TCP accept without native status must use the existing local status/retirement path instead.
            results[index] = new CenterEntry
            {
                Id = entry.Id, Name = entry.Name, ShortName = entry.ShortName, Host = entry.Host, Port = entry.Port,
                BackendId = entry.BackendId, NativeCenterId = entry.NativeCenterId, StatusCapacity = entry.StatusCapacity,
                UnavailableCapacity = entry.StatusCapacity!.Value,
            };
        }
        return results;
    }

    private static async Task ProbeAsync(CenterEntry entry, CenterSelectorOptions options, BubbleFighterBuildProfile profile,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Parse(entry.Host), entry.Port, cancellationToken).ConfigureAwait(false);
        var protocol = profile.Automatic!;
        var stream = client.GetStream();
        var encryptor = profile.Dialect.CreateEncryptor(PacketDirection.ClientToServer);
        using var decoder = new WireStreamDecoder(profile.Dialect, PacketDirection.ServerToClient, options.MaxPayloadBytes);
        // tw-454 sub_B56250 sends the empty native status request; cn-324 sub_7001F0 precedes it with its marker.
        if (profile.Marker is { } marker) await SendAsync(marker).ConfigureAwait(false);
        await SendAsync(protocol.StatusRequest).ConfigureAwait(false);
        var receive = new byte[options.ReceiveBufferBytes];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(receive, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Native Center closed before its status reply.");
                var items = decoder.Feed(receive.AsSpan(0, read));
                var statusSeen = false;
                try
                {
                    foreach (var item in items)
                    {
                        if (item is WireStreamItem.Preamble preamble)
                        {
                            if (preamble.Bytes.Length < sizeof(ushort)
                                || BinaryPrimitives.ReadUInt16LittleEndian(preamble.Bytes) != profile.GreetingStatus)
                                throw new PacketFormatException("Native Center greeting does not match its build.", 0);
                            continue;
                        }
                        if (item is not WireStreamItem.Frame frame || statusSeen || frame.Value.Opcode != protocol.StatusReply)
                            throw new PacketFormatException("Unexpected native Center status reply.", 0);
                        protocol.Status.ValidatePayload(frame.Value.Payload, options.MaxStringCharacters, options.MaxPayloadBytes / sizeof(uint));
                        statusSeen = true;
                    }
                    if (statusSeen)
                    {
                        if (decoder.PendingBytes != 0) throw new PacketFormatException("Incomplete data follows native Center status.", 0);
                        return;
                    }
                }
                finally
                {
                    foreach (var item in items)
                    {
                        if (item is WireStreamItem.Frame frame) CryptographicOperations.ZeroMemory(frame.Value.Payload);
                        else if (item is WireStreamItem.Preamble preamble) CryptographicOperations.ZeroMemory(preamble.Bytes);
                    }
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(receive); }

        async ValueTask SendAsync(ushort opcode)
        {
            var bytes = profile.Dialect.EncodeFrame(opcode, []);
            encryptor.Apply(bytes);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
