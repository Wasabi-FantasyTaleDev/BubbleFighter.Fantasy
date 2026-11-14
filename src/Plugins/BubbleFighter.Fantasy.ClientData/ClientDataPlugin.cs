using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.ClientData;

/// <summary>
/// Publishes <see cref="IClientDataStore"/> over the client's own archives. Like the client,
/// lookups go by the file's path inside the archives, never by archive name: the client opens
/// every pack and resolves a member path to whichever archive holds it
/// (kr-1256 <c>sub_1018640</c>, <c>sub_1017EF0</c>). Loose files under <see cref="ClientDataOptions.RawDirectory"/>
/// take precedence, so edited data can be tried without repacking.
/// </summary>
[Plugin("clientdata", Category = "core", Names = ["en=Client data", "zh-Hans=客户端数据", "zh-Hant=用戶端資料", "ko=클라이언트 데이터"], Description = "Client data files served from PKN/BFZ archives, with a loose-file override directory.",
    Provides = [typeof(IClientDataStore)], Optional = [typeof(IHostReadiness)])]
public sealed class ClientDataPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ClientDataOptions>();
        var store = ClientDataStore.Open(options.DataDirectory, options.RawDirectory, context.Logger, options.CompiledFile);
        context.Effect(store);
        ClientDataStore.BecomeCurrent(store);
        // Parsed workbooks are shared only while the server starts. A store opened later (a reload) never keeps any.
        // The compiled file is written then too; a catalog compiled later is written as soon as it is built.
        context.Effect(context.Events.On<StartupConvergedEvent>(_ => store.EndStartup()));
        if (context.Services.TryGet<IHostReadiness>(out var readiness) && readiness is { IsReady: true })
            store.EndStartup();
        context.Effect(context.Services.Register<IClientDataStore>(store));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Configuration of the <c>clientdata</c> row.</summary>
public sealed class ClientDataOptions
{
    /// <summary>
    /// Gets or sets the directory holding the client's archives (<c>.pkn</c> of either layout, <c>.bfz</c>) at
    /// their original client-relative paths with the client's own letter case, for example
    /// <c>Packs/Config_00000.pkn</c>, <c>Pack/Config0000.pkn</c> or <c>Config/Config.bfz</c>: each archive's
    /// keys derive from that path. Only the archives the server reads need to be present.
    /// </summary>
    [Required]
    public string DataDirectory { get; set; } = "client-data/kr-1256/data";

    /// <summary>
    /// Gets or sets the directory of loose, unpacked files laid out by their client paths (for
    /// example <c>ServerShared/MapMeta.xml</c>). A file here wins over the same path inside an
    /// archive. The directory may be absent.
    /// </summary>
    [Required]
    public string RawDirectory { get; set; } = "client-data/kr-1256/dataraw";

    /// <summary>
    /// Gets or sets the file that keeps this build's compiled catalogs, so a later start reads them instead of
    /// unpacking and parsing the client's workbooks. The file is a cache: it is used only while the archives, the
    /// loose files and the server's assemblies are the ones it was written from, it is rewritten when they change,
    /// and deleting it changes nothing but the time the next start takes. Each build needs its own file. Empty keeps
    /// no file and builds every catalog on every start.
    /// </summary>
    public string CompiledFile { get; set; } = "";
}

/// <summary>
/// The loose-file directory first, then the archives, resolved the way the client resolves them. A
/// <c>.bfz</c> client takes the archive named after the nearest folder of the file that has one: for
/// <c>Maps/rel_X/a.xml</c> it tries <c>Maps/rel_X/rel_X.bfz</c>, then <c>Maps/Maps.bfz</c> (kr-812
/// <c>sub_A4B600</c>, called from <c>sub_A4BA00</c>). A <c>.pkn</c> client opens every pack in its pack
/// folder and reads a file from whichever pack holds it (cn-279 <c>sub_8A18B0</c>, kr-1256 <c>sub_1018640</c>).
/// Lookups ignore letter case, as on the client's Windows file system, also on Linux and macOS. Only the
/// archives' indexes stay in memory; file contents are read when asked and not cached.
/// </summary>
internal sealed class ClientDataStore : IClientDataStore, IClientWorkbookCache, ICompiledClientData, IDisposable
{
    private readonly string? _raw;
    private CompiledDataFile? _compiled;
    private readonly Lock _workbooksGate = new();
    // Client path to the worksheets parsed so far; null once released, after which nothing is kept.
    private Dictionary<string, Workbook>? _workbooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IArchiveReader> _archives = [];
    private readonly Dictionary<string, IArchiveReader> _packMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IArchiveReader> _bfzByPath = new(StringComparer.OrdinalIgnoreCase);

