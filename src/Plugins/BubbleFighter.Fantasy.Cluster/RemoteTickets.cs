using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Companion;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class RemoteTickets(TicketClientOptions options, HttpClient http) : ILauncherTicketService, IDisposable
{
    private readonly SemaphoreSlim _workers = new(options.MaxConcurrentRequests);
    public string? Redeem(string ticket, string build) => throw new NotSupportedException("Remote ticket redemption requires RedeemIdentityAsync.");

    public async ValueTask<LauncherTicketIdentity?> RedeemIdentityAsync(string ticket, string build, CancellationToken cancellationToken)
    {
        if (!TicketValidation.Token(ticket) || !options.Builds.Contains(build, StringComparer.Ordinal)) return null;
        if (!await _workers.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new HttpRequestException("Ticket authority request capacity reached.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/nodes/" + Uri.EscapeDataString(options.NodeId) + "/tickets/redeem")
            { Content = JsonContent.Create(new TicketRedemption(ticket, build)) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(options.MaxBodyBytes, deadline.Token).ConfigureAwait(false);
            var identity = await response.Content.ReadFromJsonAsync<LauncherTicketIdentity>(TicketAuthority.JsonOptions, deadline.Token).ConfigureAwait(false);
            if (identity is null || string.IsNullOrWhiteSpace(identity.Username) || identity.Username.Length > 256
                || identity.Username.Any(char.IsControl) || identity.LauncherLanguage is not (null or "zh-CN" or "zh-TW" or "ko" or "en" or "id" or "vi")
                || identity.LaunchId is { } launch && (launch.Length != 32 || launch.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
                throw new InvalidDataException("The ticket authority returned an invalid identity.");
            return identity;
        }
        finally { _workers.Release(); }
    }

    public void Dispose() => _workers.Dispose();
}

/// <summary>Publishes asynchronous ticket redemption for a worker with an explicitly configured authority.</summary>
[Plugin("cluster.ticket-client", Category = "cluster", Names = ["en=Cluster ticket client", "zh-Hans=集群票据客户端", "zh-Hant=叢集票證用戶端", "ko=클러스터 티켓 클라이언트"], Description = "Redeems trusted authority tickets without sharing account credentials.",
    Provides = [typeof(ILauncherTicketService)], Optional = [typeof(ICompanionFederation)])]
public sealed class TicketClientPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<TicketClientOptions>();
        if (options.Companion)
        {
            if (!context.Services.TryGet<ICompanionFederation>(out var companion) || companion is null)
                throw new ConfigException("Remote companion launches require the companion row.");
            context.Effect(companion.TrustAuthority(options.Key, options.Builds));
        }
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = options.MaxConcurrentRequests })
        { BaseAddress = new(options.AuthorityUrl), Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        context.Effect(http);
        var service = new RemoteTickets(options, http);
        context.Effect(service);
        context.Effect(context.Services.Register<ILauncherTicketService>(service));
        return ValueTask.CompletedTask;
    }
}
