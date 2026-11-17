using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The master asks to disband the clan or to cancel a pending disband (clan C2S <c>ClanDisband</c>, writer kr-1256
/// sub_528190, sender kr-1256 sub_5BAC70). Answered with <see cref="ClanDisbandAnswered"/>.
/// </summary>
/// <param name="Kind"><see cref="Request"/> or <see cref="Cancel"/>.</param>
public sealed record ClanDisbandRequested(int Kind) : IMessage
{
    /// <summary>Schedule the disband.</summary>
    public const int Request = 1;

    /// <summary>Cancel the pending disband.</summary>
    public const int Cancel = 2;
}

/// <summary>
/// The answer to <see cref="ClanDisbandRequested"/> (clan S2C <c>ClanDisbandResult</c>, consumer kr-1256 sub_520680):
/// any result but 1 shows the failure notice of its kind.
/// </summary>
/// <param name="Result">1 when the request was applied, else 0.</param>
/// <param name="Kind">The request's kind.</param>
public sealed record ClanDisbandAnswered(int Result, int Kind) : IMessage;

/// <summary>
/// A disband was scheduled or cancelled, told to every member (clan S2C <c>ClanDisbandChanged</c>, reader kr-1256
/// sub_52E860).
/// </summary>
/// <param name="Unknown0">The word the reader requires to be 1 before it acts; the server writes 1.</param>
/// <param name="Kind"><see cref="ClanDisbandRequested.Request"/> or <see cref="ClanDisbandRequested.Cancel"/>.</param>
public sealed record ClanDisbandChanged(int Unknown0, int Kind) : IMessage;
