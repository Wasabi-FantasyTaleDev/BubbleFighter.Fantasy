using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Packets;

public static partial class PacketCodec
{
    /// <summary>Replaces named top-level strings while retaining every other encoded field byte for byte.</summary>
    /// <param name="tables">The tables used to locate and validate the entire original structure.</param>
    /// <param name="build">The selected packet lineage.</param>
    /// <param name="structure">The structure whose top-level string fields are replaced.</param>
    /// <param name="payload">The complete original body, within the native frame bound.</param>
    /// <param name="replacements">Replacement strings indexed by their audited field names.</param>
    /// <returns>The complete body with only the selected string encodings changed.</returns>
    /// <exception cref="ConfigException">A replacement does not name a top-level string field.</exception>
    /// <exception cref="PacketFormatException">The original body is malformed or exceeds the standard read bounds.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resulting body exceeds the native frame bound.</exception>
    public static byte[] ReplaceStrings(PacketTables tables, string build, string structure,
        ReadOnlySpan<byte> payload, IReadOnlyDictionary<string, string> replacements)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(replacements);
        var fields = tables.Resolve(build, structure);
        foreach (var name in replacements.Keys)
            if (!fields.Any(field => field.Name == name && (field.Type == "wstr"
                || PacketTables.TryParseWhen(field.Type, out _, out _, out var inner) && inner == "wstr")))
                throw new ConfigException($"{build} {structure} has no top-level string field '{name}'.");
        if (payload.Length > ushort.MaxValue)
            throw new PacketFormatException("Body exceeds the native frame bound.", 0);
        var reader = new PacketReader(payload);
        var budget = new ReadBudget(new PacketReadLimits(), canonicalBooleans: false);
        budget.Reserve(fields.Count, 0);
        var buffer = new LimitedBufferWriter(ushort.MaxValue);
        var writer = new PacketWriter(buffer);
        var values = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var type = field.Type;
            if (PacketTables.TryParseWhen(type, out var gate, out var mask, out var inner))
            {
                if ((unchecked((ulong)Number(values[gate])) & mask) == 0)
                {
                    if (replacements.ContainsKey(field.Name))
                        throw new ArgumentException($"{structure}.{field.Name} is absent and cannot be replaced.", nameof(replacements));
                    continue;
                }
                type = inner;
            }
            var start = reader.Offset;
            values.Add(field.Name, ReadValue(ref reader, tables, build, type, budget, 1));
            if (replacements.TryGetValue(field.Name, out var text))
                writer.WriteWideStringPrefixed(text);
            else
                writer.WriteBytes(payload[start..reader.Offset]);
        }
        if (reader.Remaining != 0)
            throw new PacketFormatException($"{structure} has trailing bytes.", reader.Offset);
        return buffer.WrittenSpan.ToArray();
    }
}
