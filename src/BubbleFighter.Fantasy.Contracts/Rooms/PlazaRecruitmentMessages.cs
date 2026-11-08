using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>The server-owned recruitment presentation of one installed plaza member.</summary>
/// <param name="Allowed">Whether this member leads their party or is currently alone.</param>
/// <param name="Active">Whether this leader is displaying a recruitment sign.</param>
/// <param name="Text">The bounded recruitment message shown beside the sign.</param>
public sealed record PlazaRecruitmentState(bool Allowed, bool Active, string Text)
{
    /// <summary>Gets the accepted member limit, including the recruiting leader.</summary>
    public int MemberLimit { get; init; }

    /// <summary>Gets the native recruitment category selected by the document control.</summary>
    public byte Category { get; init; } = 1;
}

/// <summary>A plaza leader starts or cancels recruitment through the build's native control.</summary>
/// <param name="Active">Whether to display the recruitment sign.</param>
/// <param name="Text">An optional message carried by the same request; older controls send it separately.</param>
public sealed record PlazaRecruitmentRequested(bool Active, string? Text = null) : IMessage
{
    /// <summary>Gets the requested party capacity when the native document includes that control.</summary>
    public int? MemberLimit { get; init; }

    /// <summary>Gets the native document category, or one for a simple plaza sign.</summary>
    public byte Category { get; init; } = 1;
}

/// <summary>Requests one page of currently active native recruitment documents.</summary>
/// <param name="Category">The native category filter; zero requests all categories.</param>
/// <param name="Page">The zero-based page index.</param>
public sealed record PlazaRecruitmentListRequested(byte Category, uint Page) : IMessage;

/// <summary>A currently recruiting local leader and the committed membership count.</summary>
/// <param name="Member">The leader's installed plaza identity and recruitment state.</param>
/// <param name="MemberCount">The number of committed party members, including the leader.</param>
public sealed record PlazaRecruitmentListing(PlazaMember Member, int MemberCount);

/// <summary>Answers a native recruitment page with bounded entries and the complete filtered count.</summary>
/// <param name="Entries">At most one native page of active leaders.</param>
/// <param name="Total">The number of active documents matching the filter.</param>
/// <param name="Page">The zero-based index of this page.</param>
public sealed record PlazaRecruitmentListReceived(IReadOnlyList<PlazaRecruitmentListing> Entries, int Total, uint Page) : IMessage;

/// <summary>Rejects a recruitment document that cannot be published by the current plaza leader.</summary>
public sealed record PlazaRecruitmentRejected : IMessage;

/// <summary>A plaza player supplies or repeats the text of their native recruitment sign.</summary>
/// <param name="Text">The recruitment message.</param>
public sealed record PlazaRecruitmentTextRequested(string Text) : IMessage;

/// <summary>Refreshes an existing plaza seat without recreating its actor or changing its position.</summary>
/// <param name="Member">The committed member and recruitment presentation.</param>
public sealed record PlazaRecruitmentChanged(PlazaMember Member) : IMessage;

/// <summary>Displays a recruitment message above the member identified by their native plaza seat.</summary>
/// <param name="Slot">The recipient's local plaza seat for the recruiting member.</param>
/// <param name="Text">The recruitment message.</param>
public sealed record PlazaRecruitmentTextReceived(int Slot, string Text) : IMessage;

/// <summary>A current game connection's server-owned recruitment admission state.</summary>
public interface IPlazaRecruitmentStatus
{
    /// <summary>Gets whether this connection is currently an installed plaza leader recruiting members.</summary>
    bool IsRecruiting { get; }

    /// <summary>Gets the accepted capacity of the currently recruiting party.</summary>
    int MemberLimit { get; }
}

/// <summary>Announces a completed party membership update so scene owners can refresh leader-only controls.</summary>
/// <param name="Protocol">The game protocol whose party membership changed.</param>
public sealed record PartyPlazaMembershipChanged(string Protocol) : IEvent
{
    /// <summary>Gets the members whose explicit departure also cancels their previous recruitment sign.</summary>
    public IReadOnlyList<int> DepartedUsers { get; init; } = [];
}
