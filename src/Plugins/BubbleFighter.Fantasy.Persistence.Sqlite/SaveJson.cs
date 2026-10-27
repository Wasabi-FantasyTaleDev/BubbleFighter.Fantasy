using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Settings;

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
        Converters = { new LifetimeConverter(), new CustomizedKeyConverter() },
    };

    /// <summary>Serializes the snapshot with the strict schema options.</summary>
    internal static string Encode(SaveState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>Decodes the snapshot; an empty document is a corrupt save, not an empty state.</summary>
    /// <exception cref="InvalidDataException">Thrown when the document is null or the JSON does not match the schema.</exception>
    internal static SaveState Decode(string json) => JsonSerializer.Deserialize<SaveState>(json, Options)
        ?? throw new InvalidDataException("SQLite player state is null.");

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
