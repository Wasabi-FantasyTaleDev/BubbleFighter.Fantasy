namespace BubbleFighter.Fantasy.Contracts.ClientData;

/// <summary>
/// Read access to the game client's own data files (tables, map metadata, specs) as the
/// client ships them inside its packed archives. Paths are the client's virtual paths with
/// <c>/</c> separators, for example <c>ServerShared/MapMeta.xml</c>; lookups ignore case.
/// </summary>
public interface IClientDataStore
{
    /// <summary>Returns whether the client data contains a file.</summary>
    /// <param name="path">The client path, for example <c>ServerShared/MapMeta.xml</c>.</param>
    bool Exists(string path);

    /// <summary>Reads a file, verified against its archive checksum.</summary>
    /// <param name="path">The client path.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    byte[] Read(string path);

    /// <summary>
    /// Reads a file with an inclusive uncompressed byte limit. Stores that expose file or archive lengths should
    /// reject oversized resources before allocation; the default implementation also verifies the returned length.
    /// </summary>
    /// <param name="path">The client path.</param>
    /// <param name="maximumBytes">The largest accepted uncompressed file size.</param>
    /// <returns>The file's bytes, verified against its archive checksum when applicable.</returns>
    byte[] Read(string path, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        var bytes = Read(path);
        return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException($"Client file '{path}' exceeds its byte limit.");
    }

    /// <summary>
    /// Gets a file's uncompressed size. Stores that know it from a file system or an archive index answer without
    /// reading the file; the default implementation reads it.
    /// </summary>
    /// <param name="path">The client path.</param>
    /// <returns>The size in bytes.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    long Length(string path) => Read(path).LongLength;

    /// <summary>
    /// Opens a file for one read from start to end. The client's item workbooks unpack to tens of megabytes, so a
    /// parser that takes a stream should use this instead of <see cref="Read(string)"/>: an archive store unpacks
    /// as the parser reads and never holds the whole file. The read that delivers the last byte verifies the
    /// archive checksum and throws <see cref="InvalidDataException"/> when it does not match; a caller that stops
    /// early has the rest verified when it disposes the stream. The default implementation reads the file whole.
    /// </summary>
    /// <param name="path">The client path.</param>
    /// <returns>A readable stream positioned at the start of the file; the caller disposes it.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    Stream OpenRead(string path) => new MemoryStream(Read(path), writable: false);

    /// <summary>
    /// Opens a file for one read from start to end, with an inclusive uncompressed byte limit. A store that knows
    /// the size refuses an oversized file before it unpacks anything. The returned stream reports its
    /// <see cref="Stream.Length"/>, the file's uncompressed size, so a caller can bound its parser by it. The
    /// default implementation goes through <see cref="Read(string, int)"/>.
    /// </summary>
    /// <param name="path">The client path.</param>
    /// <param name="maximumBytes">The largest accepted uncompressed file size.</param>
    /// <returns>A readable stream positioned at the start of the file; the caller disposes it.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    /// <exception cref="InvalidDataException">The file is larger than <paramref name="maximumBytes"/>.</exception>
    Stream OpenRead(string path, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        return new MemoryStream(Read(path, (int)Math.Min(maximumBytes, int.MaxValue)), writable: false);
    }
}
