using System.ComponentModel.DataAnnotations;
using System.Net;

namespace BubbleFighter.Fantasy.Net.Udp;

/// <summary>Configuration for one UDP datagram listener row.</summary>
public sealed class UdpListenerOptions : IValidatableObject
{
    /// <summary>Gets or sets whether this row opens a UDP socket; disabling it leaves TCP listeners unchanged.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the protocol name associated with datagrams received by this listener.</summary>
    [Required]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>Gets or sets the IPv4 address and UDP port to bind.</summary>
    [Required]
    public string Listen { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum accepted datagram size.</summary>
    [Range(64, 65507)]
    public int MaxDatagramBytes { get; set; } = 1400;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Protocol) || Protocol.Any(char.IsWhiteSpace))
            yield return new ValidationResult("protocol must be a non-empty token without whitespace.", [nameof(Protocol)]);
        if (!TryParseEndPoint(Listen, out _, out var error))
            yield return new ValidationResult($"listen: {error}", [nameof(Listen)]);
    }

    internal static bool TryParseEndPoint(string? text, out IPEndPoint? endpoint, out string error)
    {
        endpoint = null;
        error = "must be an IPv4 address and port, such as 0.0.0.0:7200";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var separator = text.LastIndexOf(':');
        if (separator <= 0 || !IPAddress.TryParse(text[..separator], out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(text[(separator + 1)..], out var port) || port is < 0 or > 65535)
            return false;
        endpoint = new IPEndPoint(address, port);
        error = string.Empty;
        return true;
    }
}
