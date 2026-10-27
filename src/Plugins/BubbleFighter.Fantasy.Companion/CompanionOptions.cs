using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>Listener, launch table and per-connection bounds of the <c>companion</c> row.</summary>
public sealed class CompanionOptions : IValidatableOptions
{
    /// <summary>Gets or sets the WebSocket listener IP and port; terminate TLS in front of it for <c>wss</c>.</summary>
    public string Listen { get; set; } = "127.0.0.1:8092";

    /// <summary>
    /// Gets or sets the URL launchers connect to, <c>ws</c> or <c>wss</c> with the path <c>/companion/v1</c>; <c>{host}</c>
    /// stands for the host name the launcher used to reach the launcher API. Empty means plain <c>ws</c> on the listen port.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Gets the URL handed to launchers: <see cref="PublicUrl"/>, or the listen port on the launcher's host.</summary>
    public string EffectivePublicUrl => string.IsNullOrEmpty(PublicUrl) && IPEndPoint.TryParse(Listen ?? "", out var endpoint)
        ? $"ws://{{host}}:{endpoint.Port}{CompanionServer.Path}" : PublicUrl ?? "";

    /// <summary>Gets or sets how long a one-time connection ticket is accepted, in seconds.</summary>
    [Range(10, 3600)] public int TicketSeconds { get; set; } = 120;

    /// <summary>Gets or sets how long a launch waits for its game login before it is dropped, in seconds.</summary>
    [Range(30, 86400)] public int PendingSeconds { get; set; } = 900;

    /// <summary>Gets or sets the bound on open launches, pending and bound together.</summary>
    [Range(1, 1000000)] public int MaxLaunches { get; set; } = 4096;

    /// <summary>Gets or sets the bound on concurrent companion connections.</summary>
    [Range(1, 100000)] public int MaxConnections { get; set; } = 2048;

    /// <summary>Gets or sets the largest reassembled message in bytes; it matches the launcher's own bound.</summary>
    [Range(256, 1048576)] public int MessageBytes { get; set; } = 64 * 1024;

    /// <summary>Gets or sets the deepest JSON nesting accepted, including the envelope.</summary>
    [Range(4, 64)] public int JsonDepth { get; set; } = 16;

    /// <summary>Gets or sets the most fragments one message may arrive in.</summary>
    [Range(1, 65536)] public int MessageFragments { get; set; } = 4096;

    /// <summary>Gets or sets the most queued outbound messages per connection.</summary>
    [Range(1, 1024)] public int QueueMessages { get; set; } = 128;

    /// <summary>Gets or sets the most queued outbound bytes per connection.</summary>
    [Range(256, 16777216)] public int QueueBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the most received messages per connection in a one-second window, controls included.</summary>
    [Range(1, 4096)] public int MessagesPerSecond { get; set; } = 128;

    /// <summary>Gets or sets the most client requests handled at once per connection.</summary>
    [Range(1, 256)] public int MaxConcurrentRequests { get; set; } = 16;

    /// <summary>Gets or sets the time allowed between the upgrade and the client's hello, in seconds.</summary>
    [Range(1, 300)] public int HandshakeSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets how long a connection may stay silent before it is closed, in seconds. The launcher probes every
    /// 20 seconds, so this leaves room for one lost probe.
    /// </summary>
    [Range(30, 600)] public int IdleSeconds { get; set; } = 60;

    /// <summary>Gets or sets the graceful unload timeout in seconds.</summary>
    [Range(1, 60)] public int ShutdownSeconds { get; set; } = 5;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!IPEndPoint.TryParse(Listen ?? "", out _)) yield return new("listen must be an IP address and port.", [nameof(Listen)]);
        if (!Uri.TryCreate(EffectivePublicUrl.Replace("{host}", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var url)
            || url.Scheme is not ("ws" or "wss") || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
            || url.AbsolutePath != CompanionServer.Path)
            yield return new($"public_url must be a ws or wss URL whose path is {CompanionServer.Path}, without query or fragment.", [nameof(PublicUrl)]);
        if (QueueBytes < MessageBytes) yield return new("queue_bytes must hold at least one message of message_bytes.", [nameof(QueueBytes)]);
    }
}
