using System.Buffers;
using System.Collections;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>
/// Writes and reads packet structures by field name, so a plugin supplies values and
/// never field order or widths. A value is looked up by the field's name; a field without one carries
/// its table default. A nested structure takes its values from a map under its own field name, or,
/// when there is none, from the same map as its parent, so a caller can fill a nested field such as a
/// nickname without knowing which structure holds it.
/// </summary>
public static partial class PacketCodec
{
    /// <summary>Reads a complete flat scalar structure using the selected build's field order.</summary>
    /// <param name="tables">The audited packet tables.</param>
    /// <param name="build">The client build whose scalar layout is read.</param>
    /// <param name="structure">The structure name.</param>
    /// <param name="payload">The complete packet body, without its framing header.</param>
    /// <param name="maxStringCharacters">The maximum permitted character count before decoding a string.</param>
    /// <param name="requireCanonicalBooleans">Reject boolean words other than zero or one when enabled.</param>
    /// <returns>Scalar field values; words preserve their bits as signed 32-bit integers.</returns>
    /// <exception cref="PacketFormatException">The body is truncated, oversized or has trailing bytes.</exception>
    /// <exception cref="ConfigException">A field is not a supported scalar type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The string bound exceeds the native frame limit.</exception>
    public static Dictionary<string, object> ReadScalars(PacketTables tables, string build, string structure,
        ReadOnlySpan<byte> payload, int maxStringCharacters = 1024, bool requireCanonicalBooleans = false)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (maxStringCharacters is < 0 or > (ushort.MaxValue - 4) / 2)
            throw new ArgumentOutOfRangeException(nameof(maxStringCharacters));
        if (payload.Length > ushort.MaxValue)
            throw new PacketFormatException("Body exceeds the native frame bound.", 0);
        var reader = new PacketReader(payload);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in tables.Resolve(build, structure))
        {
            var type = field.Type;
            if (PacketTables.TryParseWhen(type, out var gate, out var mask, out var inner))
            {
                if (IsStructure(inner))
                    throw new ConfigException($"'{inner}' is not a supported scalar field type; use PacketCodec.Read.");
                if ((unchecked((ulong)Number(values[gate])) & mask) == 0)
                    continue;
                type = inner;
            }
            values.Add(field.Name, ReadScalar(ref reader, type, maxStringCharacters, requireCanonicalBooleans));
        }
        if (reader.Remaining != 0)
            throw new PacketFormatException($"{structure} has trailing bytes.", reader.Offset);
        return values;
    }

    private static object ReadScalar(ref PacketReader reader, string type, int maxStringCharacters, bool canonicalBooleans) =>
        type switch
        {
            "u8" => (object)reader.ReadByte(),
            "u16" => reader.ReadUInt16(),
            "u32" => reader.ReadInt32(),
            "u64" => reader.ReadInt64(),
            "bool" => ReadBoolean(ref reader, canonicalBooleans),
            "f32" => reader.ReadSingle(),
            "f64" => reader.ReadDouble(),
            "wstr" => ReadBoundedString(ref reader, maxStringCharacters),
            _ => throw new ConfigException($"'{type}' is not a supported scalar field type."),
        };

    private static bool ReadBoolean(ref PacketReader reader, bool canonical)
    {
        var value = reader.ReadUInt32();
        if (canonical && value > 1)
            throw new PacketFormatException("Boolean word must be zero or one.", reader.Offset);
        return value != 0;
    }

    private static string ReadBoundedString(ref PacketReader reader, int maximum)
    {
        var length = reader.ReadUInt32();
        if (length > maximum)
            throw new PacketFormatException($"String exceeds {maximum} characters.", reader.Offset);
        return System.Text.Encoding.Unicode.GetString(reader.ReadBytes(checked((int)length * 2)));
    }

    /// <summary>Writes one structure as one build has it.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">A build the tables name.</param>
    /// <param name="structure">The structure name.</param>
    /// <param name="values">The values by field name; missing fields carry their default.</param>
    /// <param name="maxBytes">The largest body the caller can send.</param>
    /// <returns>The body.</returns>
    /// <exception cref="ConfigException">Thrown for an unknown build, structure or field type.</exception>
    /// <exception cref="ArgumentException">Thrown for a value of the wrong kind or a run of the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the body is longer than <paramref name="maxBytes"/>.</exception>
    public static byte[] Write(PacketTables tables, string build, string structure,
        IReadOnlyDictionary<string, object?> values, int maxBytes = ushort.MaxValue)
    {
        if (maxBytes is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "A packet body must fit the native unsigned 16-bit length.");
        var buffer = new LimitedBufferWriter(maxBytes);
        var writer = new PacketWriter(buffer);
        Write(writer, tables, build, structure, values);
        if (buffer.WrittenCount > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(values), $"{structure} is {buffer.WrittenCount} bytes, more than the {maxBytes} allowed.");
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes one structure as one build has it into a writer.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">A build the tables name.</param>
    /// <param name="structure">The structure name.</param>
    /// <param name="values">The values by field name; missing fields carry their default.</param>
    /// <exception cref="ConfigException">Thrown for an unknown build, structure or field type.</exception>
    /// <exception cref="ArgumentException">Thrown for a value of the wrong kind or a run of the wrong length.</exception>
    public static void Write(PacketWriter writer, PacketTables tables, string build, string structure,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(tables);
        WriteResolvedFields(writer, tables, build, structure, tables.Resolve(build, structure), values);
    }

    /// <summary>Writes fields already resolved from packet tables, including scene-specific field projections.</summary>
    /// <param name="writer">The enclosing packet's writer.</param>
    /// <param name="tables">Tables used to resolve nested structures.</param>
    /// <param name="build">The native build used for nested structures.</param>
    /// <param name="structure">The enclosing structure name used in diagnostics.</param>
    /// <param name="fields">The resolved fields in their native wire order.</param>
    /// <param name="values">Supplied values by field name; missing values carry the field defaults.</param>
    /// <exception cref="ConfigException">A field type or nested structure is unknown.</exception>
    /// <exception cref="ArgumentException">A supplied value cannot be encoded by its field type.</exception>
    public static void WriteResolvedFields(PacketWriter writer, PacketTables tables, string build, string structure,
        IReadOnlyList<PacketField> fields, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var field in fields)
        {
            values.TryGetValue(field.Name, out var value);
            if (PacketTables.TryParseWhen(field.Type, out var gate, out var mask, out var inner))
            {
                // Use the same width conversion as the serialized gate, including native two's-complement words.
                if (!values.TryGetValue(gate, out var suppliedGate) || suppliedGate is null)
                    throw new ArgumentException($"{structure}.{field.Name} requires an explicit '{gate}' value.", nameof(values));
                var source = fields.FirstOrDefault(candidate => candidate.Name == gate)
                    ?? throw new ConfigException($"{structure}.{field.Name} has no gate '{gate}' in this projection.");
                var bits = source.Type switch
                {
                    "u8" => unchecked((byte)Number(suppliedGate)),
                    "u16" => unchecked((ushort)Number(suppliedGate)),
                    "u32" => unchecked((uint)Number(suppliedGate)),
                    "u64" => unchecked((ulong)Number(suppliedGate)),
                    _ => throw new ConfigException($"{structure}.{field.Name} gate '{gate}' is not an unsigned scalar."),
                };
                if ((bits & mask) == 0)
                {
                    if (value is not null)
                        throw new ArgumentException($"{structure}.{field.Name} has a value while its gate is clear.", nameof(values));
                    continue;
                }
                if (value is null)
                    throw new ArgumentException($"{structure}.{field.Name} requires a value while its gate is set.", nameof(values));
                if (IsStructure(inner) && value is not IReadOnlyDictionary<string, object?>)
                    throw new ArgumentException($"{structure}.{field.Name} requires a map of field values.", nameof(values));
                WriteField(writer, tables, build, structure, field with { Type = inner }, value, values);
            }
            else
                WriteField(writer, tables, build, structure, field, value, values);
        }
    }

    private static void WriteField(PacketWriter writer, PacketTables tables, string build, string structure,
        PacketField field, object? value, IReadOnlyDictionary<string, object?> scope)
    {
        var type = field.Type;
        if (PacketTables.TryParseRun(type, out var element, out var count))
        {
            if (value is null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (IsStructure(element))
                        Write(writer, tables, build, element, new Dictionary<string, object?>());
                    else
                        WriteScalar(writer, element, field.Default, field, structure);
                }
                return;
            }
            var items = Items(value, field, structure);
            if (items.Count != count)
                throw new ArgumentException($"{structure}.{field.Name} is a run of {count}, not {items.Count}.", nameof(value));
            foreach (var item in items)
            {
                if (IsStructure(element))
                    Write(writer, tables, build, element, item as IReadOnlyDictionary<string, object?>
                        ?? throw new ArgumentException($"{structure}.{field.Name} needs a map of field values per item.", nameof(value)));
                else
                    WriteScalar(writer, element, item, field, structure);
            }
            return;
        }
        if (type == "empty_list")
        {
            writer.WriteUInt32(0);
            return;
        }
        if (PacketTables.TryParseOptional(type, out var tag, out var block))
        {
            // A map writes the block after its tag; no value or a number writes only an absent tag byte
            // (the field default, or that number), as a client's default-constructed record carries it.
            if (value is IReadOnlyDictionary<string, object?> present)
            {
                writer.WriteByte(tag);
                Write(writer, tables, build, block, present);
                return;
            }
            var absent = value is null ? field.Default : Number(value);
            if (absent == tag || absent is < 0 or > byte.MaxValue)
                throw new ArgumentException($"{structure}.{field.Name} cannot mark its block absent with tag {absent}.", nameof(value));
            writer.WriteByte((byte)absent);
            return;
        }
        if (type.StartsWith("list<", StringComparison.Ordinal))
        {
            var inner = type[5..^1];
            var items = value is null ? [] : Items(value, field, structure);
            writer.WriteUInt32(checked((uint)items.Count));
            foreach (var item in items)
            {
                if (IsStructure(inner))
                    Write(writer, tables, build, inner, item as IReadOnlyDictionary<string, object?>
                        ?? throw new ArgumentException($"{structure}.{field.Name} needs a map of field values per item.", nameof(value)));
                else
                    WriteScalar(writer, inner, item, field, structure);
            }
            return;
        }
        if (IsStructure(type))
        {
            Write(writer, tables, build, type, value as IReadOnlyDictionary<string, object?> ?? scope);
            return;
        }
        WriteScalar(writer, type, value ?? (type == "wstr" ? "" : field.Default), field, structure);
    }

    private static void WriteScalar(PacketWriter writer, string type, object? value, PacketField field, string structure)
    {
        try
        {
            switch (type)
            {
                case "u8": writer.WriteByte(unchecked((byte)Number(value))); break;
                case "u16": writer.WriteUInt16(unchecked((ushort)Number(value))); break;
                case "u32": writer.WriteUInt32(unchecked((uint)Number(value))); break;
                case "u64": writer.WriteInt64(Number(value)); break;
                case "bool": writer.WriteBool32(value is bool flag ? flag : Number(value) != 0); break;
                case "f32": writer.WriteSingle(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
                case "f64": writer.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
                case "wstr":
                    var text = value as string ?? throw new InvalidCastException("not a string");
                    if (text.Length > (ushort.MaxValue - 4) / 2)
                        throw new ArgumentOutOfRangeException(nameof(value), "String exceeds the native packet body bound.");
                    writer.WriteWideStringPrefixed(text);
                    break;
                default: throw new ConfigException($"{structure}.{field.Name} has the type '{type}', which this codec cannot write.");
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new ArgumentException($"{structure}.{field.Name} ({type}) cannot carry {value ?? "null"}: {ex.Message}", nameof(value), ex);
        }
    }

    // An unsigned field takes a negative number as its two's-complement bits, as the client's reader does.
    private static long Number(object? value) => value switch
    {
        bool flag => flag ? 1 : 0,
        uint u => u,
        ulong u => unchecked((long)u),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private static List<object?> Items(object value, PacketField field, string structure)
    {
        if (value is not IEnumerable items || value is string)
            throw new ArgumentException($"{structure}.{field.Name} needs a sequence of values.", nameof(value));
        var result = new List<object?>();
        foreach (var item in items)
        {
            if (result.Count == ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"{structure}.{field.Name} exceeds the packet collection bound.");
            result.Add(item);
        }
        return result;
    }

    private sealed class LimitedBufferWriter(int limit) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        internal int WrittenCount => _buffer.WrittenCount;
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

        public void Advance(int count)
        {
            Check(count);
            _buffer.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Check(sizeHint);
            var memory = _buffer.GetMemory(sizeHint);
            return memory[..Math.Min(memory.Length, limit - WrittenCount)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Check(sizeHint);
            var span = _buffer.GetSpan(sizeHint);
            return span[..Math.Min(span.Length, limit - WrittenCount)];
        }

        private void Check(int count)
        {
            if (count < 0 || count > limit - WrittenCount)
                throw new ArgumentOutOfRangeException(nameof(count), $"Packet body exceeds its {limit}-byte budget.");
        }
    }

    private static bool IsStructure(string type) => type.Length > 0 && char.IsAsciiLetterUpper(type[0]);
}
