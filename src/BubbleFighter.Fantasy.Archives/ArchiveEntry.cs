namespace BubbleFighter.Fantasy.Archives;

/// <summary>One file inside an archive, independent of the archive format.</summary>
/// <param name="Path">The member path with <c>/</c> separators, as the archive stores it.</param>
/// <param name="Size">The unpacked size in bytes.</param>
public sealed record ArchiveEntry(string Path, long Size);

/// <summary>A read-only view of one archive's members.</summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>Gets the members in archive order.</summary>
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Returns whether a member exists; the lookup ignores case and accepts <c>\</c> separators.</summary>
    /// <param name="path">The member path.</param>
    bool Contains(string path);

    /// <summary>Reads, decrypts, unpacks and verifies one member.</summary>
    /// <param name="path">The member path; the lookup ignores case and accepts <c>\</c> separators.</param>
    /// <returns>The member's bytes.</returns>
    /// <exception cref="FileNotFoundException">No member has that path.</exception>
    /// <exception cref="InvalidDataException">The member's stored data is damaged.</exception>
    byte[] Read(string path);
}

/// <summary>
/// Opens any client archive the same way: a <c>.bfz</c> archive, or a <c>.pkn</c> pack in either of its
/// incompatible index layouts (the version-2 layout with normalized URI keys, or the early layout with
/// case-sensitive URI keys), which is told apart from the file itself.
/// </summary>
public static class Archive
{
    /// <summary>
    /// Opens a <c>.pkn</c> pack or a <c>.bfz</c> archive. The key material of every format is derived
    /// from the archive's path relative to the client's root directory, so that path is needed exactly as
    /// the client has it, case included: for example <c>Packs/Config_00000.pkn</c>,
    /// <c>Pack/Config0000.pkn</c> or <c>Config/Config.bfz</c>. When omitted it is inferred from the file
    /// name (a version-2 pack in <c>Packs/</c>, an early pack in <c>Pack/</c>, a BFZ archive in a folder of
    /// its own name directly under the root). A pack is tried as version 2 first, whose header carries a
    /// checksum over its version and entry count, and as an early pack when that header does not decode.
    /// </summary>
    /// <param name="filePath">The archive file.</param>
    /// <param name="clientRelativePath">The archive's path relative to the client root, or <see langword="null"/> to infer it.</param>
    /// <returns>A reader over the archive's members.</returns>
    /// <exception cref="InvalidDataException">The file decodes as none of the formats under that path.</exception>
    public static IArchiveReader Open(string filePath, string? clientRelativePath = null)
    {
        var extension = System.IO.Path.GetExtension(filePath);
        if (extension.Equals(".bfz", StringComparison.OrdinalIgnoreCase))
            return BfzArchive.Open(filePath, clientRelativePath);
        if (!extension.Equals(".pkn", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"'{filePath}' is neither a .pkn nor a .bfz archive.");
        InvalidDataException modern;
        try { return PknArchive.Open(filePath, clientRelativePath); }
        catch (InvalidDataException e) { modern = e; }
        try { return LegacyPknArchive.Open(filePath, clientRelativePath); }
        catch (InvalidDataException legacy)
        {
            throw new InvalidDataException(
                $"'{filePath}' decodes neither as a version-2 pack ({modern.Message}) nor as an early pack ({legacy.Message}). "
                + "Its path relative to the client root, case included, must be the one the client has.", legacy);
        }
    }

    internal static string NormalizeMemberPath(string path) => path.Replace('\\', '/');
}
