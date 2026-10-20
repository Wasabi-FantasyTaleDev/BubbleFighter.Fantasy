using System.Runtime.CompilerServices;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>One line read from a child stream.</summary>
/// <param name="Bytes">The line without its terminator; valid only until the next line is requested.</param>
/// <param name="Truncated">Whether the line exceeded the bound and was cut (only when truncation was requested).</param>
internal readonly record struct BoundedLine(ReadOnlyMemory<byte> Bytes, bool Truncated);

/// <summary>Splits a byte stream into newline-terminated lines without ever holding more than a fixed bound.</summary>
internal static class BoundedLineReader
{
    /// <summary>
    /// Reads lines until end of stream. A line longer than <paramref name="maxBytes"/> throws
    /// <see cref="InvalidDataException"/> unless <paramref name="truncate"/> is set, in which case the excess is dropped.
    /// A final line without a terminator is a truncated frame and also throws, so a dying process is never read as a message.
    /// </summary>
    /// <param name="stream">The child's output stream.</param>
    /// <param name="maxBytes">The maximum line length in bytes, terminator excluded.</param>
    /// <param name="truncate">Whether to cut an oversized line instead of failing.</param>
    /// <param name="ct">Cancellation.</param>
    public static async IAsyncEnumerable<BoundedLine> ReadAsync(Stream stream, int maxBytes, bool truncate,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        var chunk = new byte[Math.Min(65536, maxBytes + 1)];
        var line = new byte[maxBytes];
        var length = 0;
        var overflow = false;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (length > 0 || overflow)
                    throw new InvalidDataException("The stream ended in the middle of a line.");
                yield break;
            }
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(chunk, (byte)'\n', start, read - start);
                var end = newline < 0 ? read : newline;
                var take = end - start;
                if (!overflow)
                {
                    if (length + take > maxBytes)
                    {
                        if (!truncate)
                            throw new InvalidDataException($"A line exceeds the {maxBytes}-byte bound.");
                        take = maxBytes - length;
                        overflow = true;
                    }
                    Buffer.BlockCopy(chunk, start, line, length, take);
                    length += take;
                }
                if (newline < 0)
                    break;
                var finished = length;
                if (finished > 0 && line[finished - 1] == (byte)'\r')
                    finished--;
                yield return new BoundedLine(line.AsMemory(0, finished), overflow);
                length = 0;
                overflow = false;
                start = newline + 1;
            }
        }
    }
}
