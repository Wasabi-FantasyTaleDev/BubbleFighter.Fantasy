using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.Installation;

namespace BubbleFighter.Fantasy.Installation;

// Each import becomes a new immutable data root. A failed replacement cannot alter a running build's files.
internal sealed class InstallationClientDataImporter(InstallationOptions options, HashSet<string> profiles)
{
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private long _remainingStorage;
    private long _extracted;
    private long _received;
    private int _entries;

    public async ValueTask<string> ImportAsync(string profile, bool archive,
        IAsyncEnumerable<InstallationUpload> files, CancellationToken ct)
    {
        if (profile.Length is 0 or > 80 || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw Invalid();
        var imports = Path.Combine(options.StateDirectory, "administration", "client-imports");
        Directory.CreateDirectory(imports);
        RejectLink(imports);
        var generated = Path.Combine(options.StateDirectory, "client-data");
        Directory.CreateDirectory(generated);
        RejectLink(generated);
        _remainingStorage = options.ClientDataStorageBytes - StoredBytes(imports, ct) - StoredBytes(generated, ct);
        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(imports, ".pending-" + token);
        var destination = Path.Combine(imports, token);
        Directory.CreateDirectory(staging);
        var complete = false;
        bool? installedGame = null;
        try
        {
            var fileCount = 0;
            await foreach (var file in files.WithCancellation(ct).ConfigureAwait(false))
            {
                if (++fileCount > options.ClientDataFileLimit || archive && fileCount > 1) throw Limit();
                if (archive)
                {
                    var package = Path.Combine(staging, ".package.zip");
                    await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
                    {
                        await CopyAsync(file.Content, output, false, ct).ConfigureAwait(false);
                        // Inspect the directory size before ZipArchive can allocate an entry collection.
                        CheckZipDirectory(output);
                        output.Position = 0;
                        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
                        foreach (var entry in zip.Entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (++_entries > options.ClientDataFileLimit) throw Limit();
                            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                            if (unixType is not (0 or 0x4000 or 0x8000) || (entry.ExternalAttributes & 0x400) != 0) throw Invalid();
                            var segments = Segments(entry.FullName, directory: entry.FullName.EndsWith('/'));
                            if (entry.FullName.EndsWith('/')) continue;
                            var relative = SelectPreparedPath(segments, profile);
                            if (relative is null) continue;
                            if (entry.Length > options.ClientDataExtractedBytes - _extracted || entry.Length > _remainingStorage) throw Limit();
                            await using var input = entry.Open();
                            await WriteAsync(staging, profile, relative, input, true, ct).ConfigureAwait(false);
                        }
                    }
                    File.Delete(package);
                }
                else
                {
                    if (++_entries > options.ClientDataFileLimit) throw Limit();
                    var segments = Segments(file.Name);
                    // webkitRelativePath always includes the directory chosen by the operator.
                    if (segments.Length < 2) throw Invalid();
                    var relative = SelectPreparedPath(segments, profile);
                    var gameArchive = relative is null && !HasDataMarker(segments) && IsArchive(segments[^1]);
                    if (installedGame.HasValue && installedGame.Value != gameArchive) throw Invalid();
                    installedGame = gameArchive;
                    if (gameArchive)
                    {
                        if (!ClientDataGenerator.IsMounted(string.Join('/', segments[1..]))) continue;
                        relative = [".client", .. segments[1..]];
                    }
                    if (relative is null) throw Invalid();
                    await WriteAsync(staging, profile, relative, file.Content, false, ct).ConfigureAwait(false);
                }
            }
            if (_written.Count == 0 || _extracted == 0) throw Invalid();
            ct.ThrowIfCancellationRequested();
            if (installedGame == true)
            {
                var generatedBuild = Path.Combine(staging, ".generated");
                var finalBuild = Path.Combine(generated, profile);
                if (Directory.Exists(finalBuild) || File.Exists(finalBuild))
                    throw new ArgumentException("client_data_exists");
                if (_remainingStorage <= 0) throw Limit();
                new ClientDataGenerator().Generate(profile, Path.Combine(staging, profile, ".client"), generatedBuild,
                    options.ClientDataFileLimit, Math.Min(options.ClientDataExtractedBytes, _remainingStorage), ct);
                ct.ThrowIfCancellationRequested();
                Directory.Move(generatedBuild, finalBuild);
                return generated;
            }
            Directory.Move(staging, destination);
            complete = true;
            return destination;
        }
        catch (InvalidDataException e) when (e.Message.StartsWith("Missing required client files: ", StringComparison.Ordinal))
        {
            throw new ArgumentException("client_data_missing:" + e.Message["Missing required client files: ".Length..]);
        }
        catch (InvalidDataException e) when (e.Message.Contains("limit exceeded", StringComparison.Ordinal)) { throw Limit(); }
        catch (InvalidDataException) { throw Invalid(); }
        finally
        {
            // Only the unique directory created by this call is removed; retained imports are never pruned here.
            if (!complete && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private long StoredBytes(string imports, CancellationToken ct)
    {
        long bytes = 0;
        var copies = 0;
        var entries = 0;
        var pending = new Stack<string>();
        foreach (var item in Directory.EnumerateFileSystemEntries(imports))
        {
            ct.ThrowIfCancellationRequested();
            if (++copies >= options.ClientDataCopyLimit) throw Limit();
            RejectLink(item);
            if (Directory.Exists(item)) pending.Push(item); else bytes += new FileInfo(item).Length;
        }
        while (pending.TryPop(out var directory))
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > (long)options.ClientDataFileLimit * options.ClientDataCopyLimit * 4) throw Limit();
                RejectLink(item);
                if (Directory.Exists(item)) pending.Push(item); else bytes += new FileInfo(item).Length;
                if (bytes >= options.ClientDataStorageBytes) throw Limit();
            }
        return bytes;
    }

    private async ValueTask WriteAsync(string staging, string profile, string[] relative, Stream input, bool extracted, CancellationToken ct)
    {
        var key = string.Join('/', relative);
        if (!_written.Add(key)) throw Invalid();
        var file = Path.Combine([staging, profile, .. relative]);
        var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await CopyAsync(input, output, extracted, ct, dataFile: true).ConfigureAwait(false);
    }

    private async ValueTask CopyAsync(Stream input, Stream output, bool extracted, CancellationToken ct, bool dataFile = false)
    {
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            _remainingStorage -= read;
            if (!extracted) _received += read;
            if (dataFile) _extracted += read;
            if (_remainingStorage < 0 || _received > options.ClientDataUploadBytes || _extracted > options.ClientDataExtractedBytes) throw Limit();
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private string[]? SelectPreparedPath(string[] parts, string profile)
    {
        var marker = Array.FindIndex(parts, IsDataMarker);
        if (marker < 0) return null;
        if (marker > 2 || marker == parts.Length - 1) throw Invalid();
        var prefix = parts[..marker];
        if (prefix.Any(p => profiles.Contains(p) && !p.Equals(profile, StringComparison.OrdinalIgnoreCase))) return null;
        var relative = parts[marker..];
        relative[0] = relative[0].ToLowerInvariant();
        return relative;
    }

    private static string[] Segments(string name, bool directory = false)
    {
        if (name.Length is 0 or > 1024 || name.StartsWith('/') || name.StartsWith('\\')) throw Invalid();
        var normalized = name.Replace('\\', '/');
        var parts = (directory ? normalized.TrimEnd('/') : normalized).Split('/');
        if (parts.Length > 32 || parts.Any(p => p.Length is 0 or > 255 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || IsReservedName(p))) throw Invalid();
        return parts;
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9';
    }

    private void CheckZipDirectory(FileStream stream)
    {
        // The classic ZIP directory terminator is 22 bytes followed by at most 65535 comment bytes.
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            var end = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50 ||
                offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != tail.Length) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count) throw Invalid();
            if (count == ushort.MaxValue || count > options.ClientDataFileLimit || size > (long)options.ClientDataFileLimit * 4096) throw Limit();
            if ((long)start + size != stream.Length - tail.Length + offset) throw Invalid();
            return;
        }
        throw Invalid();
    }

    private static bool IsDataMarker(string part) => part.Equals("data", StringComparison.OrdinalIgnoreCase) || part.Equals("dataraw", StringComparison.OrdinalIgnoreCase);
    private static bool HasDataMarker(string[] parts) => parts.Any(IsDataMarker);
    private static bool IsArchive(string name) => name.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase);
    private static void RejectLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid(); }
    private static ArgumentException Invalid() => new("client_data_invalid");
    private static ArgumentException Limit() => new("client_data_limit");
}
