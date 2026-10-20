using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Packets;

public static partial class PacketCodec
{
    /// <summary>Reads a complete table-defined packet, including nested structures, lists and fixed runs.</summary>
    /// <param name="tables">Audited layouts whose build differences determine the field order.</param>
    /// <param name="build">Client build whose resolved layouts are read.</param>
    /// <param name="structure">Root structure name.</param>
    /// <param name="payload">Complete unframed packet body, limited to the native 16-bit frame bound.</param>
    /// <param name="limits">Bounds for the decoded tree; omitted limits use the documented defaults.</param>
    /// <param name="requireCanonicalBooleans">Reject boolean words other than zero or one when enabled.</param>
    /// <returns>Nested field maps and element arrays that can also be supplied to the table writer.</returns>
    /// <exception cref="PacketFormatException">The body is truncated, has trailing bytes or exceeds a decoding bound.</exception>
    /// <exception cref="ConfigException">A table names an unsupported type or an unresolved structure.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A decoding limit exceeds its supported range.</exception>
    public static Dictionary<string, object?> Read(PacketTables tables, string build, string structure,
        ReadOnlySpan<byte> payload, PacketReadLimits? limits = null, bool requireCanonicalBooleans = false)
    {
        ArgumentNullException.ThrowIfNull(tables);
        limits ??= new PacketReadLimits();
        limits.Validate();
        if (payload.Length > ushort.MaxValue)
            throw new PacketFormatException("Body exceeds the native frame bound.", 0);
        var reader = new PacketReader(payload);
        var budget = new ReadBudget(limits, requireCanonicalBooleans);
        var values = ReadStructure(ref reader, tables, build, structure, budget, 1);
        if (reader.Remaining != 0)
            throw new PacketFormatException($"{structure} has trailing bytes.", reader.Offset);
        return values;
    }

    private static Dictionary<string, object?> ReadStructure(ref PacketReader reader, PacketTables tables,
        string build, string structure, ReadBudget budget, int depth)
    {
        budget.CheckDepth(depth, reader.Offset);
        var fields = tables.Resolve(build, structure);
        budget.Reserve(fields.Count, reader.Offset);
        var values = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var type = field.Type;
            if (PacketTables.TryParseWhen(type, out var gate, out var mask, out var inner))
            {
                if ((unchecked((ulong)Number(values[gate])) & mask) == 0)
                    continue;
                type = inner;
            }
            values.Add(field.Name, ReadValue(ref reader, tables, build, type, budget, depth));
        }
        return values;
    }

    private static object ReadValue(ref PacketReader reader, PacketTables tables, string build,
        string type, ReadBudget budget, int depth)
    {
        if (PacketTables.TryParseRun(type, out var element, out var count))
            return ReadElements(ref reader, tables, build, element, count, budget, depth + 1);
        if (type == "empty_list")
        {
            budget.CheckDepth(depth + 1, reader.Offset);
            // An outbound empty-list approximation cannot admit a nonempty inbound list.
            if (reader.ReadUInt32() != 0)
                throw new PacketFormatException("An empty-list field has a nonzero count.", reader.Offset);
            return Array.Empty<object?>();
        }
        if (type.StartsWith("list<", StringComparison.Ordinal) && type.EndsWith('>'))
        {
            var length = reader.ReadUInt32();
            if (length > budget.Limits.MaxCollectionElements)
                throw new PacketFormatException("List exceeds the element bound.", reader.Offset);
            return ReadElements(ref reader, tables, build, type[5..^1], (int)length, budget, depth + 1);
        }
        if (PacketTables.TryParseOptional(type, out var tag, out var block))
        {
            // The block's map when the tag announces it, otherwise the absent tag byte itself, so the
            // value writes back to the same bytes.
            var marker = reader.ReadByte();
            return marker == tag ? ReadStructure(ref reader, tables, build, block, budget, depth + 1) : marker;
        }
        if (IsStructure(type))
            return ReadStructure(ref reader, tables, build, type, budget, depth + 1);
        return ReadScalar(ref reader, type, budget.Limits.MaxStringCharacters, budget.CanonicalBooleans);
    }

    private static object?[] ReadElements(ref PacketReader reader, PacketTables tables, string build,
        string element, int count, ReadBudget budget, int depth)
    {
        budget.CheckDepth(depth, reader.Offset);
        if (count > budget.Limits.MaxCollectionElements)
            throw new PacketFormatException("Collection exceeds the element bound.", reader.Offset);
        budget.Reserve(count, reader.Offset);
        if (IsStructure(element))
            tables.Resolve(build, element);
        else if (element is not ("u8" or "u16" or "u32" or "u64" or "bool" or "f32" or "f64" or "wstr"))
            throw new ConfigException($"'{element}' is not a supported packet collection element type.");
        var width = element switch
        {
            "u8" => 1,
            "u16" => 2,
            "u32" or "bool" or "f32" => 4,
            "u64" or "f64" => 8,
            "wstr" or "empty_list" => 4,
            _ => 0,
        };
        if (width != 0 && count > reader.Remaining / width)
            throw new PacketFormatException("Collection exceeds the remaining body.", reader.Offset);
        var items = new object?[count];
        for (var i = 0; i < count; i++)
            items[i] = ReadValue(ref reader, tables, build, element, budget, depth);
        return items;
    }

    private sealed class ReadBudget(PacketReadLimits limits, bool canonicalBooleans)
    {
        private int _remaining = limits.MaxValues;
        internal PacketReadLimits Limits => limits;
        internal bool CanonicalBooleans => canonicalBooleans;

        internal void Reserve(int count, int offset)
        {
            if (count > _remaining)
                throw new PacketFormatException("Packet exceeds the total decoded-value bound.", offset);
            _remaining -= count;
        }

        internal void CheckDepth(int depth, int offset)
        {
            if (depth > limits.MaxDepth)
                throw new PacketFormatException("Packet exceeds the nesting bound.", offset);
        }
    }
}
