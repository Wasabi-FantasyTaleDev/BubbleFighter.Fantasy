namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>Redeems short-lived launcher tickets for a specific client build.</summary>
public interface ILauncherTicketService
{
    /// <summary>Consumes a valid ticket exactly once, returning its account name; mismatched or expired tickets return null.</summary>
    string? Redeem(string ticket, string build);

    /// <summary>Consumes a valid build-bound ticket with its user-selected UI language; older providers return an unspecified language.</summary>
    LauncherTicketIdentity? RedeemIdentity(string ticket, string build) =>
        Redeem(ticket, build) is { } username ? new(username) : null;
}

/// <summary>The account, optional user UI language and optional launch authenticated by one launch ticket.</summary>
/// <param name="Username">Account that authorized the ticket.</param>
/// <param name="LauncherLanguage">Launcher UI language, independent of the native client and its region.</param>
/// <param name="LaunchId">The launch the ticket was issued for, when a launch-scoped service such as the companion opened one.</param>
public sealed record LauncherTicketIdentity(string Username, string? LauncherLanguage = null, string? LaunchId = null);
