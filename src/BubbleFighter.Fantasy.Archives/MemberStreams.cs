using System.Buffers;
using System.Security.Cryptography;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>Base of the read-only, forward-only streams a member is read through.</summary>
internal abstract class ForwardReadStream : Stream
{
    public sealed override bool CanRead => true;
    public sealed override bool CanSeek => false;
    public sealed override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public sealed override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public sealed override void Flush() { }
    public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public sealed override void SetLength(long value) => throw new NotSupportedException();
    public sealed override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// One byte range of an archive file. The archive's stream is shared by every reader, so each read takes the
/// archive's lock, seeks and reads; nothing is held between reads.
/// </summary>
internal sealed class ArchiveSliceStream(Stream archive, long offset, long length) : ForwardReadStream
{
    private long _read;

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - _read);
        if (count <= 0)
            return 0;
        int got;
        lock (archive)
        {
            archive.Position = offset + _read;
            got = archive.Read(buffer[..count]);
        }
        if (got == 0)
            throw new EndOfStreamException("The archive ends inside a member's stored data.");
        _read += got;
        return got;
    }
}

/// <summary>Limits a stream to its first bytes.</summary>
internal sealed class PrefixStream(Stream inner, long length) : ForwardReadStream
{
    private long _left = length;

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _left);
        if (count <= 0)
            return 0;
        var got = inner.Read(buffer[..count]);
        _left -= got;
        return got;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>What a member's unpacked bytes are checked against once all of them have been read.</summary>
internal abstract class MemberChecksum : IDisposable
{
    /// <summary>Adds the next unpacked bytes.</summary>
    public abstract void Append(ReadOnlySpan<byte> data);

    /// <summary>Returns whether the bytes added so far are the member the index describes.</summary>
    public abstract bool Matches();

    /// <summary>Gets the name of the check for the error message, for example <c>CRC</c>.</summary>
    public abstract string Name { get; }

    public virtual void Dispose() { }
}

internal sealed class Crc32Checksum(uint expected) : MemberChecksum
{
    private uint _state = ZipCrypto.Crc32Seed;

    public override string Name => "CRC";
    public override void Append(ReadOnlySpan<byte> data) => _state = ZipCrypto.Crc32Append(_state, data);
    public override bool Matches() => ~_state == expected;
}

internal sealed class Md5Checksum(byte[] expected) : MemberChecksum
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

    public override string Name => "MD5";
    public override void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    public override bool Matches()
    {
        Span<byte> actual = stackalloc byte[16];
        return _hash.TryGetHashAndReset(actual, out var written) && actual[..written].SequenceEqual(expected);
    }

    public override void Dispose() => _hash.Dispose();
}

