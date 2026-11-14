using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.ClientData;

/// <summary>One archive the compiled file was written from: its client path, size and last write time.</summary>
internal readonly record struct ArchiveStamp(string Path, long Length, long WriteTicks);

/// <summary>A loose file as a catalog found it: its size and last write time, or <see cref="Absent"/>.</summary>
internal readonly record struct LooseStamp(long Length, long WriteTicks)
{
    internal static readonly LooseStamp Absent = new(-1, 0);
}

/// <summary>
/// One build's compiled client data file: the catalogs rows built from the client data, kept so the next start reads
/// them instead of unpacking and parsing workbooks. The file is a cache. Its header names the archives (path, size,
/// last write time) and the server's assemblies it was written from; a start whose header differs uses none of it.
/// Each section additionally names the row's inputs, the assemblies of the row that wrote it and every client file
/// the row asked for with its loose-file state, and is used only while all of those still hold. A section that is
/// stale or damaged is compiled again and replaced; nothing is ever read from a section that does not match.
/// </summary>
/// <remarks>
/// Layout, little-endian: magic <c>BFFCDAT1</c>; u32 format version; three module version ids (the entry assembly,
/// this plugin, the contracts); i32 archive count, then per archive its path, i64 size, i64 write ticks; i32 section
/// count, then per section its name, 32-byte inputs hash, 32-byte assembly stamp, i32 file count with per file its
/// client path, i64 loose size (-1 when not loose) and i64 loose write ticks, then i64 payload offset counted from
/// the end of this directory, i32 payload length and the payload's 32-byte SHA-256; the payloads follow.
/// </remarks>
internal sealed class CompiledDataFile
{
    private const ulong Magic = 0x3154414443464642; // "BFFCDAT1"
    private const uint FormatVersion = 1;
    private const int MaxSections = 256;
    private const int MaxSectionsPerName = 4;
    private const int MaxFilesPerSection = 4096;
    private const int MaxArchives = 65536;
    private const int MaxNameCharacters = 1024;
    /// <summary>The largest catalog kept; a larger one is built on every start and reported.</summary>
    internal const int MaxPayloadBytes = 64 * 1024 * 1024;

    private readonly string _path;
    private readonly ArchiveStamp[] _archives;
    private readonly Guid[] _program;
    private readonly Func<string, LooseStamp> _loose;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<Section> _sections = [];
    private readonly Dictionary<Assembly, byte[]> _stamps = [];
    private bool _dirty;
    private bool _writeAtOnce;
    private bool _summarized;

    private CompiledDataFile(string path, ArchiveStamp[] archives, Guid[] program, Func<string, LooseStamp> loose, ILogger logger)
    {
        _path = path;
        _archives = archives;
        _program = program;
        _loose = loose;
        _logger = logger;
    }

    /// <summary>Gets how many catalogs this start read from the file.</summary>
    internal int Loaded { get; private set; }

    /// <summary>Gets how many catalogs this start built from the client data.</summary>
    internal int Compiled { get; private set; }

    /// <summary>Gets the file's path.</summary>
    internal string FilePath => _path;

    /// <summary>
    /// Opens the build's compiled file. A missing file, or one written from other archives or by other assemblies,
    /// yields no sections; the reason is logged and the file is rewritten as catalogs are compiled.
    /// </summary>
    internal static CompiledDataFile Open(string path, IEnumerable<ArchiveStamp> archives, Func<string, LooseStamp> loose, ILogger logger)
    {
        var program = new[]
        {
            Assembly.GetEntryAssembly()?.ManifestModule.ModuleVersionId ?? Guid.Empty,
            typeof(CompiledDataFile).Assembly.ManifestModule.ModuleVersionId,
            typeof(ICompiledClientData).Assembly.ManifestModule.ModuleVersionId,
        };
        var file = new CompiledDataFile(Path.GetFullPath(path), archives.OrderBy(archive => archive.Path, StringComparer.Ordinal).ToArray(),
            program, loose, logger);
        file.ReadDirectory();
        return file;
    }

