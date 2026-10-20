using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>
/// Reads a BFZ archive: the zip central directory, then per member the traditional-cipher
/// header, stored or deflated data and a CRC-32 check. Only the zip features the client's
/// archives use are supported (no zip64, no multi-disk).
/// </summary>
public sealed class BfzArchive : IArchiveReader
{
    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndOfCentralSignature = 0x06054B50;

    private readonly Stream _stream;
    private readonly byte[] _password;
    private readonly Dictionary<string, Member> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ArchiveEntry> _entries = [];

    private sealed record Member(string Path, ushort Flags, ushort Method, uint Crc, uint CompressedSize,
        uint Size, uint LocalHeaderOffset, ushort Time);

    private BfzArchive(Stream stream, string clientRelativePath)
    {
        _stream = stream;
        RelativePath = clientRelativePath.Replace('\\', '/');
        _password = BfzFormat.Password(RelativePath);
        var (count, directoryOffset) = FindCentralDirectory();
        stream.Position = directoryOffset;
        Span<byte> fixedPart = stackalloc byte[46];
        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(fixedPart);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != CentralHeaderSignature)
                throw new InvalidDataException($"'{RelativePath}' has a damaged central directory at entry {i}.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[10..]);
            var time = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[12..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[16..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[20..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[32..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[42..]);
            var name = new byte[nameLength];
            stream.ReadExactly(name);
            stream.Position += extraLength + commentLength;
            var path = ((flags & 0x800) != 0 ? Encoding.UTF8 : BfzFormat.NameEncoding).GetString(name);
            if (path.EndsWith('/'))
                continue;
            var member = new Member(path, flags, method, crc, compressed, size, localOffset, time);
            _byPath[Archive.NormalizeMemberPath(path)] = member;
            _entries.Add(new ArchiveEntry(Archive.NormalizeMemberPath(path), size));
        }
    }

    /// <summary>Gets the client-relative path the password was derived from.</summary>
    public string RelativePath { get; }

    /// <inheritdoc />
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    /// <summary>
    /// Opens an archive file. <paramref name="clientRelativePath"/> is its path relative to the
    /// client root (for example <c>Dds/Maps/rel_Desert04_Pharaoh/rel_Desert04_Pharaoh.bfz</c>);
    /// when omitted the archive is assumed to sit in a folder of its own name directly under the
    /// root.
    /// </summary>
    /// <param name="filePath">The archive file.</param>
    /// <param name="clientRelativePath">The archive's path relative to the client root, or <see langword="null"/>.</param>
    public static BfzArchive Open(string filePath, string? clientRelativePath = null)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new BfzArchive(stream, clientRelativePath ?? BfzFormat.InferRelativePath(filePath));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads an archive from a seekable stream, which the archive then owns.</summary>
    /// <param name="stream">The archive bytes.</param>
    /// <param name="clientRelativePath">The archive's path relative to the client root.</param>
    public static BfzArchive Open(Stream stream, string clientRelativePath) => new(stream, clientRelativePath);

    /// <inheritdoc />
    public bool Contains(string path) => _byPath.ContainsKey(Archive.NormalizeMemberPath(path));

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        if (!_byPath.TryGetValue(Archive.NormalizeMemberPath(path), out var member))
            throw new FileNotFoundException($"'{RelativePath}' has no member '{path}'.", path);

        byte[] stored;
        lock (_stream)
        {
            _stream.Position = member.LocalHeaderOffset;
            Span<byte> local = stackalloc byte[30];
            _stream.ReadExactly(local);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeaderSignature)
                throw new InvalidDataException($"'{RelativePath}' member '{member.Path}' has no local header.");
            _stream.Position += BinaryPrimitives.ReadUInt16LittleEndian(local[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
            stored = new byte[member.CompressedSize];
            _stream.ReadExactly(stored);
        }

        var body = stored.AsSpan();
        if ((member.Flags & 1) != 0)
        {
            new ZipCrypto(_password).Decrypt(body);
            // The last header byte repeats the CRC's high byte (or the time's, with bit 3 set).
            var check = (member.Flags & 8) != 0 ? (byte)(member.Time >> 8) : (byte)(member.Crc >> 24);
            if (body.Length < 12 || body[11] != check)
                throw new InvalidDataException($"'{RelativePath}' member '{member.Path}' does not decrypt with this archive's password.");
            body = body[12..];
        }

        byte[] data;
        switch (member.Method)
        {
            case 0:
                data = body.ToArray();
                break;
            case 8:
                data = new byte[member.Size];
                using (var inflater = new DeflateStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress))
                    inflater.ReadExactly(data);
                break;
            default:
                throw new NotSupportedException($"'{RelativePath}' member '{member.Path}' uses zip method {member.Method}.");
        }
        if (ZipCrypto.Crc32(data) != member.Crc)
            throw new InvalidDataException($"'{RelativePath}' member '{member.Path}' fails its CRC check.");
        return data;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    private (int Count, long Offset) FindCentralDirectory()
    {
        var tailLength = (int)Math.Min(_stream.Length, 22 + ushort.MaxValue);
        var tail = new byte[tailLength];
        _stream.Position = _stream.Length - tailLength;
        _stream.ReadExactly(tail);
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndOfCentralSignature)
                continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            return (count, offset);
        }
        throw new InvalidDataException($"'{RelativePath}' is not a zip archive.");
    }

