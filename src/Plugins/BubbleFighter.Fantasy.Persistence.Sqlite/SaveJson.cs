using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// Encodes and decodes the player's <see cref="SaveState"/> snapshot. Unknown members are rejected on
/// decode rather than ignored: a stored snapshot whose shape the current schema does not know fails
/// loudly instead of silently dropping fields, which keeps older saves readable while turning schema
/// drift into a visible migration problem rather than a silent data-loss path.
/// </summary>
internal static class SaveJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new LifetimeConverter(), new CustomizedKeyConverter(), new BattleRecordConverter() },
    };

    /// <summary>Serializes the snapshot with the strict schema options.</summary>
    internal static string Encode(SaveState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>Decodes the snapshot; an empty document is a corrupt save, not an empty state.</summary>
    /// <exception cref="InvalidDataException">Thrown when the document is null or the JSON does not match the schema.</exception>
    internal static SaveState Decode(string json) => JsonSerializer.Deserialize<SaveState>(json, Options)
        ?? throw new InvalidDataException("SQLite player state is null.");

    // Every stored counter is required: a truncated history record must not silently turn into zero statistics.
    private sealed class BattleRecordConverter : JsonConverter<PlayerBattleRecord>
    {
        private static readonly string[] Fields = ["ModeId", "Wins", "Draws", "Losses", "Escapes",
            "Knockdowns", "Kills", "Rescues", "Deaths", "Score"];

        public override PlayerBattleRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != Fields.Length)
                throw new JsonException("A battle aggregate must contain its mode and every counter exactly once.");
            var values = new long[Fields.Length];
            for (var index = 0; index < Fields.Length; index++)
                if (!root.TryGetProperty(Fields[index], out var field) || field.ValueKind != JsonValueKind.Number
                    || !field.TryGetInt64(out values[index]))
                    throw new JsonException("A battle aggregate is missing a required integer field.");
            return new(checked((int)values[0]), values[1], values[2], values[3], values[4], values[5],
                values[6], values[7], values[8], values[9]);
        }

        public override void Write(Utf8JsonWriter writer, PlayerBattleRecord value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            long[] values = [value.ModeId, value.Wins, value.Draws, value.Losses, value.Escapes,
                value.Knockdowns, value.Kills, value.Rescues, value.Deaths, value.Score];
            for (var index = 0; index < Fields.Length; index++) writer.WriteNumber(Fields[index], values[index]);
            writer.WriteEndObject();
        }
    }

    // Missing key words must not deserialize as zero, which is a valid saved binding.
    private sealed class CustomizedKeyConverter : JsonConverter<CustomizedKey>
    {
        public override CustomizedKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty(nameof(CustomizedKey.Name), out var name) || name.ValueKind != JsonValueKind.String
                || !root.TryGetProperty(nameof(CustomizedKey.Key), out var key) || key.ValueKind != JsonValueKind.Number
                || !key.TryGetInt32(out var value))
                throw new JsonException("Invalid customized-key object.");
            return new(name.GetString()!, value);
        }

        public override void Write(Utf8JsonWriter writer, CustomizedKey value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(CustomizedKey.Name), value.Name);
            writer.WriteNumber(nameof(CustomizedKey.Key), value.Key);
            writer.WriteEndObject();
        }
    }

    private sealed class LifetimeConverter : JsonConverter<InventoryLifetime>
    {
        public override InventoryLifetime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2)
                throw new JsonException("Invalid inventory lifetime object.");
            var kind = (InventoryLifetimeKind)root.GetProperty(nameof(InventoryLifetime.Kind)).GetInt32();
            var deadline = root.GetProperty(nameof(InventoryLifetime.ExpiresAtUnixMilliseconds));
            return new(kind, deadline.ValueKind == JsonValueKind.Null ? null : deadline.GetInt64());
        }

        public override void Write(Utf8JsonWriter writer, InventoryLifetime value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(InventoryLifetime.Kind), (int)value.Kind);
            if (value.ExpiresAtUnixMilliseconds is { } deadline)
                writer.WriteNumber(nameof(InventoryLifetime.ExpiresAtUnixMilliseconds), deadline);
            else writer.WriteNull(nameof(InventoryLifetime.ExpiresAtUnixMilliseconds));
            writer.WriteEndObject();
        }
    }
}