    // Loose files are looked up when asked, not indexed: a build's dataraw holds thousands of files and a
    // small server should not keep every path in memory.
    private ClientDataStore(string? raw) => _raw = raw;

    internal int ArchiveCount => _archives.Count;

    internal int MemberCount => _packMembers.Count + _bfzByPath.Values.Sum(reader => reader.Entries.Count);

    internal static ClientDataStore Open(string dataDirectory, string rawDirectory, ILogger logger, string? compiledFile = null)
    {
        var stamps = new List<ArchiveStamp>();
        var data = Path.GetFullPath(dataDirectory);
        var raw = Path.GetFullPath(rawDirectory);
        if (!Directory.Exists(data) && !Directory.Exists(raw))
            throw new ConfigException(
                $"clientdata: neither data_directory '{data}' nor raw_directory '{raw}' exists. Provision the configured client-data directories before enabling this row.");

        var store = new ClientDataStore(Directory.Exists(raw) ? raw : null);
        try
        {
            if (Directory.Exists(data))
            {
                foreach (var file in Directory.EnumerateFiles(data, "*.*", SearchOption.AllDirectories)
                             .Where(IsArchive).Order(StringComparer.OrdinalIgnoreCase))
                {
                    // The path under data/ is the client's own, so it keys every format as the client does.
                    var relative = Path.GetRelativePath(data, file).Replace('\\', '/');
                    var reader = Archive.Open(file, relative);
                    store._archives.Add(reader);
                    var info = new FileInfo(file);
                    stamps.Add(new(relative, info.Length, info.LastWriteTimeUtc.Ticks));
                    if (file.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase))
                    {
                        store._bfzByPath[relative] = reader;
                        continue;
                    }
                    foreach (var entry in reader.Entries)
                        if (!store._packMembers.TryAdd(entry.Path, reader))
                            logger.LogWarning("clientdata: '{Path}' appears in more than one pack; the first one found ({Relative} is skipped).",
                                entry.Path, relative);
                }
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }
        logger.LogInformation("clientdata: {Archives} archive(s) with {Members} file(s) from '{Data}'; loose files from {Raw}, read first.",
            store._archives.Count, store.MemberCount, data, store._raw is null ? "nowhere (directory absent)" : $"'{raw}'");
        if (!string.IsNullOrWhiteSpace(compiledFile))
            store._compiled = CompiledDataFile.Open(compiledFile, stamps, store.LooseState, logger);
        return store;
    }

    /// <inheritdoc />
    public T Load<T>(ICompiledCatalog<T> catalog, string inputs, Func<IClientDataStore, T> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        return _compiled is { } compiled ? compiled.Load(this, catalog, inputs, compile) : compile(this);
    }

    /// <summary>Gets the compiled file of this store, when it keeps one.</summary>
    internal CompiledDataFile? CompiledFile => _compiled;

    /// <summary>The start is over: nothing parsed is kept any more, and compiled catalogs are written.</summary>
    internal void EndStartup()
    {
        ReleaseWorkbooks();
        if (_compiled is not { } compiled) return;
        compiled.Flush(atOnceFromNow: true);
        compiled.LogSummary();
    }

    // What a compiled catalog's validity rests on for one client path: the loose file that shadows the archives.
    private LooseStamp LooseState(string key)
    {
        if (Loose(key) is not { } loose) return LooseStamp.Absent;
        var info = new FileInfo(loose);
        return info.Exists ? new(info.Length, info.LastWriteTimeUtc.Ticks) : LooseStamp.Absent;
    }

    public bool Exists(string path)
    {
        var key = Normalize(path);
        return Loose(key) is not null || Holder(key) is not null;
    }

