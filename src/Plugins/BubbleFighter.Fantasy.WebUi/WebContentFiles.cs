using System.Buffers;
using System.Collections.Frozen;
using System.Reflection;
using BubbleFighter.Fantasy.Contracts.Admin;

namespace BubbleFighter.Fantasy.WebUi;

/// <summary>
/// An immutable set of static files that implements <see cref="IAdminWebContent"/>.
/// Paths are validated here, once, so no consumer has to repeat traversal checks: anything that
/// could leave the content root is simply not found.
/// </summary>
/// <remarks>
/// Files loaded with <see cref="FromAssembly"/> are served from the loaded assembly itself and are never copied to
/// the managed heap. The operating system backs those pages with the assembly file, so it can drop them when memory
/// is short and read them again on the next request.
/// </remarks>
public sealed class WebContentFiles : IAdminWebContent
{
    /// <summary>Prefix of the embedded resource names the project gives the built client files.</summary>
    private const string ResourcePrefix = "wwwroot/";

    /// <summary>Media types by extension. Unlisted extensions fall back to <c>application/octet-stream</c>.</summary>
    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".wav"] = "audio/wav",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, ReadOnlyMemory<byte>> _files;

    /// <summary>Creates the set from files keyed by their root-relative path.</summary>
    /// <param name="files">The files; every key must be a valid relative path with <c>/</c> separators.</param>
    /// <exception cref="ArgumentException">Thrown when a key is not a valid relative path.</exception>
    public WebContentFiles(IReadOnlyDictionary<string, byte[]> files)
        : this(Wrap(files))
    {
    }

    private WebContentFiles(Dictionary<string, ReadOnlyMemory<byte>> files)
    {
        foreach (var path in files.Keys)
            if (!IsSafeRelativePath(path))
                throw new ArgumentException($"'{path}' is not a valid relative content path.", nameof(files));
        _files = files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Gets the number of files in the set.</summary>
    public int Count => _files.Count;

    /// <summary>
    /// Loads the files the project embedded from <c>wwwroot</c>. A build with no embedded files is
    /// an error: serving an empty interface would hide that the client was never built.
    /// </summary>
    /// <param name="assembly">The assembly that carries the embedded files.</param>
    /// <returns>The files.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the assembly embeds no <c>wwwroot</c> files or no <c>index.html</c>.</exception>
    public static WebContentFiles FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
            // The runtime hands out a view of the loaded assembly. That view is kept, not disposed: it is what
            // keeps the assembly, and so the bytes, alive for as long as the files are served.
            files[normalized[ResourcePrefix.Length..]] = stream is UnmanagedMemoryStream mapped
                ? new MappedResource(mapped).Memory
                : ReadAll(stream);
        }
        if (!files.ContainsKey("index.html"))
            throw new InvalidOperationException(
                "The web interface is not embedded: no wwwroot/index.html in the assembly. Build the client (npm ci && npm run build in the client folder) before building this plugin.");
        return new WebContentFiles(files);
    }

    /// <inheritdoc />
    public bool TryGetFile(string relativePath, out ReadOnlyMemory<byte> content, out string contentType)
    {
        content = default;
        contentType = "";
        if (relativePath is null || !IsSafeRelativePath(relativePath) || !_files.TryGetValue(relativePath, out var bytes))
            return false;
        content = bytes;
        contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(relativePath), "application/octet-stream");
        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> stays inside the root: non-empty, <c>/</c>-separated, no
    /// leading slash, no empty or dot segments, and none of the characters that change meaning on
    /// a file system (backslash, drive colon, control characters including NUL).
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path[0] == '/')
            return false;
        foreach (var c in path)
            if (c is '\\' or ':' or '%' || char.IsControl(c))
                return false;
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or "..")
                return false;
        return true;
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> Wrap(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var wrapped = new Dictionary<string, ReadOnlyMemory<byte>>(files.Count, StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
            wrapped[path] = bytes;
        return wrapped;
    }

    /// <summary>Copies a resource that the runtime could not expose as a view of the loaded assembly.</summary>
    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Exposes one embedded resource as memory without copying it. The bytes are part of the loaded assembly, do
    /// not move and are read-only; the stream is held because it is what keeps that assembly loaded.
    /// </summary>
    private sealed unsafe class MappedResource : MemoryManager<byte>
    {
        private readonly UnmanagedMemoryStream _owner;
        private readonly byte* _start;
        private readonly int _length;

        internal MappedResource(UnmanagedMemoryStream stream)
        {
            _owner = stream;
            _length = checked((int)stream.Length);
            stream.Position = 0;
            _start = stream.PositionPointer;
        }

        public override Span<byte> GetSpan() => new(_start, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)_length, nameof(elementIndex));
            GC.KeepAlive(_owner);
            return new MemoryHandle(_start + elementIndex);
        }

        // The bytes never move, so a pin holds nothing that must be released.
        public override void Unpin()
        {
        }

        // The view lives exactly as long as the assembly it points into; there is nothing to free.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
