using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Messenger.Http;

/// <summary>Listener and request bounds of the <c>messenger.http</c> row.</summary>
public sealed class MessengerHttpOptions : IValidatableOptions
{
    /// <summary>Gets or sets the HTTP listener IP and port; use HTTPS termination for remote clients.</summary>
    public string Listen { get; set; } = "127.0.0.1:8091";

    /// <summary>Gets or sets the maximum incoming JSON body bytes.</summary>
    [Range(512, 65536)] public int MaxBodyBytes { get; set; } = 8192;

    /// <summary>Gets or sets the bound on accepted HTTP connections, including waiting event polls.</summary>
    [Range(1, 100000)] public int MaxConnections { get; set; } = 1024;

    /// <summary>Gets or sets concurrent non-polling requests.</summary>
    [Range(1, 1024)] public int MaxConcurrentRequests { get; set; } = 32;

    /// <summary>Gets or sets concurrent waiting event polls.</summary>
    [Range(1, 100000)] public int MaxWaitingPolls { get; set; } = 1024;

    /// <summary>Gets or sets the longest an event poll waits for news, in seconds.</summary>
    [Range(0, 60)] public int MaxWaitSeconds { get; set; } = 25;

    /// <summary>Gets or sets the request budget per transport peer IP in a one-minute window.</summary>
    [Range(1, 100000)] public int RequestsPerMinute { get; set; } = 600;

    /// <summary>Gets or sets retained IP request windows; the oldest window is evicted at capacity.</summary>
    [Range(1, 100000)] public int MaxRateLimitAddresses { get; set; } = 4096;

    /// <summary>Gets or sets the HTTP header and keep-alive timeout in seconds; it must outlast an event poll.</summary>
    [Range(1, 300)] public int HttpTimeoutSeconds { get; set; } = 40;

    /// <summary>Gets or sets the graceful unload timeout in seconds.</summary>
    [Range(1, 60)] public int ShutdownSeconds { get; set; } = 5;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!IPEndPoint.TryParse(Listen ?? "", out _)) yield return new("listen must be an IP address and port.", [nameof(Listen)]);
        if (HttpTimeoutSeconds <= MaxWaitSeconds) yield return new("http_timeout_seconds must be longer than max_wait_seconds.", [nameof(HttpTimeoutSeconds)]);
    }
}
