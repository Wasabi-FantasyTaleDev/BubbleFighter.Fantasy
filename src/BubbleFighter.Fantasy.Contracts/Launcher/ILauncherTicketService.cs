namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>Redeems short-lived launcher tickets for a specific client build.</summary>
public interface ILauncherTicketService
{
    /// <summary>Consumes a valid ticket exactly once, returning its account name; mismatched or expired tickets return null.</summary>
    string? Redeem(string ticket, string build);
}
