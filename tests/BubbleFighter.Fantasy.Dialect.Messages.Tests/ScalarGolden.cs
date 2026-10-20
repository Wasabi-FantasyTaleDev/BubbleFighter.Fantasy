using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

// Preserves the pre-migration scalar codec solely for byte comparisons against the shared codec.
internal static class ScalarGolden
{
    internal static byte[] Write(IReadOnlyList<PacketField> fields, Dictionary<string, int> values, RoundSupplyPoint? point = null)
    {
        var buffer = new ArrayBufferWriter<byte>(40);
        var writer = new PacketWriter(buffer);
        foreach (var field in fields)
        {
            switch (field.Type)
            {
                case "u32": writer.WriteInt32(values.GetValueOrDefault(field.Name, unchecked((int)field.Default))); break;
                case "f32":
                    if (point is null) throw new ConfigException("Supply position is missing.");
                    writer.WriteSingle(field.Name switch { "x" => point.X, "y" => point.Y, "z" => point.Z,
                        _ => throw new ConfigException($"Unknown supply coordinate {field.Name}.") });
                    break;
                default: throw new ConfigException($"Unsupported supply field {field.Type}.");
            }
        }
        return buffer.WrittenSpan.ToArray();
    }
}