    public byte[] Read(string path)
    {
        var key = Normalize(path);
        if (Loose(key) is { } loose)
            return File.ReadAllBytes(loose);
        if (Holder(key) is { } reader)
            return reader.Read(key);
        throw new FileNotFoundException($"clientdata: no client file '{path}' in the archives or the loose files.", path);
    }

    public byte[] Read(string path, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        var key = Normalize(path);
        if (Loose(key) is { } loose)
        {
            using var input = File.OpenRead(loose);
            if (input.Length > maximumBytes) throw new InvalidDataException($"Client file '{path}' exceeds its byte limit.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }
        if (Holder(key) is { } reader)
        {
            // The index knows the size, so an oversized file is refused before anything is unpacked.
            if (reader.Length(key) > maximumBytes) throw new InvalidDataException($"Client file '{path}' exceeds its byte limit.");
            return reader.Read(key);
        }
        throw new FileNotFoundException($"clientdata: no client file '{path}' in the archives or the loose files.", path);
    }

    public long Length(string path)
    {
        var key = Normalize(path);
        if (Loose(key) is { } loose)
            return new FileInfo(loose).Length;
        if (Holder(key) is { } reader)
            return reader.Length(key);
        throw new FileNotFoundException($"clientdata: no client file '{path}' in the archives or the loose files.", path);
    }

    public Stream OpenRead(string path)
    {
        var key = Normalize(path);
        if (Loose(key) is { } loose)
            return new FileStream(loose, FileMode.Open, FileAccess.Read, FileShare.Read, LooseReadBufferBytes, FileOptions.SequentialScan);
        if (Holder(key) is { } reader)
            return reader.OpenRead(key);
        throw new FileNotFoundException($"clientdata: no client file '{path}' in the archives or the loose files.", path);
    }

    public Stream OpenRead(string path, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        // The file system or the archive index knows the size, so nothing oversized is opened or unpacked.
        if (Length(path) > maximumBytes) throw new InvalidDataException($"Client file '{path}' exceeds its byte limit.");
        return OpenRead(path);
    }

    public void Dispose()
    {
        ReleaseWorkbooks();
        _compiled?.Flush(atOnceFromNow: true);
        foreach (var archive in _archives)
            archive.Dispose();
        _archives.Clear();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(string path,
        IReadOnlyCollection<string> sheetNames,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> parse)
    {
        ArgumentNullException.ThrowIfNull(sheetNames);
        ArgumentNullException.ThrowIfNull(parse);
        Workbook? workbook;
        lock (_workbooksGate)
        {
            if (_workbooks is null)
                return CompiledOrParsed(path, sheetNames, parse);
            var key = Normalize(path);
            if (!_workbooks.TryGetValue(key, out workbook))
                _workbooks.Add(key, workbook = new Workbook());
        }
        // One parse per workbook at a time: a row that needs sheets another row is parsing waits for them instead of
        // reading the file again. A failed parse keeps nothing, so the next caller fails the same way.
        lock (workbook.Gate)
        {
            var missing = sheetNames.Where(name => !workbook.Sheets.ContainsKey(name)).Distinct(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                foreach (var (name, rows) in CompiledOrParsed(path, missing, parse))
                    workbook.Sheets[name] = rows;
            var result = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
            foreach (var name in sheetNames)
                if (workbook.Sheets.TryGetValue(name, out var rows))
                    result[name] = rows;
            return result;
        }
    }

    // The worksheets of the build's compiled client data where they are current; the rest are parsed in one pass over
    // the workbook and kept for the next start. A parse that fails keeps nothing and fails as it always did.
    private IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> CompiledOrParsed(string path,
        IReadOnlyCollection<string> sheetNames,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> parse)
    {
        if (_compiled is not { } compiled)
            return parse(sheetNames);
        var key = Normalize(path);
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        var missing = new List<(string Name, string Reason)>();
        foreach (var name in sheetNames.Distinct(StringComparer.Ordinal))
        {
            if (compiled.TryRead(PackedWorksheet.Catalog(key, name), "", out var rows, out var reason))
                result[name] = rows;
            else
                missing.Add((name, reason));
        }
        if (missing.Count == 0)
            return result;
        // The loose-file state is taken before the parse opens the file, so a file replaced meanwhile is noticed later.
        var file = compiled.FileState(key);
        var parsed = parse(missing.Select(sheet => sheet.Name).ToArray());
        foreach (var (name, reason) in missing)
        {
            if (!parsed.TryGetValue(name, out var rows)) continue;
            compiled.Put(PackedWorksheet.Catalog(key, name), "", rows, [file], reason);
            result[name] = rows;
        }
        // A parser may return more than it was asked for; what it returns is passed on as before.
        foreach (var (name, rows) in parsed)
            result.TryAdd(name, rows);
        return result;
    }

    // Rows start one at a time in profile order, so a build's readers come right after its store. When the next
    // build's store opens, the previous one's sharing has done its work; releasing it then keeps one build's parsed
    // workbooks at a time instead of every build's until the whole start converges. A late reader just parses again.
    private static readonly Lock CurrentGate = new();
    private static ClientDataStore? _current;

    /// <summary>Makes <paramref name="store"/> the one store that keeps shared workbooks, releasing the previous one.</summary>
    internal static void BecomeCurrent(ClientDataStore store)
    {
        ClientDataStore? previous;
        lock (CurrentGate)
        {
            previous = _current;
            _current = store;
        }
        if (!ReferenceEquals(previous, store))
        {
            previous?.ReleaseWorkbooks();
            // What the previous build compiled so far goes to its file now, so a start of many builds does not hold
            // every build's new catalogs until the end; a catalog it compiles later is written when startup ends.
            previous?._compiled?.Flush(atOnceFromNow: false);
        }
    }

    /// <summary>Drops every shared workbook and keeps none from now on.</summary>
    internal void ReleaseWorkbooks()
    {
        lock (_workbooksGate)
            _workbooks = null;
        lock (CurrentGate)
            if (ReferenceEquals(_current, this))
                _current = null;
    }

    /// <summary>Gets how many workbooks are kept; zero once released.</summary>
    internal int KeptWorkbooks
    {
        get { lock (_workbooksGate) return _workbooks?.Count ?? 0; }
    }

    private sealed class Workbook
    {
        public Lock Gate { get; } = new();
        public Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets { get; } = new(StringComparer.Ordinal);
    }

    // The exact spelling first; otherwise each path segment is matched ignoring case, so a case-sensitive file
    // system finds the client's file as Windows would. A path that would leave dataraw is never served.
    private string? Loose(string key)
    {
        if (_raw is null || key.Length == 0)
            return null;
        var segments = key.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            return null;
        var exact = Path.Combine([_raw, .. segments]);
        if (File.Exists(exact))
            return exact;
        // Most files are in the archives, so this miss is the common case. The operating system is asked for the
        // one name, ignoring case, instead of for every name in the folder: a build's dataraw has thousands.
        var current = _raw;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            string? match = null;
            foreach (var entry in last
                         ? Directory.EnumerateFiles(current, segments[i], AnyCase)
                         : Directory.EnumerateDirectories(current, segments[i], AnyCase))
            {
                // The name is also a pattern to the file system; only an exact name, case aside, is this file.
                if (!string.Equals(Path.GetFileName(entry), segments[i], StringComparison.OrdinalIgnoreCase))
                    continue;
                match = entry;
                break;
            }
            if (match is null)
                return null;
            current = match;
        }
        return current;
    }

    private static readonly EnumerationOptions AnyCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        MatchType = MatchType.Simple,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// <summary>The read-ahead of a loose file opened as a stream; a parser reads it front to back.</summary>
    private const int LooseReadBufferBytes = 64 * 1024;

    private IArchiveReader? Holder(string key)
    {
        if (_packMembers.TryGetValue(key, out var pack))
            return pack;
        // The nearest folder with an archive of its own name decides; a file it lacks is not looked for higher up.
        var folders = key.Split('/')[..^1];
        for (var depth = folders.Length; depth > 0; depth--)
        {
            var candidate = string.Join('/', folders[..depth]) + "/" + folders[depth - 1] + ".bfz";
            if (_bfzByPath.TryGetValue(candidate, out var bfz))
                return bfz.Contains(key) ? bfz : null;
        }
        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsArchive(string file) =>
        file.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase);
}
