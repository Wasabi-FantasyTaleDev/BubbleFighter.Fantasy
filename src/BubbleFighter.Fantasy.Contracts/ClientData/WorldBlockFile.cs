using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BubbleFighter.Fantasy.Contracts.ClientData;

/// <summary>A named helper transform read from a map world-block resource.</summary>
/// <param name="Name">The original byte-string name, preserved through a one-byte Latin-1 mapping.</param>
/// <param name="Position">The helper's three stored position coordinates.</param>
/// <param name="Rotation">The helper's three stored rotation values.</param>
/// <param name="Offset">The byte offset of the helper's counted-name prefix in the resource.</param>
/// <param name="Properties">The original property byte string, preserved through a one-byte Latin-1 mapping.</param>
public sealed record WorldBlockHelper(string Name, Vector3 Position, Vector3 Rotation, int Offset, string Properties);

/// <summary>
/// Reads bounded helper transforms from version-2 world-block resources such as Maps/*/track.wb.
/// Mesh, material and animation records are traversed structurally; callers interpret helper names and properties.
/// </summary>
public static class WorldBlockFile
{
    /// <summary>Reads a client resource and extracts its helper records with the supplied byte and record limits.</summary>
    /// <param name="store">The owning build's client-data service.</param>
    /// <param name="path">The resource's relative client path.</param>
    /// <param name="maximumBytes">The inclusive resource byte limit.</param>
    /// <param name="maximumHelpers">The inclusive extracted helper-count limit.</param>
    /// <returns>Helper names, transforms, properties and source offsets in resource order.</returns>
    public static IReadOnlyList<WorldBlockHelper> LoadHelpers(IClientDataStore store, string path,
        int maximumBytes, int maximumHelpers) => ReadHelpers(store.Read(path, maximumBytes), maximumBytes, maximumHelpers);

    /// <summary>
    /// Walks complete world-block records to the end of the resource. Rejects unsupported versions, excessive
    /// sizes or helper counts, negative or truncated collections and non-finite helper transforms.
    /// </summary>
    /// <param name="bytes">The uncompressed world-block resource.</param>
    /// <param name="maximumBytes">The inclusive resource byte limit.</param>
    /// <param name="maximumHelpers">The inclusive extracted helper-count limit.</param>
    /// <returns>Helper records without filtering by name or changing their coordinates.</returns>
    public static IReadOnlyList<WorldBlockHelper> ReadHelpers(ReadOnlySpan<byte> bytes, int maximumBytes, int maximumHelpers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHelpers, 1);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("World-block resource exceeds its byte limit.");
        var reader = new Reader(bytes);
        // cn-279 sub_5649C0 reads version 2, a float, then named records until EOF; sub_4EC180 reads u8-counted strings.
        if (reader.Integer() != 2) throw new InvalidDataException("World-block helper reading requires a version-2 resource.");
        _ = reader.Single();
        List<WorldBlockHelper> helpers = [];
        while (!reader.AtEnd)
        {
            var offset = reader.Offset;
            var name = reader.String();
            var kind = reader.Integer();
            var properties = reader.String();
            if (kind == 1)
            {
                if (helpers.Count == maximumHelpers) throw new InvalidDataException("World-block helper count exceeds its limit.");
                helpers.Add(new(name, reader.Vector(), reader.Vector(), offset, properties));
            }
            else
            {
                // cn-279 sub_5641F0 skips every non-helper record, including material and animation payloads.
                reader.Skip(12);
                var materials = reader.Count();
                reader.SkipElements(materials, 4);
                for (var material = 0; material < materials; material++)
                {
                    if (reader.String() == "No Texture") continue;
                    reader.Skip(4);
                    _ = reader.String();
                    reader.Skip(36);
                    _ = reader.String();
                    if (reader.Integer() == 1) _ = reader.String();
                    reader.Skip(36);
                    for (var track = 0; track < 6; track++)
                    {
                        var keys = reader.Count();
                        if (keys > 0) reader.SkipElements(keys, reader.Integer() == 0 ? 16 : 8);
                    }
                }
            }
        }
        return helpers.AsReadOnly();
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        internal int Offset { get; private set; }
        internal bool AtEnd => Offset == _bytes.Length;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _bytes.Length - Offset) throw new InvalidDataException("World-block record is truncated.");
            var result = _bytes.Slice(Offset, count);
            Offset += count;
            return result;
        }

        internal int Integer() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal int Count()
        {
            var count = Integer();
            return count >= 0 ? count : throw new InvalidDataException("World-block collection count is negative.");
        }
        internal float Single()
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("World-block helper transforms must be finite.");
        }
        internal Vector3 Vector() => new(Single(), Single(), Single());
        internal string String() => Encoding.Latin1.GetString(Take(Take(1)[0]));
        internal void Skip(int count) => _ = Take(count);
        internal void SkipElements(int count, int size)
        {
            var length = (long)count * size;
            if (length > _bytes.Length - Offset) throw new InvalidDataException("World-block collection exceeds its remaining bytes.");
            Skip((int)length);
        }
    }
}
