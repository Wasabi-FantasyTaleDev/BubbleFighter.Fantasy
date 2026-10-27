using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>One member as the PKN index describes it.</summary>
/// <param name="Path">The member path exactly as stored (it keys the member cipher).</param>
/// <param name="Flags">Storage flags; see <see cref="PknFormat"/>.</param>
/// <param name="Block">The first 1024-byte block, counted from the start of member data.</param>
/// <param name="Size">The unpacked size.</param>
/// <param name="StoredSize">The stored size.</param>
/// <param name="Md5">The MD5 of the unpacked bytes.</param>
public sealed record PknEntry(string Path, uint Flags, uint Block, uint Size, uint StoredSize, byte[] Md5);

/// <summary>
/// Reads a PKN pack. The index is decoded on open; members are read on demand and their MD5
/// is checked.
/// </summary>
public sealed class PknArchive : IArchiveReader
{
    // kr-1256 sub_1126430 caps a prefixed wide string at 0x4000 code units.
    private const int MaxPathLength = 0x4000;

    private readonly Stream _stream;
    private readonly Dictionary<string, PknEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ArchiveEntry> _entries = [];

    private PknArchive(Stream stream, string uri)
    {
        _stream = stream;
        Uri = uri;
        var header = ReadDecrypted(PknFormat.HeaderOffset(uri), 12, PknFormat.HeaderKey(uri));
        var check = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = header[4];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        if (version != PknFormat.Version || check != unchecked(count + version))
            throw new InvalidDataException(
                $"'{uri}' does not decode as a version-{PknFormat.Version} pack; the pack URI may be wrong (it must be the pack's path relative to the client root).");

        var reader = new CipherReader(stream, PknFormat.IndexOffset(uri), PknFormat.IndexKey(uri));
        var members = new List<PknEntry>((int)Math.Min(count, 65536));
        for (var i = 0u; i < count; i++)
        {
            var length = reader.ReadUInt32();
            if (length > MaxPathLength)
                throw new InvalidDataException($"'{uri}' index entry {i} has a {length}-character path.");
            var path = Encoding.Unicode.GetString(reader.Read((int)length * 2));
            var sum = reader.ReadUInt32();
            var flags = reader.ReadUInt32();
            var block = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var stored = reader.ReadUInt32();
            var md5 = reader.Read(16);
            var expected = unchecked(flags + block + size + stored + (uint)md5.Sum(b => b));
            if (sum != expected)
                throw new InvalidDataException($"'{uri}' index entry {i} ('{path}') fails its checksum.");
            var entry = new PknEntry(path, flags, block, size, stored, md5);
            members.Add(entry);
            _byPath[Archive.NormalizeMemberPath(path)] = entry;
            _entries.Add(new ArchiveEntry(Archive.NormalizeMemberPath(path), size));
        }
        Members = members;
        DataOffset = (reader.Position + PknFormat.BlockSize - 1) / PknFormat.BlockSize * PknFormat.BlockSize;
    }

    /// <summary>Gets the pack URI the keys were derived from.</summary>
    public string Uri { get; }

    /// <summary>Gets the index entries in stored order.</summary>
    public IReadOnlyList<PknEntry> Members { get; }

    /// <summary>Gets the file offset member data starts at.</summary>
    public long DataOffset { get; }

    /// <inheritdoc />
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    /// <summary>
    /// Opens a pack file. <paramref name="clientRelativePath"/> is the pack's path relative to
    /// the client root (for example <c>Packs/Config_00000.pkn</c>); when omitted the pack is
    /// assumed to sit in <c>Packs/</c>.
    /// </summary>
    /// <param name="filePath">The pack file.</param>
    /// <param name="clientRelativePath">The pack's path relative to the client root, or <see langword="null"/>.</param>
    public static PknArchive Open(string filePath, string? clientRelativePath = null)
    {
        var uri = clientRelativePath is null
            ? PknFormat.UriForPack(Path.GetFileName(filePath))
            : clientRelativePath.Replace('\\', '/').ToLowerInvariant();
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new PknArchive(stream, uri);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads a pack from a seekable stream, which the archive then owns.</summary>
    /// <param name="stream">The pack bytes.</param>
    /// <param name="uri">The pack URI, lower-case with <c>/</c> separators.</param>
    public static PknArchive Open(Stream stream, string uri) => new(stream, uri);

    /// <inheritdoc />
    public bool Contains(string path) => _byPath.ContainsKey(Archive.NormalizeMemberPath(path));

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        if (!_byPath.TryGetValue(Archive.NormalizeMemberPath(path), out var entry))
            throw new FileNotFoundException($"'{Uri}' has no member '{path}'.", path);
        return Read(entry);
    }

    /// <summary>Reads, decrypts, unpacks and verifies one member.</summary>
    /// <param name="entry">A member of this pack.</param>
    public byte[] Read(PknEntry entry)
    {
        var padded = (int)((entry.StoredSize + 3) & ~3u);
        var payload = new byte[padded];
        lock (_stream)
        {
            _stream.Position = DataOffset + (long)entry.Block * PknFormat.BlockSize;
            _stream.ReadExactly(payload);
        }
        if ((entry.Flags & PknFormat.FlagHeadEncrypted) != 0)
            new Snow2Cipher(PknFormat.MemberKey(entry.Path, entry.Md5)).Decrypt(payload.AsSpan(0, Math.Min(PknFormat.BlockSize, padded)));
        if ((entry.Flags & PknFormat.FlagEncrypted) != 0)
            new Snow2Cipher(PknFormat.MemberKey(entry.Path, entry.Md5)).Decrypt(payload);

        byte[] data;
        if ((entry.Flags & PknFormat.FlagZlib) != 0)
        {
            using var inflater = new ZLibStream(new MemoryStream(payload, 0, (int)entry.StoredSize), CompressionMode.Decompress);
            data = new byte[entry.Size];
            inflater.ReadExactly(data);
        }
        else
        {
            data = payload.AsSpan(0, (int)entry.Size).ToArray();
        }
        if (!MD5.HashData(data).AsSpan().SequenceEqual(entry.Md5))
            throw new InvalidDataException($"'{Uri}' member '{entry.Path}' fails its MD5 check.");
        return data;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    private byte[] ReadDecrypted(long offset, int length, byte[] key)
    {
        var buffer = new byte[length];
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
        new Snow2Cipher(key).Decrypt(buffer);
        return buffer;
    }

    // The client's cipher stream reads whole words from the file and decrypts them in order.
    private sealed class CipherReader(Stream stream, long offset, byte[] key)
    {
        private readonly Snow2Cipher _cipher = new(key);
        private readonly byte[] _word = new byte[4];
        private int _available;
        private long _next = offset;

        public long Position { get; private set; } = offset;

        public byte[] Read(int count)
        {
            var result = new byte[count];
            for (var i = 0; i < count; i++)
            {
                if (_available == 0)
                {
                    stream.Position = _next;
                    stream.ReadExactly(_word);
                    _next += 4;
                    _cipher.Decrypt(_word);
                    _available = 4;
                }
                result[i] = _word[4 - _available--];
            }
            Position += count;
            return result;
        }

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
    }
}
