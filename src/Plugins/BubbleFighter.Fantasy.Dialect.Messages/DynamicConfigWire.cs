using System.Globalization;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Publishes the table-defined native creation form and the character choices accepted by the lobby.</summary>
internal sealed class DynamicConfigWire
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly byte[]? _compressed;
    private readonly uint _checksum;

    internal DynamicConfigWire(PacketTables tables, string build)
    {
        _tables = tables;
        _build = build;
        if (!tables.CapabilityOf(build, "character_creation")) return;
        var lobby = (IReadOnlyDictionary<string, object?>)tables.ValuesOf(build)["lobby"]!;
        if (!lobby.TryGetValue("creation_ui", out var raw)) return;
        if (raw is not string { Length: > 0 } ui ||
            !lobby.TryGetValue("creation_character_types", out var choices) || choices is not string[] types)
            throw new ConfigException($"Build '{build}' must define creation_ui and creation_character_types for its native form.");
        var characterTypes = types.Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var type) && type > 0
            ? type : throw new ConfigException($"Build '{build}' has an invalid creation character type '{value}'.")).ToArray();
        if (characterTypes.Length == 0)
            throw new ConfigException($"Build '{build}' has no native creation character choices.");
        PacketLayoutContract.RequireFields(tables, build, "CreationContent", "Native creation content",
            ("ui_path", "wstr"), ("character_types", "list<u32>"), ("preview_overrides", "list<CreationPreviewOverride>"),
            ("unknown_52", "u32"), ("unknown_56", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "DynamicConfigReply", "Native creation content",
            ("mask", "u32"), ("creation", "when<mask&0x4000,DynamicConfigBlob>"));
        PacketLayoutContract.RequireFields(tables, build, "DynamicConfigRequest", "Native creation content",
            ("mask", "u32"), ("checksum_14", "when<mask&0x4000,u32>"));
        PacketLayoutContract.RequireFields(tables, build, "DynamicConfigBlob", "Native creation content", ("compressed", "list<u8>"));
        var plain = PacketCodec.Write(tables, build, "CreationContent", new Dictionary<string, object?>
        {
            ["ui_path"] = ui, ["character_types"] = characterTypes,
        });
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, CompressionLevel.Fastest)) zlib.Write(plain);
        _compressed = target.ToArray();
        _checksum = Checksum(_compressed);
    }

    /// <summary>Whether this build has an audited native form that must be loaded before its creation stage.</summary>
    internal bool HasCreationContent => _compressed is not null;

    /// <summary>Writes a fresh snapshot before the stage reads its form path (kr-1256 sub_53D410).</summary>
    internal byte[] CreationSnapshot() => WriteReply(HasCreationContent);

    /// <summary>Replaces requested stale content; unchanged checksums and unsupported categories get an empty mask.</summary>
    internal byte[] Reply(ReadOnlySpan<byte> request)
    {
        if (!HasCreationContent) return WriteReply(false);
        var values = PacketCodec.ReadScalars(_tables, _build, "DynamicConfigRequest", request);
        return WriteReply(values.TryGetValue("checksum_14", out var raw) && unchecked((uint)(int)raw) != _checksum);
    }

    private byte[] WriteReply(bool includeCreation) => !includeCreation
        ? LoginBurstWire.WriteDynamicConfigEmpty(_tables, _build)
        : PacketCodec.Write(_tables, _build, "DynamicConfigReply", new Dictionary<string, object?>
    {
        ["mask"] = 1 << 14,
        ["creation"] = new Dictionary<string, object?> { ["compressed"] = _compressed! },
    });

    // kr-1256 sub_65F370 -> kr-1256 sub_12CB730 hashes the compressed bytes with Adler-32 seeded with zero.
    internal static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint low = 0, high = 0;
        foreach (var value in bytes)
        {
            low = (low + value) % 65521;
            high = (high + low) % 65521;
        }
        return (high << 16) | low;
    }
}
