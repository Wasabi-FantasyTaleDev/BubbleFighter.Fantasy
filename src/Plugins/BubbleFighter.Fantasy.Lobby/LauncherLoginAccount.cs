using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>Resolves explicitly marked launcher credentials before the shared account lookup or logging.</summary>
internal static class LauncherLoginAccount
{
    // Application transport marker, not a native field meaning. cn-406 sub_577D00
    // copies callback+8 to the first wstr serialized by cn-406 sub_576510.
    internal const string Prefix = "bff-ticket:";

    // A launch ticket is 64 lowercase hex digits. One that reaches the login without the marker (a launcher that
    // passed it bare) is still a ticket, never an account name: an unredeemable one is refused rather than becoming
    // a new player named after it.
    private const int TicketLength = 64;

    /// <summary>
    /// Returns the account a login may use, or null when it is refused. With <paramref name="requireTicket"/> set
    /// (authentication: ticket) every login must redeem a ticket; otherwise a plain account name passes unchanged.
    /// </summary>
    internal static string? Resolve(string account, string build, bool enabled, ILauncherTicketService? tickets,
        bool requireTicket = false) => ResolveIdentity(account, build, enabled, tickets, requireTicket)?.Username;

    internal static LauncherTicketIdentity? ResolveIdentity(string account, string build, bool enabled, ILauncherTicketService? tickets,
        bool requireTicket = false)
    {
        var marked = account.StartsWith(Prefix, StringComparison.Ordinal);
        if (!marked && !IsTicket(account)) return requireTicket ? null : new(account);
        var token = marked ? account.AsSpan(Prefix.Length) : account.AsSpan();
        if (!enabled || tickets is null || !IsTicket(token)) return null;
        return tickets.RedeemIdentity(token.ToString(), build);
    }

    /// <summary>Whether the login account is shaped like a launch ticket, marked or bare; redemption decides the rest.</summary>
    internal static bool IsTicketForm(string account) =>
        account.StartsWith(Prefix, StringComparison.Ordinal) || IsTicket(account);

    private static bool IsTicket(ReadOnlySpan<char> token)
    {
        if (token.Length != TicketLength) return false;
        foreach (var value in token)
            if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }
}
