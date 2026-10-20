namespace BubbleFighter.Fantasy.Contracts.Admin;

/// <summary>Separate administration identities and expiring sessions, independent of game and launcher accounts.</summary>
public interface IAdminAccessControl
{
    /// <summary>Gets whether a durable candidate has already claimed the administrator identity.</summary>
    bool HasAdministrator { get; }
    /// <summary>Checks a configured remote bootstrap secret without returning or logging it.</summary>
    bool AcceptsBootstrapToken(string? token);
    /// <summary>Gets whether non-loopback installation was explicitly enabled with a bootstrap secret.</summary>
    bool AllowsRemoteBootstrap { get; }
    /// <summary>Checks whether a session is live, unexpired and owned by the current activation.</summary>
    bool Authorize(string? session);
    /// <summary>Checks a password under bounded rate/concurrency limits and returns a new session on success.</summary>
    ValueTask<AdminSession?> SignInAsync(string username, string password, CancellationToken cancellationToken);
    /// <summary>Checks administrator credentials with failed attempts isolated to the transport peer address.</summary>
    /// <param name="username">The administrator account name.</param>
    /// <param name="password">The supplied password; never logged or retained.</param>
    /// <param name="sourceAddress">The connection peer's IP address text, without trusting forwarding headers.</param>
    /// <param name="cancellationToken">Cancels pending authentication work.</param>
    /// <returns>A new administration session, or null when the credentials do not match.</returns>
    ValueTask<AdminSession?> SignInAsync(string username, string password, string sourceAddress, CancellationToken cancellationToken);
    /// <summary>Immediately invalidates the supplied session; unknown sessions have no effect.</summary>
    void SignOut(string? session);
}

/// <summary>A newly issued opaque session; the HTTP adapter must put its token in an HttpOnly cookie.</summary>
/// <param name="Token">A cryptographically random session secret.</param>
/// <param name="ExpiresUtc">Absolute expiry; a restart invalidates all sessions.</param>
public sealed record AdminSession(string Token, DateTimeOffset ExpiresUtc);
