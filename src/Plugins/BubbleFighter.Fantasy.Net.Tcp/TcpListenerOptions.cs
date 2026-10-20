using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Net.Tcp;

/// <summary>Configuration for a <see cref="TcpListenerPlugin"/> row.</summary>
public sealed class TcpListenerOptions : IValidatableOptions
{
    /// <summary>Gets or sets the bind address as <c>host:port</c>, for example <c>0.0.0.0:7100</c>.</summary>
    [Required]
    public string Listen { get; set; } = "0.0.0.0:7100";

    /// <summary>Gets or sets the connection protocol name connections are handed to.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the maximum number of simultaneous connections; excess accepts are rejected.</summary>
    [Range(1, 1_000_000)]
    public int MaxConnections { get; set; } = 512;

    /// <summary>Gets or sets the listen backlog.</summary>
    [Range(1, 65535)]
    public int Backlog { get; set; } = 128;

    /// <summary>Gets or sets whether TCP_NODELAY is set on accepted sockets.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Gets or sets whether the operating system probes silent TCP peers and closes connections that stop acknowledging them.</summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>Gets or sets the seconds without TCP activity before the first keepalive probe. This is server policy, independent of game packets.</summary>
    [Range(1, 86_400)]
    public int KeepAliveTimeSeconds { get; set; } = 60;

    /// <summary>Gets or sets the seconds between unanswered TCP keepalive probes.</summary>
    [Range(1, 86_400)]
    public int KeepAliveIntervalSeconds { get; set; } = 15;

    /// <summary>Gets or sets the maximum unanswered TCP keepalive probes before the operating system reports a failed connection.</summary>
    [Range(1, 255)]
    public int KeepAliveRetryCount { get; set; } = 4;

    /// <summary>Gets or sets an optional silence limit in seconds. The default of 0 keeps idle game sessions connected; a positive value enables transport-level expiry.</summary>
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!TryParseEndPoint(Listen, out _, out var error))
            yield return new ValidationResult($"listen: {error}", [nameof(Listen)]);
        if (string.IsNullOrWhiteSpace(Protocol))
            yield return new ValidationResult("protocol must name a registered connection protocol", [nameof(Protocol)]);
    }

    /// <summary>Parses a <c>host:port</c> endpoint, resolving host names.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="endPoint">Receives the parsed endpoint.</param>
    /// <param name="error">Receives a description on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryParseEndPoint(string text, out IPEndPoint? endPoint, out string? error)
    {
        endPoint = null;
        error = null;
        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            error = "expected 'host:port'";
            return false;
        }
        var host = text[..separator].Trim('[', ']');
        if (!int.TryParse(text[(separator + 1)..], out var port) || port is < 0 or > 65535)
        {
            error = "invalid port";
            return false;
        }
        if (IPAddress.TryParse(host, out var address))
        {
            endPoint = new IPEndPoint(address, port);
            return true;
        }
        var addresses = Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            error = $"cannot resolve host '{host}'";
            return false;
        }
        endPoint = new IPEndPoint(addresses[0], port);
        return true;
    }
}
