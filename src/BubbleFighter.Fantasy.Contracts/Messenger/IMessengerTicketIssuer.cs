namespace BubbleFighter.Fantasy.Contracts.Messenger;

/// <summary>
/// Signs short-lived messenger tickets for this server's accounts. A game server hands the ticket to its client,
/// which exchanges it with a messenger that trusts this tenant's public key, in-process or on another host.
/// </summary>
public interface IMessengerTicketIssuer
{
    /// <summary>Gets the tenant key the tickets assert.</summary>
    string Tenant { get; }

    /// <summary>Gets the public base URL clients use to reach the messenger, with <c>{host}</c> standing for the host the client used to reach this server.</summary>
    string PublicUrl { get; }

    /// <summary>Gets the DER-encoded SubjectPublicKeyInfo that verifies this issuer's tickets.</summary>
    ReadOnlyMemory<byte> PublicKey { get; }

    /// <summary>Signs a ticket for <paramref name="subject"/> playing <paramref name="build"/>, or returns null when the build has no messenger game code.</summary>
    /// <param name="subject">The tenant's stable account key.</param>
    /// <param name="nickname">The account's display nickname.</param>
    /// <param name="build">The client build being launched.</param>
    MessengerTicket? Issue(string subject, string nickname, string build);
}
