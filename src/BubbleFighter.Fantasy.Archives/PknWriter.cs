using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>Writes PKN packs the client's reader accepts.</summary>
public static class PknWriter
{
    /// <summary>
    /// Writes a pack. Members are stored zlib-compressed and fully encrypted (flags 3), the
    /// form the client's own packs use, unless <paramref name="compress"/> or
    /// <paramref name="encrypt"/> turn a layer off. Space the format does not use is filled with
    /// random bytes, as in the client's packs.
    /// </summary>
    /// <param name="output">The destination; written from its current position, which is the pack's offset 0.</param>
    /// <param name="uri">The pack URI the client will derive keys from, for example <c>packs/config_00000.pkn</c>.</param>
    /// <param name="members">Member paths (with <c>/</c> separators) and contents, in index order.</param>
    /// <param name="compress">Whether to zlib-compress members.</param>
    /// <param name="encrypt">Whether to encrypt members.</param>
    public static void Write(Stream output, string uri, IEnumerable<(string Path, byte[] Data)> members,
        bool compress = true, bool encrypt = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var flags = (compress ? PknFormat.FlagZlib : 0) | (encrypt ? PknFormat.FlagEncrypted : 0);

        var stored = new List<(string Path, byte[] Payload, uint Size, byte[] Md5)>();
        foreach (var (path, data) in members)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 0x4000)
                throw new ArgumentException($"Member path '{path}' is empty or too long.", nameof(members));
            var md5 = MD5.HashData(data);
            var payload = compress ? Deflate(data) : data.ToArray();
            if (encrypt)
            {
                // The cipher works on whole words; the stored size covers the padded length.
                Array.Resize(ref payload, (payload.Length + 3) & ~3);
                new Snow2Cipher(PknFormat.MemberKey(path, md5)).Encrypt(payload);
            }
            stored.Add((path, payload, (uint)data.Length, md5));
        }

        var index = new MemoryStream();
        uint block = 0;
        var blocks = new List<uint>(stored.Count);
        foreach (var (path, payload, size, md5) in stored)
        {
            blocks.Add(block);
            var fixedPart = new byte[20];
            var storedSize = (uint)payload.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(0), unchecked(flags + block + size + storedSize + (uint)md5.Sum(b => b)));
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(4), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(8), block);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(12), size);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(16), storedSize);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)path.Length);
            index.Write(length);
            index.Write(Encoding.Unicode.GetBytes(path));
            index.Write(fixedPart);
            index.Write(md5);
            block += (uint)((payload.Length + PknFormat.BlockSize - 1) / PknFormat.BlockSize);
        }

        var headerOffset = PknFormat.HeaderOffset(uri);
        var indexOffset = PknFormat.IndexOffset(uri);
        var indexBytes = index.ToArray();
        Array.Resize(ref indexBytes, (indexBytes.Length + 3) & ~3);
        var dataOffset = (indexOffset + indexBytes.Length + PknFormat.BlockSize - 1) / PknFormat.BlockSize * PknFormat.BlockSize;
        var total = dataOffset + (long)block * PknFormat.BlockSize;
        if (total > int.MaxValue)
            throw new NotSupportedException("Packs larger than 2 GiB are not supported.");

        var image = new byte[total];
        RandomNumberGenerator.Fill(image);
        var header = new byte[12];
        var count = (uint)stored.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(header, unchecked(count + PknFormat.Version));
        header[4] = PknFormat.Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), count);
        new Snow2Cipher(PknFormat.HeaderKey(uri)).Encrypt(header);
        header.CopyTo(image, headerOffset);
        new Snow2Cipher(PknFormat.IndexKey(uri)).Encrypt(indexBytes);
        indexBytes.CopyTo(image, indexOffset);
        for (var i = 0; i < stored.Count; i++)
            stored[i].Payload.CopyTo(image, dataOffset + (long)blocks[i] * PknFormat.BlockSize);
        output.Write(image);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }
}
