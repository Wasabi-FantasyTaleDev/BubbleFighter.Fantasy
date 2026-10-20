using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>Resolves the native round selector's two strings to named fields in the retained game setting.</summary>
public sealed class RoomRoundSettingFields
{
    private readonly PacketTables _tables;
    private readonly string _build;
    private readonly string[] _request;
    private readonly string[] _targets;

    private RoomRoundSettingFields(PacketTables tables, string build, string[] request, string[] targets) =>
        (_tables, _build, _request, _targets) = (tables, build, request, targets);

    /// <summary>Loads a build's audited projection, or returns null when its target mapping is explicitly unavailable.</summary>
    /// <param name="tables">The packet tables containing the inherited mapping.</param>
    /// <param name="build">The selected packet lineage.</param>
    /// <returns>The checked projection, or null for an empty target list.</returns>
    public static RoomRoundSettingFields? Load(PacketTables tables, string build)
    {
        if (!tables.ValuesOf(build).TryGetValue("room_round_setting_fields", out var raw)
            || raw is not Dictionary<string, object?> values
            || values.Count != 2 || !values.ContainsKey("request_strings") || !values.ContainsKey("target_strings")
            || values["request_strings"] is not string[] request || request.Length != 2
            || values["target_strings"] is not string[] targets || targets.Length is not (0 or 6))
            throw new ConfigException($"{build} room_round_setting_fields requires two request_strings and zero or six target_strings.");
        if (targets.Length == 0)
            return null;
        RequireStrings("RoomRoundSettingChangeRequest", request);
        RequireStrings("GameSetting", targets);
        if (!tables.Resolve(build, "RoomRoundSettingChangeRequest").Any(field => field.Name == "round_index" && field.Type == "u32"))
            throw new ConfigException($"{build} round setting request requires its audited round_index word.");
        return new(tables, build, request, targets);

        void RequireStrings(string structure, string[] names)
        {
            var fields = tables.Resolve(build, structure);
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(name => !fields.Any(field => field.Name == name && field.Type == "wstr")))
                throw new ConfigException($"{build} room_round_setting_fields must name distinct {structure} string fields.");
        }
    }

    /// <summary>Decodes the complete native request and rejects indices outside the three native map controls.</summary>
    /// <param name="body">The request body, with no trailing data.</param>
    /// <returns>The zero-based index and its two otherwise opaque native strings.</returns>
    public (int Index, string First, string Second) Read(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(_tables, _build, "RoomRoundSettingChangeRequest", body);
        var index = (int)fields["round_index"];
        if ((uint)index >= 3)
            throw new PacketFormatException("The round-map index must select one of the three native controls.", 0);
        return (index, (string)fields[_request[0]], (string)fields[_request[1]]);
    }

    /// <summary>Overlays one selection without changing any other game-setting field.</summary>
    /// <param name="setting">The decoded retained game-setting block.</param>
    /// <param name="index">A zero-based native round-map control index.</param>
    /// <param name="first">The first native request string.</param>
    /// <param name="second">The second native request string.</param>
    public void Apply(IDictionary<string, object?> setting, int index, string first, string second)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 3);
        setting[_targets[2 * index]] = first;
        setting[_targets[2 * index + 1]] = second;
    }

    /// <summary>Replaces one encoded pair while preserving the opaque bytes of every other native field.</summary>
    /// <param name="setting">The complete retained game-setting body.</param>
    /// <param name="index">A zero-based native round-map control index.</param>
    /// <param name="first">The first native selector string.</param>
    /// <param name="second">The second native selector string.</param>
    /// <returns>The game-setting body with the selected pair replaced.</returns>
    public byte[] Apply(ReadOnlySpan<byte> setting, int index, string first, string second)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 3);
        return PacketCodec.ReplaceStrings(_tables, _build, "GameSetting", setting,
            new Dictionary<string, string>
            {
                [_targets[2 * index]] = first,
                [_targets[2 * index + 1]] = second,
            });
    }
}
