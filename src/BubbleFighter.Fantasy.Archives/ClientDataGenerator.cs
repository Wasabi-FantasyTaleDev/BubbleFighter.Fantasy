using System.Security.Cryptography;
using System.Text.Json;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>Generates server data from mounted client archives using the shipped copy and extraction rules.</summary>
public sealed class ClientDataGenerator
{
    /// <summary>Gets the shared build manifest used by native and script front ends.</summary>
    public static string ManifestJson
    {
        get
        {
            using var stream = typeof(ClientDataGenerator).Assembly.GetManifestResourceStream(
                "BubbleFighter.Fantasy.Archives.client-data-builds.json")
                ?? throw new InvalidOperationException("Client data generation rules are missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Determines whether the client mounts an archive at this relative path.</summary>
    public static bool IsMounted(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (relative.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 2 && (parts[0].Equals("Pack", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("Packs", StringComparison.OrdinalIgnoreCase));
        return relative.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2
            && Path.GetFileNameWithoutExtension(parts[^1]).Equals(parts[^2], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks whether a mounted archive contains files under its client lookup prefix.</summary>
    public static bool IsReachable(string relative, IArchiveReader reader)
    {
        var normalized = relative.Replace('\\', '/');
        return !normalized.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('/') && reader.Entries.Any(e => e.Path.StartsWith(
                normalized[..normalized.LastIndexOf('/')] + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes a new build data directory. Required files and all selected sizes are checked before any output;
    /// the destination must be empty, and failure removes only files created by this invocation.
    /// </summary>
    /// <param name="build">Build key in the embedded manifest.</param>
    /// <param name="client">Installed client root.</param>
    /// <param name="output">New directory containing data, dataraw and sources.tsv.</param>
    /// <param name="fileLimit">Maximum inspected entries and generated files.</param>
    /// <param name="byteLimit">Maximum total copied or unpacked bytes.</param>
    /// <param name="cancellationToken">Cancels planning and generation between bounded reads.</param>
    public void Generate(string build, string client, string output, int fileLimit, long byteLimit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLimit, 1);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(client));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(root, comparison) || target.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison)
            || root.StartsWith(Path.EndsInDirectorySeparator(target) ? target : target + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Client and generated data directories must not overlap.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Client data generation requires an empty destination.");
        RejectAncestorLinks(root);
        RejectAncestorLinks(target);
        using var manifest = JsonDocument.Parse(ManifestJson);
        var rules = manifest.RootElement.GetProperty("rules");
        var builds = manifest.RootElement.GetProperty("builds").EnumerateArray();
        var match = builds.FirstOrDefault(b => b.GetProperty("id").GetString() == build);
        var required = (match.ValueKind != JsonValueKind.Undefined && match.TryGetProperty("required", out var own)
            ? own : rules.GetProperty("required"))
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        var copyFolders = rules.GetProperty("copy_folders").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rawFolders = rules.GetProperty("raw_folders").EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var mounted = EnumerateMounted(root, fileLimit, cancellationToken);
        var members = new Dictionary<string, (string Archive, long Size, bool Copy)>(StringComparer.OrdinalIgnoreCase);
        var copies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        foreach (var relative in mounted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = Archive.Open(Inside(root, relative), relative);
            if (!IsReachable(relative, reader)) continue;
            var allCopy = reader.Entries.Count > 0 && reader.Entries.All(e => copyFolders.Contains(e.Path.Split('/')[0]));
            foreach (var entry in reader.Entries)
            {
                if (++inspected > fileLimit) throw new InvalidDataException("Client archive entry limit exceeded.");
                _ = Inside(target, entry.Path);
                if (entry.Size < 0) throw new InvalidDataException("Negative archive member size.");
                members.TryAdd(entry.Path, (relative, entry.Size, allCopy));
            }
            if (allCopy) copies.Add(relative, new FileInfo(Inside(root, relative)).Length);
        }
        var missing = required.Where(p => !members.ContainsKey(p)).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException("Missing required client files: " + string.Join(", ", missing));
        var raw = members.Where(p => !p.Value.Copy && (required.Contains(p.Key, StringComparer.OrdinalIgnoreCase)
            || rawFolders.TryGetValue(p.Key.Split('/')[0], out var extensions) && extensions.Contains(Path.GetExtension(p.Key))))
            .ToArray();
        long total = 0;
        foreach (var size in copies.Values.Concat(raw.Select(p => p.Value.Size)))
        {
            if (size > byteLimit - total) throw new InvalidDataException("Client data byte limit exceeded.");
            total += size;
        }
        if (copies.Count + raw.Length + 1 > fileLimit) throw new InvalidDataException("Generated file limit exceeded.");
        if (total == 0) throw new InvalidDataException("No server data was selected.");
        Directory.CreateDirectory(target);
        var written = new List<string>();
        var lines = new List<string> { "target\tsource\tmember\tsize\tsha256" };
        try
        {
            foreach (var (relative, length) in copies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Inside(target, "data/" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = File.OpenRead(Inside(root, relative));
                using var outputFile = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                written.Add(destination);
                if (source.Length != length) throw new IOException("Client archive changed during generation.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException();
                    outputFile.Write(buffer, 0, read);
                    hash.AppendData(buffer.AsSpan(0, read));
                    remaining -= read;
                }
                lines.Add($"data/{relative}\t{relative}\t\t{length}\t{Convert.ToHexStringLower(hash.GetHashAndReset())}");
            }
            foreach (var group in raw.GroupBy(p => p.Value.Archive, StringComparer.OrdinalIgnoreCase))
            {
                using var reader = Archive.Open(Inside(root, group.Key), group.Key);
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = reader.Read(item.Key);
                    if (bytes.LongLength != item.Value.Size) throw new IOException("Client member changed during generation.");
                    var destination = Inside(target, "dataraw/" + item.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    { written.Add(destination); file.Write(bytes); }
                    lines.Add($"dataraw/{item.Key}\t{group.Key}\t{item.Key}\t{bytes.LongLength}\t{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var sources = Inside(target, "sources.tsv");
            var provenance = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            if (provenance.LongLength > byteLimit - total)
                throw new InvalidDataException("Client data byte limit exceeded.");
            using var provenanceFile = new FileStream(sources, FileMode.CreateNew, FileAccess.Write);
            written.Add(sources);
            provenanceFile.Write(provenance);
        }
        catch
        {
            foreach (var file in written) File.Delete(file);
            throw;
        }
    }

    /// <summary>Lists mounted archives in client lookup order without following filesystem links.</summary>
    public static IReadOnlyList<string> EnumerateMounted(string client, int fileLimit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileLimit, 1);
        var root = Path.GetFullPath(client);
        RejectAncestorLinks(root);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var result = new List<string>();
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > fileLimit) throw new InvalidDataException("Client directory entry limit exceeded.");
                RejectLink(entry);
                if (Directory.Exists(entry))
                {
                    if (directory.Depth >= 32) throw new InvalidDataException("Client directory depth limit exceeded.");
                    pending.Push((entry, directory.Depth + 1));
                }
                else
                {
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    if (IsMounted(relative)) result.Add(relative);
                }
            }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string Inside(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(s => s.Length == 0 || s is "." or ".."
            || s.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c)) || s.EndsWith('.') || s.EndsWith(' ')))
            throw new InvalidDataException("Invalid client archive path.");
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Client archive path escapes its root.");
        return target;
    }

    private static void RejectAncestorLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists) RejectLink(current.FullName);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Client data generation does not follow filesystem links.");
    }
}
