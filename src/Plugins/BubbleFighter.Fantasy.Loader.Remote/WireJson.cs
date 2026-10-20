using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>The System.Text.Json settings that define message body values on the remote wire (spec section 6).</summary>
internal static class WireJson
{
    /// <summary>
    /// Snake_case names, enums as their names, UTC ISO-8601 dates. Binding is strict: an unknown member or a missing
    /// constructor parameter is an error, never a silent default.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Writes <see cref="DateTime"/> as ISO-8601 UTC and reads any ISO-8601 value back as UTC.</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.Parse(reader.GetString() ?? throw new JsonException("A date must be a string."),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
    }
}