    /// <summary>
    /// Writes a BFZ archive: every member deflated and encrypted with the password of
    /// <paramref name="clientRelativePath"/>, as the client's archives are.
    /// </summary>
    /// <param name="output">The destination; written from its current position.</param>
    /// <param name="clientRelativePath">The archive's path relative to the client root, which keys the password.</param>
    /// <param name="members">Member paths (with <c>/</c> separators) and contents.</param>
    public static void Write(Stream output, string clientRelativePath, IEnumerable<(string Path, byte[] Data)> members)
    {
        ArgumentNullException.ThrowIfNull(output);
        var password = BfzFormat.Password(clientRelativePath);
        var start = output.Position;
        var central = new MemoryStream();
        var count = 0;
        foreach (var (path, data) in members)
        {
            var name = BfzFormat.NameEncoding.GetBytes(path.Replace('\\', '/'));
            var crc = ZipCrypto.Crc32(data);
            byte[] deflated;
            using (var buffer = new MemoryStream())
            {
                using (var deflater = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    deflater.Write(data);
                deflated = buffer.ToArray();
            }
            var body = new byte[12 + deflated.Length];
            RandomNumberGenerator.Fill(body.AsSpan(0, 11));
            body[11] = (byte)(crc >> 24);
            deflated.CopyTo(body, 12);
            new ZipCrypto(password).Encrypt(body);

            var localOffset = (uint)(output.Position - start);
            WriteHeader(output, LocalHeaderSignature, central: false, crc, (uint)body.Length, (uint)data.Length, name, 0);
            output.Write(body);
            WriteHeader(central, CentralHeaderSignature, central: true, crc, (uint)body.Length, (uint)data.Length, name, localOffset);
            count++;
        }

        var directoryOffset = (uint)(output.Position - start);
        central.Position = 0;
        central.CopyTo(output);
        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndOfCentralSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], checked((ushort)count));
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], checked((ushort)count));
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], directoryOffset);
        output.Write(end);
    }

    private static void WriteHeader(Stream output, uint signature, bool central, uint crc, uint compressed, uint size,
        byte[] name, uint localOffset)
    {
        var fixedPart = new byte[central ? 46 : 30];
        var span = fixedPart.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, signature);
        var o = 4;
        if (central)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[o..], 20); // made by
            o += 2;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(span[o..], 20); // needed to extract
        BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 2)..], 1); // encrypted
        BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 4)..], 8); // deflate
        BinaryPrimitives.WriteUInt32LittleEndian(span[(o + 10)..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(o + 14)..], compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(o + 18)..], size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 22)..], (ushort)name.Length);
        if (central)
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], localOffset);
        output.Write(fixedPart);
        output.Write(name);
    }
}
