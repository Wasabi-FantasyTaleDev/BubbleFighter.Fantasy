using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace FantasyTale.Gateway;

internal enum WireFieldKind { Word, Short, Boolean, WideString, List }
internal sealed record WireField(string Name, WireFieldKind Kind, long Default, CompiledPacket? Child,
    string? MaskField = null, uint MaskBits = 0);

/// <summary>Executes only the field shapes compiled from packets/, without loading YAML at runtime.</summary>
internal sealed class CompiledPacket(WireField[] fields)
{
    private readonly bool _conditional = fields.Any(field => field.MaskField is not null);
    internal byte[] Write(IReadOnlyDictionary<string, object?> values)
    {
        var output = new ArrayBufferWriter<byte>();
        Write(new PacketWriter(output), values);
        return output.WrittenSpan.ToArray();
    }

    private void Write(PacketWriter writer, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var field in fields)
        {
            if (field.MaskField is { } mask && (!values.TryGetValue(mask, out var maskValue)
                || (unchecked((uint)Convert.ToInt64(maskValue)) & field.MaskBits) == 0)) continue;
            values.TryGetValue(field.Name, out var value);
            switch (field.Kind)
            {
                case WireFieldKind.Word:
                    writer.WriteUInt32(unchecked((uint)(value is null ? field.Default : Convert.ToInt64(value))));
                    break;
                case WireFieldKind.Short:
                    writer.WriteUInt16(checked((ushort)(value is null ? field.Default : Convert.ToInt64(value))));
                    break;
                case WireFieldKind.Boolean:
                    writer.WriteUInt32(value is true || value is null && field.Default != 0 ? 1u : 0u);
                    break;
                case WireFieldKind.WideString:
                    writer.WriteWideStringPrefixed(value as string ?? "");
                    break;
                case WireFieldKind.List:
                    var items = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)value!;
                    writer.WriteUInt32(checked((uint)items.Count));
                    foreach (var item in items)
                        field.Child!.Write(writer, item);
                    break;
                default:
                    throw new InvalidOperationException("Invalid compiled packet field.");
            }
        }
    }

    internal void ValidateFirstConnect(ReadOnlySpan<byte> payload, int major, int maxStringCharacters)
    {
        var reader = new PacketReader(payload);
        foreach (var field in fields)
        {
            switch (field.Kind)
            {
                case WireFieldKind.Word:
                    var word = reader.ReadUInt32();
                    if (field.Name == "client_version" && word != major)
                        throw new PacketFormatException("Client version does not match the selected build.", reader.Offset);
                    break;
                case WireFieldKind.WideString:
                    var length = reader.ReadUInt32();
                    if (length > maxStringCharacters)
                        throw new PacketFormatException("First-connect string exceeds its configured bound.", reader.Offset);
                    reader.ReadBytes(checked((int)length * 2));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported compiled first-connect field.");
            }
        }
        if (reader.Remaining != 0)
            throw new PacketFormatException("First-connect packet has trailing bytes.", reader.Offset);
    }

    internal void ValidateLogin(ReadOnlySpan<byte> payload, int maxStringCharacters, int maxItems)
        => ValidatePayload(payload, maxStringCharacters, maxItems);

    internal void ValidatePayload(ReadOnlySpan<byte> payload, int maxStringCharacters, int maxItems)
    {
        var reader = new PacketReader(payload);
        ValidateFields(ref reader, maxStringCharacters, maxItems);
        if (reader.Remaining != 0)
            throw new PacketFormatException("Packet has trailing bytes.", reader.Offset);
    }

    private void ValidateFields(ref PacketReader reader, int maxStringCharacters, int maxItems)
    {
        Dictionary<string, uint>? words = _conditional ? new(StringComparer.Ordinal) : null;
        foreach (var field in fields)
        {
            if (field.MaskField is { } mask && (words![mask] & field.MaskBits) == 0) continue;
            switch (field.Kind)
            {
                case WireFieldKind.Word:
                case WireFieldKind.Boolean:
                    var word = reader.ReadUInt32();
                    words?.Add(field.Name, word);
                    break;
                case WireFieldKind.Short:
                    var shortWord = reader.ReadUInt16();
                    words?.Add(field.Name, shortWord);
                    break;
                case WireFieldKind.WideString:
                    var length = reader.ReadUInt32();
                    if (length > maxStringCharacters || field.Name == "credential" && length == 0)
                        throw new PacketFormatException("Login string violates its configured bounds.", reader.Offset);
                    reader.ReadBytes(checked((int)length * 2));
                    break;
                case WireFieldKind.List:
                    var count = reader.ReadUInt32();
                    if (count > maxItems)
                        throw new PacketFormatException("Login adapter list exceeds its configured bound.", reader.Offset);
                    for (var i = 0u; i < count; i++)
                        field.Child!.ValidateFields(ref reader, maxStringCharacters, maxItems);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported compiled login field.");
            }
        }
    }
}
