using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Reads the reviewed native C2S scene seed tables through packet data tables.</summary>
internal static class SceneSeedWire
{
    /// <summary>Reads one table and rejects a structure whose audited field shape is wrong.</summary>
    internal static NativeSceneSeedTable Read(PacketTables tables, string build, NativeSceneSeedKind kind,
        ReadOnlySpan<byte> payload, PacketReadLimits limits)
    {
        var structure = kind switch
        {
            NativeSceneSeedKind.Critical => "RelayCriticalSeedTable",
            NativeSceneSeedKind.Defence => "RelayDefenceSeedTable",
            NativeSceneSeedKind.CriticalCancellation => "RelayCriticalSeedCancelTable",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var fields = PacketCodec.Read(tables, build, structure, payload, limits);
        var entity = Convert.ToInt32(fields.GetValueOrDefault("entity_id"), System.Globalization.CultureInfo.InvariantCulture);
        var listFields = tables.Resolve(build, structure)
            .Where(field => field.Type is "list<u32>" or "list<u8>")
            .ToArray();
        if (listFields.Length == 0)
            throw new ConfigException($"'{structure}' for '{build}' has no native seed vector.");

        if (kind == NativeSceneSeedKind.Defence)
        {
            if (listFields.Length != 1 || listFields[0].Type != "list<u8>")
                throw new ConfigException($"'{structure}' for '{build}' has an invalid defence vector shape.");
            return NativeSceneSeedTable.Bytes(kind, entity, Values<byte>(fields[listFields[0].Name]));
        }

        if (listFields.Any(field => field.Type != "list<u32>"))
            throw new ConfigException($"'{structure}' for '{build}' has an invalid word vector shape.");
        var vectors = listFields.Select(field => Values<uint>(fields[field.Name]).ToArray()).ToArray();
        return NativeSceneSeedTable.WithWordVectors(kind, entity, vectors);
    }

    private static IEnumerable<T> Values<T>(object? value)
    {
        if (value is not object?[] values)
            throw new PacketFormatException("Native seed vector did not decode as a list.", 0);
        foreach (var item in values)
        {
            if (item is T typed)
            {
                yield return typed;
                continue;
            }
            // PacketCodec preserves u32 wire bits in the signed Int32 CLR type.
            if (typeof(T) == typeof(uint) && item is int word)
            {
                yield return (T)(object)unchecked((uint)word);
                continue;
            }
            throw new PacketFormatException("Native seed vector contained an unexpected value.", 0);
        }
    }
}
