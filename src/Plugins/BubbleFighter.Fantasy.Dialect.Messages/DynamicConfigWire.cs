using System.Globalization;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Serves the build's native DynamicConfig generation. The per-slot content rows in the build's
/// <c>dynamic_config</c> block are marshaled through their packet schemas and framed as the S2C 1500 category mask
/// plus one body per set bit. What a body is depends on the build's own <c>DynamicConfigReply</c> table, which names
/// each slot's carrier: <c>DynamicConfigBlob</c> (kr-1256 sub_9D2FF0 case 1500 walks the set bits ascending through
/// kr-1256 sub_65F370, which inflates a counted zlib blob, restamps the slot's registry entry with an Adler-32 over the
/// compressed bytes seeded with zero, kr-1256 sub_65F370 -> kr-1256 sub_12CB730, and applies the slot decoder) or the
/// slot's own schema (kr-812 sub_45B5E0 hands the plain packet bytes to the decoder and stamps the Adler-32 of the
/// bytes it consumed, kr-812 sub_A5FB20). A failed bit abandons the rest of the frame, so a bit is served only when its
/// schema and content rows actually exist.
/// Bits 7, 10, 11 and 15 are absent from the served mask here: the slot 7 and 11 schemas are
/// only partially split and slots 10 and 15 register at runtime without a static decoder.
/// </summary>
internal sealed class DynamicConfigWire : IPetClientConfigurationSink
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly Dictionary<int, Slot> _slots = new();
    private readonly byte[]? _creationSnapshot;
    private readonly Template? _petTemplate;

    /// <summary>
    /// One served category: its registry bit, schema, the body the client's stamp covers (the zlib bytes of a blob
    /// carrier, the plain marshaled bytes of a direct one) and the content a direct carrier writes in place.
    /// </summary>
    private sealed record Slot(int Bit, string Schema, byte[] Body, bool Blob, Dictionary<string, object?> Content, uint Stamp);

    private sealed record Template(string Schema, Dictionary<string, object?> Content);

    internal DynamicConfigWire(PacketTables tables, string build, string protocol = "")
    {
        _tables = tables;
        _build = build;
        Protocol = protocol;
        var rows = DynamicConfigContent.Load(tables, build);
        var supportsPets = PetConfigurationBuilds(tables, build).Contains(build, StringComparer.Ordinal);
        if (rows.Count != 0 || supportsPets) ValidateEnvelope();
        foreach (var (rawBit, raw) in rows)
        {
            if (!int.TryParse(rawBit, NumberStyles.None, CultureInfo.InvariantCulture, out var bit) || bit is < 1 or > 31)
                throw new ConfigException($"Build '{build}' dynamic_config slot keys must be registry bit numbers, not '{rawBit}'.");
            if (raw is not Dictionary<string, object?> entry
                || entry.GetValueOrDefault("schema") is not string schema
                || entry.GetValueOrDefault("content") is not Dictionary<string, object?> content)
                throw new ConfigException($"Build '{build}' dynamic_config slots must map schema and content.");
            if (_slots.ContainsKey(bit))
                throw new ConfigException($"Build '{build}' dynamic_config declares bit {bit} twice.");
            if (bit == 14 && tables.CapabilityOf(build, "character_creation"))
                content = MergeCreationContent(tables, build, content);
            _slots[bit] = Encode(bit, schema, content);
            if (bit == 14 && supportsPets) _petTemplate = new(schema, content);
        }
        if (supportsPets)
        {
            var petRows = DynamicConfigContent.Load(tables, build, "pet_content_file");
            if (petRows.Count != 0)
            {
                if (_petTemplate is not null || petRows.Count != 1
                    || petRows.GetValueOrDefault("14") is not Dictionary<string, object?> entry
                    || entry.GetValueOrDefault("schema") is not string schema
                    || entry.GetValueOrDefault("content") is not Dictionary<string, object?> content)
                    throw new ConfigException($"Build '{build}' pet content must supply only category 14, once.");
                _petTemplate = new(schema, content);
            }
            if (_petTemplate is null)
                throw new ConfigException($"Build '{build}' admits pet configuration without category 14 content.");
            ValidatePetTemplate(_petTemplate);
        }
        _creationSnapshot = _slots.ContainsKey(14) ? WriteReply([14], _slots) : WriteReply([], _slots);
    }

    /// <summary>Gets the protocol whose mounted DynamicConfig route publishes this generation.</summary>
    public string Protocol { get; }

    /// <summary>Whether this exact build admits both pet settings in its audited category 14.</summary>
    internal bool SupportsPetConfiguration => _petTemplate is not null;

    /// <summary>Verifies that the complete pet settings can be encoded through this build's admitted category.</summary>
    public void Validate(PetClientConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = CreationSnapshot(configuration);
    }

    /// <summary>Resolves the active protocol provider once when a response is prepared, after later rows may activate.</summary>
    internal static ValueTask<PetClientConfiguration?> ResolvePetConfigurationAsync(IEventBus events, string protocol,
        CancellationToken ct) => events.WaterfallAsync<PetClientConfigurationRequested, PetClientConfiguration?>(
            new(protocol), _ => ValueTask.FromResult<PetClientConfiguration?>(null), ct);

    /// <summary>Whether the build serves a native creation form that must load before its stage.</summary>
    internal bool HasCreationContent => _slots.ContainsKey(14);

    /// <summary>Writes a fresh creation snapshot before the stage reads its form path (kr-1256 sub_53D410).</summary>
    internal byte[] CreationSnapshot(PetClientConfiguration? configuration = null) => configuration is null
        ? _creationSnapshot! : WriteReply([14], Prepare(configuration));

    /// <summary>The registry bits this build serves.</summary>
    internal IEnumerable<int> ServedBits => _slots.Keys;

    /// <summary>
    /// Reads the client's category request (kr-1256 sub_99E860: a mask, then one stored stamp per
    /// set bit in ascending bit order) and frames the stale categories. Each stamp describes only
    /// the category identified by its bit. Categories the client already holds, and
    /// categories this build does not serve, are left out; a request with nothing stale answers
    /// with null so the caller can send the S2C 4 ack instead of an empty frame.
    /// </summary>
    internal byte[]? Reply(ReadOnlySpan<byte> request, PetClientConfiguration? configuration = null)
    {
        var slots = Prepare(configuration);
        // A build that serves no category keeps answering with its native empty-mask frame.
        if (slots.Count == 0) return LoginBurstWire.WriteDynamicConfigEmpty(_tables, _build);
        var values = PacketCodec.ReadScalars(_tables, _build, "DynamicConfigRequest", request);
        var mask = values["mask"] switch
        {
            ushort value => value,
            int value => unchecked((uint)value),
            _ => throw new InvalidDataException("Dynamic config requires an unsigned 16-bit or 32-bit mask."),
        };
        // kr-1256 sub_99E860 walks registry ids 0 through 31 and writes each selected entry's own stamp.
        var stale = slots.Values.Where(slot => (mask >> slot.Bit & 1) != 0
                && unchecked((uint)(int)values["checksum_" + slot.Bit.ToString(CultureInfo.InvariantCulture)]) != slot.Stamp)
            .Select(slot => slot.Bit).ToArray();
        return stale.Length == 0 ? null : WriteReply(stale, slots);
    }

    /// <summary>Computes the requested categories' stamps without framing a reply.</summary>
    internal uint StampOf(int bit, PetClientConfiguration? configuration = null) =>
        Prepare(configuration).TryGetValue(bit, out var slot) ? slot.Stamp
            : throw new InvalidOperationException($"Build '{_build}' does not serve dynamic config bit {bit}.");

    private byte[] WriteReply(IReadOnlyCollection<int> bits, IReadOnlyDictionary<int, Slot> slots)
    {
        var values = new Dictionary<string, object?> { ["mask"] = bits.Aggregate(0, (mask, bit) => mask | 1 << bit) };
        foreach (var bit in bits)
            values["slot_" + bit.ToString(CultureInfo.InvariantCulture)] = slots[bit].Blob
                ? new Dictionary<string, object?> { ["compressed"] = slots[bit].Body }
                : slots[bit].Content;
        return PacketCodec.Write(_tables, _build, "DynamicConfigReply", values);
    }

    private IReadOnlyDictionary<int, Slot> Prepare(PetClientConfiguration? configuration)
    {
        if (configuration is null) return _slots;
        var template = _petTemplate
            ?? throw new ConfigException($"Build '{_build}' has no audited pet client configuration route.");
        // kr-1256 sub_EE1F10 and cn-774 sub_E5B7F0 add this f32 duration to the selector-7 OA date.
        var content = new Dictionary<string, object?>(template.Content, StringComparer.Ordinal)
        {
            ["unknown_52"] = BitConverter.SingleToInt32Bits((float)(configuration.FeedCooldownSeconds / 86400d)),
            ["unknown_56"] = configuration.MileageDisplayMaximum,
        };
        return new Dictionary<int, Slot>(_slots) { [14] = Encode(14, template.Schema, content) };
    }

    private Slot Encode(int bit, string schema, Dictionary<string, object?> content)
    {
        var plain = PacketCodec.Write(_tables, _build, schema, content);
        var carrier = CarrierOf(_tables, _build, bit);
        if (carrier == "DynamicConfigBlob")
        {
            PacketLayoutContract.RequireFields(_tables, _build, "DynamicConfigBlob", "Native dynamic config", ("compressed", "list<u8>"));
            using var target = new MemoryStream();
            using (var zlib = new ZLibStream(target, CompressionLevel.Fastest)) zlib.Write(plain);
            var compressed = target.ToArray();
            return new(bit, schema, compressed, true, content, Checksum(compressed));
        }
        if (carrier != schema)
            throw new ConfigException($"Build '{_build}' frames dynamic config bit {bit} as {carrier}, but its content is {schema}.");
        return new(bit, schema, plain, false, content, Checksum(plain));
    }

    /// <summary>
    /// Reads what the build's <c>DynamicConfigReply</c> carries for a bit: the type of its <c>slot_N</c> field,
    /// <c>DynamicConfigBlob</c> for a counted zlib blob or the slot's own schema for plain bytes.
    /// </summary>
    internal static string CarrierOf(PacketTables tables, string build, int bit)
    {
        var name = "slot_" + bit.ToString(CultureInfo.InvariantCulture);
        var type = tables.Resolve(build, "DynamicConfigReply").FirstOrDefault(field => field.Name == name)?.Type
            ?? throw new ConfigException($"Build '{build}' DynamicConfigReply has no {name}, so dynamic config bit {bit} cannot be served.");
        const string prefix = "when<";
        var comma = type.LastIndexOf(',');
        if (!type.StartsWith(prefix, StringComparison.Ordinal) || !type.EndsWith('>') || comma < 0)
            throw new ConfigException($"Build '{build}' {name} must be a 'when<mask&bit,Carrier>' field, not '{type}'.");
        return type[(comma + 1)..^1].Trim();
    }

    /// <summary>
    /// Whether a request whose categories are all current is answered: <c>dynamic_config.unchanged_reply</c> of the
    /// build is <c>ack</c> (the default; the caller sends its acknowledgement frame) or <c>none</c> (nothing waits for
    /// an answer, as in kr-812 sub_820060, so none is sent).
    /// </summary>
    internal static bool AnswersUnchanged(PacketTables tables, string build)
    {
        if (tables.ValuesOf(build).GetValueOrDefault("dynamic_config") is not Dictionary<string, object?> block
            || block.GetValueOrDefault("unchanged_reply") is not string mode) return true;
        return mode switch
        {
            "ack" => true,
            "none" => false,
            _ => throw new ConfigException($"Build '{build}' dynamic_config.unchanged_reply must be 'ack' or 'none', not '{mode}'."),
        };
    }

    private void ValidateEnvelope()
    {
        var maskType = _tables.Resolve(_build, "DynamicConfigRequest").FirstOrDefault(field => field.Name == "mask")?.Type;
        if (maskType is not ("u16" or "u32"))
            throw new ConfigException($"Build '{_build}' DynamicConfig requires an audited u16 or u32 mask.");
        PacketLayoutContract.RequireFields(_tables, _build, "DynamicConfigRequest", "Native dynamic config", ("mask", maskType));
        PacketLayoutContract.RequireFields(_tables, _build, "DynamicConfigReply", "Native dynamic config", ("mask", maskType));
    }

    private void ValidatePetTemplate(Template template)
    {
        PacketLayoutContract.RequireFields(_tables, _build, "DynamicConfigRequest", "Pet client configuration",
            ("checksum_14", "when<mask&0x4000,u32>"));
        PacketLayoutContract.RequireFields(_tables, _build, "DynamicConfigReply", "Pet client configuration",
            ("slot_14", "when<mask&0x4000,DynamicConfigBlob>"));
        PacketLayoutContract.RequireFields(_tables, _build, template.Schema, "Pet client configuration",
            ("ui_path", "wstr"), ("character_types", "list<u32>"), ("preview_overrides", "list<CreationPreviewOverride>"),
            ("unknown_52", "u32"), ("unknown_56", "u32"));
        if (template.Content.GetValueOrDefault("ui_path") is not string { Length: > 0 }
            || template.Content.GetValueOrDefault("character_types") is not Array { Length: > 0 }
            || template.Content.GetValueOrDefault("preview_overrides") is not Array)
            throw new ConfigException($"Build '{_build}' pet configuration requires explicit native creation inputs.");
    }

    private static string[] PetConfigurationBuilds(PacketTables tables, string build)
    {
        if (tables.ValuesOf(build).GetValueOrDefault("dynamic_config") is not Dictionary<string, object?> block
            || !block.TryGetValue("pet_configuration_builds", out var admitted)) return [];
        return admitted as string[]
            ?? throw new ConfigException($"Build '{build}' pet_configuration_builds must be an explicit build list.");
    }

    /// <summary>
    /// Fills the creation category's form path and character choices from the lobby block, so the
    /// served form and the stage's own form stay one decision. The served content's remaining
    /// rows (the preview overrides and the two trailing words) come from the content block.
    /// </summary>
    private static Dictionary<string, object?> MergeCreationContent(PacketTables tables, string build,
        Dictionary<string, object?> content)
    {
        var lobby = (IReadOnlyDictionary<string, object?>)tables.ValuesOf(build)["lobby"]!;
        if (!lobby.TryGetValue("creation_ui", out var raw) || raw is not string { Length: > 0 } ui
            || !lobby.TryGetValue("creation_character_types", out var choices) || choices is not string[] types)
            throw new ConfigException($"Build '{build}' must define creation_ui and creation_character_types for its native form.");
        var characterTypes = types.Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var type) && type > 0
            ? type : throw new ConfigException($"Build '{build}' has an invalid creation character type '{value}'.")).ToArray();
        if (characterTypes.Length == 0)
            throw new ConfigException($"Build '{build}' has no native creation character choices.");
        if (content.ContainsKey("ui_path") || content.ContainsKey("character_types"))
            throw new ConfigException($"Build '{build}' dynamic_config bit 14 must leave ui_path and character_types to the lobby block.");
        var merged = new Dictionary<string, object?>(content, StringComparer.Ordinal)
        {
            ["ui_path"] = ui,
            ["character_types"] = characterTypes,
        };
        PacketLayoutContract.RequireFields(tables, build, "CreationContent", "Native creation content",
            ("ui_path", "wstr"), ("character_types", "list<u32>"), ("preview_overrides", "list<CreationPreviewOverride>"),
            ("unknown_52", "u32"), ("unknown_56", "u32"));
        return merged;
    }

    // kr-1256 sub_65F370 -> kr-1256 sub_12CB730 hashes the compressed bytes, seeding the running
    // sum with zero; the value the client reports back in C2S 1500 is exactly this seed-zero
    // Adler-32 over the compressed bytes it holds. kr-812 sub_A5FB20 -> kr-812 sub_BC81B0 hashes the plain bytes
    // its decoder consumed the same way.
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
