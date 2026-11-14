using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Publishes opt-in authenticated room discovery without altering standalone room behavior.</summary>
[Plugin("cluster.room-directory", Category = "cluster", Names = ["en=Cluster room directory", "zh-Hans=集群房间目录", "zh-Hant=叢集房間目錄", "ko=클러스터 방 목록"], Description = "Bounded authenticated room discovery for a configured node.",
    Provides = [typeof(IFederatedRoomDiscovery)])]
public sealed class RoomDirectoryClientPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<RoomDirectoryClientOptions>();
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = options.MaxConcurrentRequests,
        }) { BaseAddress = new Uri(options.DirectoryUrl), Timeout = Timeout.InfiniteTimeSpan };
        var client = new RoomDirectoryClient(options, http);
        context.Effect(client);
        context.Effect(context.Services.Register<IFederatedRoomDiscovery>(client));
        return ValueTask.CompletedTask;
    }
}

internal sealed class RoomDirectoryClient(RoomDirectoryClientOptions options, HttpClient http)
    : IFederatedRoomDiscovery, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private readonly HashSet<string> _builds = options.Builds.ToHashSet(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private int _disposed;

    public async ValueTask<IReadOnlyList<FederatedRoom>> ListAsync(string clientBuild, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_builds.Contains(clientBuild)) throw new ArgumentException("Room discovery requires a configured build.", nameof(clientBuild));
        var active = Interlocked.Increment(ref _active);
        try
        {
            if (active > options.MaxConcurrentRequests)
                throw new InvalidOperationException("The configured room-discovery request capacity is exhausted.");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "/v1/nodes/" + Uri.EscapeDataString(options.NodeId) + "/rooms/" + Uri.EscapeDataString(clientBuild));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The room directory did not return JSON.");
            await response.Content.LoadIntoBufferAsync(options.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<RoomEnvelope>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The room directory omitted its response.");
            var rooms = reply.Rooms;
            if (reply.Schema != 1 || rooms is null || rooms.Length > options.MaxRooms)
                throw new InvalidDataException("The room directory returned an invalid schema or room count.");
            var identities = new HashSet<(string Node, int Room)>();
            foreach (var room in rooms)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (room is null || !RoomDirectoryClientOptions.BackendId(room.NodeId) || room.Epoch <= 0
                    || room.ClientBuild != clientBuild || room.RoomId < 0
                    || !Text(room.Title, 256) || !Text(room.Mode, 64) || !Text(room.Phase, 32)
                    || room.Map is not null && !Text(room.Map, 256)
                    || room.Capacity is < 1 or > 256 || room.Players < 0 || room.Players > room.Capacity
                    || !identities.Add((room.NodeId, room.RoomId)) || !RoomListingValidation.IsValid(room, options.MaxRoomSettingsBytes))
                    throw new InvalidDataException("The room directory returned an invalid or duplicate room.");
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(rooms);
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Cancel(); }
        finally
        {
            http.Dispose();
            _stop.Dispose();
        }
    }

    private static bool Text(string? text, int limit) => text is not null && text.Length <= limit && !text.Any(char.IsControl);

    private sealed record RoomEnvelope([property: JsonRequired] int Schema, [property: JsonRequired] FederatedRoom[]? Rooms);
}