    private void ReadDirectory()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("clientdata: no compiled client data at '{Path}'; catalogs are built from the client data and kept there for the next start.", _path);
            return;
        }
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt64() != Magic || reader.ReadUInt32() != FormatVersion)
            {
                Discard("it is not a compiled client data file of this format");
                return;
            }
            foreach (var expected in _program)
                if (new Guid(reader.ReadBytes(16)) != expected)
                {
                    Discard("the server's assemblies changed");
                    return;
                }
            var count = Bounded(reader.ReadInt32(), MaxArchives, "archive count");
            var archives = new ArchiveStamp[count];
            for (var index = 0; index < count; index++)
                archives[index] = new(Name(reader), reader.ReadInt64(), reader.ReadInt64());
            if (ArchiveDifference(archives) is { } difference)
            {
                Discard(difference);
                return;
            }
            var sections = Bounded(reader.ReadInt32(), MaxSections, "section count");
            var read = new List<Section>(sections);
            for (var index = 0; index < sections; index++)
            {
                var name = Name(reader);
                var inputs = Exactly(reader, 32);
                var stamp = Exactly(reader, 32);
                var files = new (string Path, LooseStamp Loose)[Bounded(reader.ReadInt32(), MaxFilesPerSection, "file count")];
                for (var file = 0; file < files.Length; file++)
                    files[file] = (Name(reader), new(reader.ReadInt64(), reader.ReadInt64()));
                var offset = reader.ReadInt64();
                var length = Bounded(reader.ReadInt32(), MaxPayloadBytes, "payload length");
                var hash = Exactly(reader, 32);
                read.Add(new(name, inputs, stamp, files, hash) { Offset = offset, Length = length });
            }
            // Payload offsets count from the end of the directory.
            var payloads = stream.Position;
            foreach (var section in read)
            {
                if (section.Offset < 0 || section.Offset > stream.Length - payloads - section.Length)
                    throw new InvalidDataException("a section lies outside the file");
                section.Offset += payloads;
            }
            _sections.AddRange(read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // EndOfStreamException is an IOException: a truncated file lands here too.
            _sections.Clear();
            _logger.LogWarning("clientdata: compiled client data '{Path}' cannot be read ({Reason}); catalogs are built from the client data and the file is rewritten.",
                _path, error.Message);
        }
    }

    private void Discard(string reason) =>
        _logger.LogInformation("clientdata: compiled client data '{Path}' is not used because {Reason}; catalogs are built from the client data and the file is rewritten.",
            _path, reason);

    private string? ArchiveDifference(ArchiveStamp[] written)
    {
        if (written.Length != _archives.Length)
            return $"it was written from {written.Length} archive(s) and the build now has {_archives.Length}";
        for (var index = 0; index < written.Length; index++)
            if (written[index] != _archives[index])
                return written[index].Path == _archives[index].Path
                    ? $"archive '{_archives[index].Path}' changed"
                    : $"archive '{written[index].Path}' was replaced by '{_archives[index].Path}'";
        return null;
    }

    /// <summary>
    /// Returns the catalog from its section when the section is current; otherwise builds it, checks that it
    /// survives a round trip, and keeps it. A catalog that cannot be kept is still returned as built.
    /// </summary>
    internal T Load<T>(IClientDataStore store, ICompiledCatalog<T> catalog, string inputs, Func<IClientDataStore, T> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        if (TryRead(catalog, inputs, out var kept, out var reason))
            return kept;
        var tracker = new TrackingStore(store, _loose);
        var built = compile(tracker);
        Put(catalog, inputs, built, tracker.Files(), reason);
        return built;
    }

    /// <summary>
    /// Reads the catalog from its section when the section is current. Otherwise <paramref name="reason"/> says why
    /// the catalog has to be built.
    /// </summary>
    internal bool TryRead<T>(ICompiledCatalog<T> catalog, string inputs, out T value, out string reason)
    {
        var (name, inputsHash) = Key(catalog, inputs);
        byte[] stamp;
        Section? section;
        lock (_gate)
        {
            stamp = StampOf(catalog.GetType().Assembly);
            section = _sections.Find(candidate => candidate.Name == name && candidate.Inputs.AsSpan().SequenceEqual(inputsHash));
        }
        value = default!;
        reason = "it was not compiled before";
        if (section is null) return false;
        if (Staleness(section, stamp) is { } stale)
        {
            reason = stale;
            return false;
        }
        try
        {
            var payload = Payload(section);
            // The array is visible to the catalog so that it can be read in place instead of being copied.
            using var input = new MemoryStream(payload, 0, payload.Length, writable: false, publiclyVisible: true);
            value = catalog.Read(input);
            if (input.Position != input.Length)
                throw new InvalidDataException("the catalog did not read its whole section");
            lock (_gate) Loaded++;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            value = default!;
            reason = "its section cannot be read (" + error.Message + ")";
            _logger.LogWarning("clientdata: compiled catalog '{Name}' in '{Path}' cannot be read ({Reason}); it is built from the client data again.",
                name, _path, error.Message);
            return false;
        }
    }

    /// <summary>Keeps a catalog that was just built, with the client files it was built from.</summary>
    internal void Put<T>(ICompiledCatalog<T> catalog, string inputs, T built, (string Path, LooseStamp Loose)[] files, string reason)
    {
        var (name, inputsHash) = Key(catalog, inputs);
        byte[] stamp;
        lock (_gate) stamp = StampOf(catalog.GetType().Assembly);
        _logger.LogInformation("clientdata: catalog '{Name}' was built from the client data because {Reason}.", name, reason);
        Keep(catalog, built, new(name, inputsHash, stamp, files, []));
    }

    /// <summary>Gets the loose-file state a section's validity rests on for one client path.</summary>
    internal (string Path, LooseStamp Loose) FileState(string path)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        return (key, _loose(key));
    }

    private static (string Name, byte[] Inputs) Key<T>(ICompiledCatalog<T> catalog, string inputs)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inputs);
        var name = catalog.Name;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameCharacters)
            throw new ArgumentException("A compiled catalog needs a name of at most 1024 characters.", nameof(catalog));
        return (name, SHA256.HashData(Encoding.UTF8.GetBytes(inputs)));
    }

    private string? Staleness(Section section, byte[] stamp)
    {
        if (!section.Stamp.AsSpan().SequenceEqual(stamp))
            return "the row's assemblies changed";
        foreach (var (path, loose) in section.Files)
            if (_loose(path) != loose)
                return $"the loose file state of '{path}' changed";
        return null;
    }

    private void Keep<T>(ICompiledCatalog<T> catalog, T built, Section section)
    {
        byte[] payload;
        try
        {
            payload = Bytes(catalog, built);
            // Reading the section back and writing it again must give the same bytes. This does not replace the
            // catalog's equality test, but a format that loses or reorders data on the way never reaches the file.
            // The array is visible to the catalog so that it can be read in place instead of being copied.
            using var input = new MemoryStream(payload, 0, payload.Length, writable: false, publiclyVisible: true);
            var again = catalog.Read(input);
            if (input.Position != input.Length || !Bytes(catalog, again).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("the catalog read back from its section is not the catalog that was written");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (_gate) Compiled++;
            _logger.LogError("clientdata: catalog '{Name}' is not kept in '{Path}' and will be built on every start: {Reason}.",
                section.Name, _path, error.Message);
            return;
        }
        section.Pending = payload;
        section.Length = payload.Length;
        section.Hash = SHA256.HashData(payload);
        bool write;
        lock (_gate)
        {
            Compiled++;
            _sections.RemoveAll(candidate => candidate.Name == section.Name && candidate.Inputs.AsSpan().SequenceEqual(section.Inputs));
            // Rows of one build may load one catalog with different inputs; a few are kept, the oldest give way.
            while (_sections.Count(candidate => candidate.Name == section.Name) >= MaxSectionsPerName)
                _sections.RemoveAt(_sections.FindIndex(candidate => candidate.Name == section.Name));
            while (_sections.Count >= MaxSections)
                _sections.RemoveAt(0);
            _sections.Add(section);
            _dirty = true;
            write = _writeAtOnce;
        }
        if (write) Flush(atOnceFromNow: true);
    }

    private static byte[] Bytes<T>(ICompiledCatalog<T> catalog, T value)
    {
        using var output = new MemoryStream();
        catalog.Write(value, output);
        if (output.Length > MaxPayloadBytes)
            throw new InvalidDataException($"the catalog is {output.Length} bytes; at most {MaxPayloadBytes} are kept");
        return output.ToArray();
    }

    private byte[] Payload(Section section)
    {
        var payload = section.Pending;
        if (payload is null)
        {
            payload = new byte[section.Length];
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            stream.Position = section.Offset;
            stream.ReadExactly(payload);
        }
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(section.Hash))
            throw new InvalidDataException("its checksum does not match");
        return payload;
    }

    /// <summary>
    /// Writes the file when catalogs were compiled since it was read or last written. The new file replaces the old
    /// one in one rename, so a reader never sees half of it. A failure is logged and the catalogs are simply built
    /// again by the next start.
    /// </summary>
    /// <param name="atOnceFromNow">True once the start is over: a catalog compiled later is written when it is built.</param>
    internal void Flush(bool atOnceFromNow)
    {
        lock (_gate)
        {
            _writeAtOnce |= atOnceFromNow;
            if (!_dirty) return;
            _dirty = false;
            var temporary = _path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                // Sections that were not compiled now are carried over from the old file.
                var kept = new List<(Section Section, byte[] Payload)>(_sections.Count);
                foreach (var section in _sections)
                {
                    try { kept.Add((section, Payload(section))); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        _logger.LogWarning("clientdata: compiled catalog '{Name}' could not be carried over ({Reason}); it is built again by the next start.",
                            section.Name, error.Message);
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    foreach (var id in _program) writer.Write(id.ToByteArray());
                    writer.Write(_archives.Length);
                    foreach (var archive in _archives)
                    {
                        writer.Write(archive.Path);
                        writer.Write(archive.Length);
                        writer.Write(archive.WriteTicks);
                    }
                    writer.Write(kept.Count);
                    long relative = 0;
                    foreach (var (section, payload) in kept)
                    {
                        writer.Write(section.Name);
                        writer.Write(section.Inputs);
                        writer.Write(section.Stamp);
                        writer.Write(section.Files.Length);
                        foreach (var (path, loose) in section.Files)
                        {
                            writer.Write(path);
                            writer.Write(loose.Length);
                            writer.Write(loose.WriteTicks);
                        }
                        writer.Write(relative);
                        writer.Write(payload.Length);
                        writer.Write(section.Hash);
                        relative += payload.Length;
                    }
                    writer.Flush();
                    var offset = stream.Position;
                    foreach (var (section, payload) in kept)
                    {
                        stream.Write(payload);
                        section.Offset = offset;
                        offset += payload.Length;
                    }
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _sections.Clear();
                foreach (var (section, _) in kept)
                {
                    // The bytes are in the file now; the start does not keep a second copy of every catalog.
                    section.Pending = null;
                    _sections.Add(section);
                }
                _logger.LogInformation("clientdata: wrote {Sections} compiled catalog(s) to '{Path}'.", kept.Count, _path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Without the file the pending bytes are useless, and the old offsets may no longer be true.
                _sections.Clear();
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                _logger.LogWarning("clientdata: compiled client data could not be written to '{Path}' ({Reason}); the next start builds its catalogs from the client data again.",
                    _path, error.Message);
            }
        }
    }

    /// <summary>Logs once what this start took from the file and what it had to build; the perf harness reads it.</summary>
    internal void LogSummary()
    {
        int loaded, compiled;
        lock (_gate)
        {
            if (_summarized) return;
            _summarized = true;
            (loaded, compiled) = (Loaded, Compiled);
        }
        _logger.LogInformation("clientdata: compiled client data '{Path}': {Loaded} catalog(s) read, {Compiled} built from the client data.",
            _path, loaded, compiled);
    }

    // A row's catalog depends on the row's own code and on the project assemblies that code calls. Their module
    // version ids change with every build that changes them, so a section never outlives the code that wrote it.
    private byte[] StampOf(Assembly assembly)
    {
        if (_stamps.TryGetValue(assembly, out var known)) return known;
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var candidate in AssemblyLoadContext.Default.Assemblies.Concat(context?.Assemblies ?? []))
            if (candidate.GetName().Name is { } simple)
                loaded[simple] = candidate; // The row's own context wins over the shared one.
        var parts = new SortedDictionary<string, Guid>(StringComparer.Ordinal)
        {
            [assembly.GetName().Name ?? ""] = assembly.ManifestModule.ModuleVersionId,
        };
        foreach (var reference in assembly.GetReferencedAssemblies())
            if (reference.Name is { } simple && simple.StartsWith("BubbleFighter.", StringComparison.Ordinal)
                && loaded.TryGetValue(simple, out var referenced))
                parts[simple] = referenced.ManifestModule.ModuleVersionId;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (simple, id) in parts)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(simple));
            hash.AppendData([0]);
            hash.AppendData(id.ToByteArray());
        }
        return _stamps[assembly] = hash.GetHashAndReset();
    }

    private static int Bounded(int value, int maximum, string what) =>
        value >= 0 && value <= maximum ? value : throw new InvalidDataException($"its {what} is out of range");

    private static string Name(BinaryReader reader)
    {
        var text = reader.ReadString();
        return text.Length <= MaxNameCharacters ? text : throw new InvalidDataException("a name is too long");
    }

    private static byte[] Exactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException("the file ends inside its directory");
    }

    private sealed class Section(string name, byte[] inputs, byte[] stamp, (string Path, LooseStamp Loose)[] files, byte[] hash)
    {
        public string Name { get; } = name;
        public byte[] Inputs { get; } = inputs;
        public byte[] Stamp { get; } = stamp;
        public (string Path, LooseStamp Loose)[] Files { get; } = files;
        public byte[] Hash { get; set; } = hash;
        public long Offset { get; set; }
        public int Length { get; set; }
        /// <summary>The payload while it is not in the file yet.</summary>
        public byte[]? Pending { get; set; }
    }

    /// <summary>
    /// The store a catalog is compiled through. It reads what the build's store reads and notes every client path
    /// asked for with its loose-file state at that moment, which is what the section's validity later rests on.
    /// </summary>
    private sealed class TrackingStore(IClientDataStore inner, Func<string, LooseStamp> loose)
        : IClientDataStore, IClientWorkbookCache, ICompiledClientData
    {
        private readonly Dictionary<string, LooseStamp> _files = new(StringComparer.OrdinalIgnoreCase);

        internal (string Path, LooseStamp Loose)[] Files()
        {
            lock (_files)
                return _files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => (file.Key, file.Value)).ToArray();
        }

        private string Note(string path)
        {
            var key = path.Replace('\\', '/').TrimStart('/');
            lock (_files)
                if (!_files.ContainsKey(key))
                    _files.Add(key, loose(key));
            return path;
        }

        public bool Exists(string path) => inner.Exists(Note(path));

        public byte[] Read(string path) => inner.Read(Note(path));

        public byte[] Read(string path, int maximumBytes) => inner.Read(Note(path), maximumBytes);

        public long Length(string path) => inner.Length(Note(path));

        public Stream OpenRead(string path) => inner.OpenRead(Note(path));

        public Stream OpenRead(string path, long maximumBytes) => inner.OpenRead(Note(path), maximumBytes);

        public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(string path, IReadOnlyCollection<string> sheetNames,
            Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> parse) =>
            inner is IClientWorkbookCache cache ? cache.Sheets(Note(path), sheetNames, parse) : parse(sheetNames);

        // A catalog compiled inside another is part of it: it is built here, so its files count for the outer one.
        public T Load<T>(ICompiledCatalog<T> catalog, string inputs, Func<IClientDataStore, T> compile) => compile(this);
    }
}
