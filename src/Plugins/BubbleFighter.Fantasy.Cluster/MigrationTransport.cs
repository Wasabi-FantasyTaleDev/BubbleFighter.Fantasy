using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Cluster;

internal static class MigrationTransport
{
    internal const string LeaseHeader = "X-Cluster-Lease";
    internal const string EpochHeader = "X-Cluster-Epoch";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new AddressConverter() },
    };

    internal static bool Valid(MigrationRequest? request) => request is not null && AccountHandoff.IsMigrationId(request.MigrationId)
        && RoomDirectoryClientOptions.BackendId(request.SourceNode) && RoomDirectoryClientOptions.BackendId(request.DestinationNode)
        && request.SourceNode != request.DestinationNode && request.DestinationEpoch > 0
        && RoomDirectoryClientOptions.Identifier(request.ClientBuild) && request.UserNum is > 0 and <= int.MaxValue
        && request.ClientAddress is { AddressFamily: AddressFamily.InterNetwork }
        && (request.PartyLeader is null || request.PartyLeader > 0 && request.Guest is null)
        && RoomDirectoryClientOptions.BackendId(request.HomeNode)
        && (request.Kind == MigrationKind.Home ? request.Room is null && request.Guest is null
                && request.HomeScene is SessionPhase.MainMenu or SessionPhase.Plaza
            : request.Kind == MigrationKind.Room && request.HomeScene == SessionPhase.MainMenu
                && request.Room is { RoomId: > 0 and <= ushort.MaxValue } room
                && room.InstanceId != Guid.Empty && room.UserNumber == request.UserNum && room.Password is { Length: <= 256 })
        && (request.Guest is null || Text(request.Guest.Nickname, 128) && Text(request.Guest.ServerName, 128)
            && request.Guest.Level >= 0 && request.Guest.EquippedItemIds is { Length: <= 256 } && request.Guest.ColorIndex >= 0);

    internal static bool Valid(MigrationOffer offer) => offer.Accepted
        ? offer.Ipv4!.AddressFamily == AddressFamily.InterNetwork && offer.Lifetime > TimeSpan.Zero
            && offer.Lifetime <= TimeSpan.FromMinutes(1)
        : offer.Ipv4 is null && offer.Port == 0 && offer.Lifetime == TimeSpan.Zero
            && (offer.Refusal is null || Enum.IsDefined(offer.Refusal.Value));

    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= limit && !value.Any(char.IsControl);

    private sealed class AddressConverter : JsonConverter<IPAddress>
    {
        public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { Length: <= 45 } text
                || !IPAddress.TryParse(text, out var address)) throw new JsonException("A migration address must be an IP literal.");
            return address;
        }
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

internal sealed record MigrationEnvelope(MigrationRequest Request, bool Cancel);