/// <summary>
/// The unpacked bytes of one member, checked as they pass. The read that delivers the last byte verifies the
/// member's length and checksum and throws <see cref="InvalidDataException"/> instead of returning when they do
/// not match, so a caller that reads to the end never accepts damaged data. A caller that stops early has the rest
/// read and checked when it disposes the stream, unless a read already failed.
/// </summary>
internal sealed class VerifiedMemberStream(Stream unpacked, long length, MemberChecksum checksum, string description)
    : ForwardReadStream
{
    private const int DrainBufferBytes = 64 * 1024;

    private long _read;
    private bool _verified;
    private bool _failed;
    private bool _disposed;

    public override long Length => length;

    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(Span<byte> buffer)
    {
        if (_verified || buffer.IsEmpty)
            return 0;
        try
        {
            // One byte beyond the declared size is asked for at the end, which is how surplus data is noticed.
            var got = unpacked.Read(buffer);
            if (got == 0)
            {
                if (_read != length)
                    throw new InvalidDataException($"{description} ends after {_read} of its {length} bytes.");
                Verify();
                return 0;
            }
            _read += got;
            if (_read > length)
                throw new InvalidDataException($"{description} expands beyond its declared size of {length} bytes.");
            checksum.Append(buffer[..got]);
            if (_read == length)
            {
                Span<byte> surplus = stackalloc byte[1];
                if (unpacked.Read(surplus) != 0)
                    throw new InvalidDataException($"{description} expands beyond its declared size of {length} bytes.");
                Verify();
            }
            return got;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void Verify()
    {
        if (!checksum.Matches())
            throw new InvalidDataException($"{description} fails its {checksum.Name} check.");
        _verified = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;
        try
        {
            if (!_verified && !_failed)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(DrainBufferBytes);
                try
                {
                    while (Read(buffer) != 0)
                    {
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally
        {
            checksum.Dispose();
            unpacked.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Decrypts a BFZ member's stored bytes with the traditional zip cipher as they are read.</summary>
internal sealed class ZipCryptoStream(Stream stored, ZipCrypto cipher) : ForwardReadStream
{
    public override int Read(Span<byte> buffer)
    {
        var got = stored.Read(buffer);
        cipher.Decrypt(buffer[..got]);
        return got;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            stored.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Decrypts a PKN member's stored bytes as they are read. The stored data is padded to whole cipher words; a member
/// may have its first block encrypted, all of it, or both, each with a cipher started from the member's key.
/// </summary>
internal sealed class PknStoredStream : ForwardReadStream
{
    // A multiple of the cipher's word and of the pack's block, so the head block always lies in the first fill.
    private const int ChunkBytes = 64 * PknFormat.BlockSize;

    private readonly Stream _padded;
    private readonly Snow2Cipher? _head;
    private readonly Snow2Cipher? _whole;
    private readonly byte[] _buffer;
    private readonly int _chunk;
    private long _left;
    private int _start;
    private int _end;
    private bool _first = true;
    private bool _disposed;

    public PknStoredStream(Stream padded, long paddedLength, PknEntry entry)
    {
        _padded = padded;
        _left = paddedLength;
        if ((entry.Flags & PknFormat.FlagHeadEncrypted) != 0)
            _head = new Snow2Cipher(PknFormat.MemberKey(entry.Path, entry.Md5));
        if ((entry.Flags & PknFormat.FlagEncrypted) != 0)
            _whole = new Snow2Cipher(PknFormat.MemberKey(entry.Path, entry.Md5));
        // A small member is read in one fill and does not need the whole chunk.
        _chunk = (int)Math.Min(ChunkBytes, paddedLength);
        _buffer = ArrayPool<byte>.Shared.Rent(_chunk);
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_start == _end && !Fill())
            return 0;
        var count = Math.Min(buffer.Length, _end - _start);
        _buffer.AsSpan(_start, count).CopyTo(buffer);
        _start += count;
        return count;
    }

    private bool Fill()
    {
        var count = (int)Math.Min(_chunk, _left);
        if (count <= 0)
            return false;
        var chunk = _buffer.AsSpan(0, count);
        _padded.ReadExactly(chunk);
        _left -= count;
        if (_first)
        {
            _first = false;
            _head?.Decrypt(chunk[..Math.Min(PknFormat.BlockSize, count)]);
        }
        _whole?.Decrypt(chunk);
        _start = 0;
        _end = count;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            _padded.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Member reading shared by both PKN index layouts, whose stored data has one format.</summary>
internal static class PknMember
{
    /// <summary>Opens the unpacked, verified bytes of one member.</summary>
    /// <param name="archive">The pack's stream, which is also the lock every reader takes.</param>
    /// <param name="dataOffset">The file offset member data starts at.</param>
    /// <param name="entry">The member.</param>
    /// <param name="uri">The pack URI, for messages.</param>
    public static Stream Open(Stream archive, long dataOffset, PknEntry entry, string uri)
    {
        var paddedLength = ((long)entry.StoredSize + 3) & ~3L;
        var zlib = (entry.Flags & PknFormat.FlagZlib) != 0;
        if (!zlib && entry.Size > entry.StoredSize)
            throw new InvalidDataException($"'{uri}' member '{entry.Path}' is larger than its stored data.");
        Stream stored = new PknStoredStream(
            new ArchiveSliceStream(archive, dataOffset + (long)entry.Block * PknFormat.BlockSize, paddedLength), paddedLength, entry);
        // The padding is not part of the member; an unpacked member is the first bytes of what is stored.
        stored = new PrefixStream(stored, zlib ? entry.StoredSize : entry.Size);
        if (zlib)
            stored = new System.IO.Compression.ZLibStream(stored, System.IO.Compression.CompressionMode.Decompress);
        return new VerifiedMemberStream(stored, entry.Size, new Md5Checksum(entry.Md5), $"'{uri}' member '{entry.Path}'");
    }

    /// <summary>Reads one member whole through <see cref="Open"/>.</summary>
    public static byte[] Read(Stream archive, long dataOffset, PknEntry entry, string uri)
    {
        var data = new byte[entry.Size];
        using var stream = Open(archive, dataOffset, entry, uri);
        stream.ReadExactly(data);
        return data;
    }
}
