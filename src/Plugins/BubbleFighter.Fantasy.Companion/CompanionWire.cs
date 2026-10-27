using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// One companion message. Every message carries a kind, an id, a lowercase dotted type, a schema version and a payload
/// (which may be JSON null); responses, errors and heartbeat acknowledgements name the message they answer, and every
/// message after the welcome carries the connection's epoch.
/// </summary>
internal sealed record CompanionEnvelope(
    string Kind, string Id, string Type, int Version, JsonElement Payload,
    string? ReplyTo = null, string? Epoch = null, string? OperationId = null);

/// <summary>The wire rules shared with the launcher: snake_case members, no unknown or duplicate members, strict UTF-8.</summary>
internal static class CompanionWire
{
    internal const string Subprotocol = "fantasytale.companion.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonElement Null = JsonSerializer.SerializeToElement<object?>(null);

    internal static JsonSerializerOptions Options(int depth) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = depth,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    internal static JsonElement Payload(object? value, JsonSerializerOptions options) =>
        value switch
        {
            null => Null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(value, value.GetType(), options),
        };

    /// <summary>Encodes a message; a message over <paramref name="maximum"/> bytes is refused rather than truncated.</summary>
    internal static byte[] Encode(CompanionEnvelope envelope, JsonSerializerOptions options, int maximum)
    {
        Validate(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, options);
        if (bytes.Length > maximum) throw new InvalidDataException("companion.message-too-large");
        return bytes;
    }

    internal static CompanionEnvelope Decode(ReadOnlySpan<byte> bytes, JsonSerializerOptions options)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = options.MaxDepth });
            RejectDuplicateProperties(document.RootElement);
            var envelope = document.RootElement.Deserialize<CompanionEnvelope>(options) ?? throw new InvalidDataException("companion.invalid-envelope");
            Validate(envelope);
            return envelope with { Payload = envelope.Payload.Clone() };
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            // Parser messages quote remote bytes; only the reason code leaves this method.
            throw new InvalidDataException("companion.invalid-envelope");
        }
    }

    /// <summary>Reads one text message, refusing it once it outgrows the bound or the fragment budget; null at close.</summary>
    internal static async Task<CompanionEnvelope?> ReceiveAsync(WebSocket socket, CompanionOptions limits, JsonSerializerOptions options, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(8192, limits.MessageBytes + 1)];
        using var message = new MemoryStream();
        for (var fragments = 1; ; fragments++)
        {
            if (fragments > limits.MessageFragments) throw new InvalidDataException("companion.fragment-limit");
            var part = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("companion.text-required");
            if (message.Length + part.Count > limits.MessageBytes) throw new InvalidDataException("companion.message-too-large");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return Decode(message.GetBuffer().AsSpan(0, (int)message.Length), options);
        }
    }

    /// <summary>Type names are lowercase dotted identifiers starting with a letter, as the launcher requires.</summary>
    internal static bool ValidName(string? value) => value is { Length: > 0 and <= 96 } && char.IsAsciiLetterLower(value[0])
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_');

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static string NewId() => Guid.NewGuid().ToString("N");

    private static void Validate(CompanionEnvelope envelope)
    {
        if (envelope.Kind is not ("hello" or "welcome" or "event" or "request" or "response" or "error" or "heartbeat" or "heartbeat_ack" or "state")
            || !ValidName(envelope.Type) || !ValidId(envelope.Id) || envelope.Version is < 1 or > 65535
            || envelope.Payload.ValueKind == JsonValueKind.Undefined
            || envelope.ReplyTo is not null && !ValidId(envelope.ReplyTo)
            || envelope.Epoch is not null && !ValidId(envelope.Epoch)
            || envelope.OperationId is not null && !ValidId(envelope.OperationId)
            || (envelope.Kind is "response" or "error" or "heartbeat_ack") != (envelope.ReplyTo is not null))
            throw new InvalidDataException("companion.invalid-envelope");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("companion.duplicate-property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
