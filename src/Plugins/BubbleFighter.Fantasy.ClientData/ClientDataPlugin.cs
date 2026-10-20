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
[Plugin("clientdata", Description = "Client data files served from PKN/BFZ archives, with a loose-file override directory.",
    Provides = [typeof(IClientDataStore)])]
public sealed class ClientDataPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ClientDataOptions>();
        var store = ClientDataStore.Open(options.DataDirectory, options.RawDirectory, context.Logger);
        context.Effect(store);
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
internal sealed class ClientDataStore : IClientDataStore, IDisposable
{
    private readonly string? _raw;
    private readonly List<IArchiveReader> _archives = [];
    private readonly Dictionary<string, IArchiveReader> _packMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IArchiveReader> _bfzByPath = new(StringComparer.OrdinalIgnoreCase);

    // Loose files are looked up when asked, not indexed: a build's dataraw holds thousands of files and a
    // small server should not keep every path in memory.
    private ClientDataStore(string? raw) => _raw = raw;

    internal int ArchiveCount => _archives.Count;

    internal int MemberCount => _packMembers.Count + _bfzByPath.Values.Sum(reader => reader.Entries.Count);

    internal static ClientDataStore Open(string dataDirectory, string rawDirectory, ILogger logger)
    {
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
        return store;
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

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
        _archives.Clear();
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
        var current = _raw;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            var match = (last ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current))
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segments[i], StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            current = match;
        }
        return current;
    }

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
