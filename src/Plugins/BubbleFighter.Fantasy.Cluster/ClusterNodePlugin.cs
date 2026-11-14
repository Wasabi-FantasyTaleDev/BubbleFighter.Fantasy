using System.Net;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Reports build readiness, populations and published rooms without changing standalone game handlers.</summary>
[Plugin("cluster.node", Category = "cluster", Names = ["en=Cluster node", "zh-Hans=集群节点", "zh-Hant=叢集節點", "ko=클러스터 노드"], Description = "Authenticated node lease and room summary reporting.",
    Requires = [typeof(ISessionRegistry), typeof(IHostReadiness)], Provides = [typeof(INodeLeaseSource)],
    Optional = [typeof(IPersistentDataDomainIdentity), typeof(ILocalLauncherPresence)],
    OptionalMany = [typeof(IPublishedRoomDirectory), typeof(IListenerEndpoint)])]
public sealed class ClusterNodePlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<NodeOptions>();
        var leaseSource = new NodeLeaseSource(options.NodeId, options.DirectoryUrl);
        context.Effect(context.Services.Register<INodeLeaseSource>(leaseSource));
        var readiness = context.Services.Get<IHostReadiness>();
        var sessions = context.Services.Get<ISessionRegistry>();
        var directories = context.Services.GetAll<IPublishedRoomDirectory>();
        var listeners = context.Services.GetAll<IListenerEndpoint>();
        context.Services.TryGet<IPersistentDataDomainIdentity>(out var identity);
        context.Services.TryGet<ILocalLauncherPresence>(out var presence);
        if (options.ReportLauncherPresence && (presence is null || identity is null))
            throw new ConfigException("report_launcher_presence requires local Launcher presence and a persistent data domain provider.");
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 1 })
        { BaseAddress = new Uri(options.DirectoryUrl), Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        context.Effect(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.HeartbeatKey);
        await context.RunTaskAsync("cluster-node-heartbeat", async ct =>
        {
            var instance = Guid.NewGuid().ToString("N");
            LeaseGrant? lease = null;
            long sequence = 0;
            var prefix = "/v1/nodes/" + Uri.EscapeDataString(options.NodeId);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
            try
            {
                do
                {
                    try
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        attempt.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                        var reportToken = attempt.Token;
                        var dataDomainId = identity is null ? null : await identity.ReadAsync(reportToken).ConfigureAwait(false);
                        if (lease is null)
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/register")
                            { Content = JsonContent.Create(new NodeRegistration(instance)) };
                            using var registration = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, reportToken).ConfigureAwait(false);
                            registration.EnsureSuccessStatusCode();
                            await registration.Content.LoadIntoBufferAsync(options.MaxBodyBytes, reportToken).ConfigureAwait(false);
                            lease = await registration.Content.ReadFromJsonAsync<LeaseGrant>(reportToken).ConfigureAwait(false)
                                ?? throw new InvalidDataException("The directory omitted the node lease.");
                            if (lease.Token is not { Length: > 0 and <= 128 } || lease.Epoch <= 0 || lease.LeaseSeconds is < 3 or > 300)
                            { lease = null; throw new InvalidDataException("The directory returned an invalid node lease."); }
                            if (options.HeartbeatSeconds + options.RequestTimeoutSeconds >= lease.LeaseSeconds)
                                throw new ConfigException("Heartbeat interval plus request timeout must be shorter than the directory lease.");
                            sequence = 0;
                        }
                        var active = sessions.All.Where(session => !session.Connection.IsClosed && session.TryFeature<IPlayerSession>(out var player)
                            && player.LoginConnectionId == session.Connection.Id).ToArray();
                        var builds = options.Builds.Select(build => new NodeBuildState(build, readiness.IsReady
                            && listeners.Any(listener => listener.Protocol == build + "/game"),
                            active.Where(session => session.Profile.Id == build).Select(session => session.Connection.Id).Distinct().Count())).ToArray();
                        var rooms = new List<FederatedRoom>();
                        foreach (var directory in directories.Where(directory => options.Builds.Contains(directory.ClientBuild, StringComparer.Ordinal)))
                        {
                            foreach (var room in await directory.ListPublishedAsync(reportToken).ConfigureAwait(false))
                            {
                                if (rooms.Count >= options.MaxRooms) throw new InvalidDataException("Published room count exceeds the configured node report limit.");
                                var entry = room.Entry;
                                rooms.Add(new(options.NodeId, lease.Epoch, directory.ClientBuild, entry.RoomId, entry.Name,
                                    entry.ModeId.ToString(System.Globalization.CultureInfo.InvariantCulture), room.Map,
                                    room.Phase, entry.Occupants, entry.Capacity, entry.HasPassword, false)
                                    { InstanceId = room.InstanceId, Listing = entry });
                            }
                        }
                        var report = new NodeHeartbeat(lease.Token, lease.Epoch, checked(++sequence), options.Draining, dataDomainId, builds, rooms.ToArray())
                        {
                            Presence = options.ReportLauncherPresence ? presence!.Capture(includeRoster: true)
                                .Where(value => options.Builds.Contains(value.BuildId, StringComparer.Ordinal)).ToArray() : null,
                        };
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonSerializerOptions.Web);
                        if (bytes.Length > options.MaxBodyBytes) throw new InvalidDataException("Node report exceeds max_body_bytes.");
                        using var content = new ByteArrayContent(bytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        using var heartbeat = new HttpRequestMessage(HttpMethod.Put, prefix + "/heartbeat") { Content = content };
                        using var response = await client.SendAsync(heartbeat, HttpCompletionOption.ResponseHeadersRead, reportToken).ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.Conflict) lease = null;
                        response.EnsureSuccessStatusCode();
                        leaseSource.Publish(lease);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or JsonException or DbException || error is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        leaseSource.Publish(null);
                        context.Logger.LogWarning("Cluster node {NodeId} report failed ({FailureType}); lease expiry removes stale placement and rooms.", options.NodeId, error.GetType().Name);
                    }
                } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                leaseSource.Publish(null);
                if (lease is not null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, prefix + "/lease") { Content = JsonContent.Create(new NodeRelease(lease.Token)) };
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                    try
                    {
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                    }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                    { context.Logger.LogWarning("Cluster node {NodeId} release failed ({FailureType}); the lease will expire.", options.NodeId, error.GetType().Name); }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
