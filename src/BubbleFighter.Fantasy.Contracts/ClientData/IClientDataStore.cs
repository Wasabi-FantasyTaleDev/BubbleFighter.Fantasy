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
}
