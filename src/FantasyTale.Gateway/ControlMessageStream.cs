using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FantasyTale.Gateway;

/// <summary>Reads and writes bounded length-prefixed control messages over the parent pipe.</summary>
internal static class ControlMessageStream
{
    /// <summary>Reads one message body, or returns null when the parent closes the pipe cleanly.</summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken, int frameTimeoutMs = 5000,
        int maxMessageBytes = GatewayConstants.MaxControlMessageBytes)
    {
        var header = new byte[sizeof(int)];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return null;
        using var frame = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        frame.CancelAfter(frameTimeoutMs);
        await ReadExactlyAsync(stream, header.AsMemory(first), frame.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (maxMessageBytes < 1 || maxMessageBytes > Array.MaxLength || length < 1 || length > maxMessageBytes)
            throw new GatewayConfigurationException($"Control message length {length} is outside the allowed range.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, frame.Token).ConfigureAwait(false);
        return body;
    }

    /// <summary>Writes one bounded JSON control message and flushes it before returning.</summary>
    public static async ValueTask WriteJsonAsync<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken,
        int maxMessageBytes = GatewayConstants.MaxControlMessageBytes)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (maxMessageBytes < 1 || maxMessageBytes > Array.MaxLength || body.Length < 1 || body.Length > maxMessageBytes)
            throw new GatewayConfigurationException("Control message exceeds the configured size limit.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var read = await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The parent closed a partial control message.");
            destination = destination[read..];
        }
    }
}
