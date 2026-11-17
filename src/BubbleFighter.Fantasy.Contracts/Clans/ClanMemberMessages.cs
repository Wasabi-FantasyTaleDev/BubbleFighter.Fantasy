using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The role and event words the clan's member notices carry in their role field (kr-1256 sub_51D330 for the player
/// it is about, kr-1256 sub_51EA80 for everyone else). The seated roles are <see cref="ClanRole"/>; these negative
/// words say how a row ended.
/// </summary>
public static class ClanMemberEvent
{
    /// <summary>An application was turned away ("your application to %s was rejected").</summary>
    public const int Rejected = -10;

    /// <summary>A member left the clan ("you left %s").</summary>
    public const int Left = -20;

    /// <summary>A member was removed ("you were removed from %s").</summary>
    public const int Removed = -30;

    /// <summary>
    /// The application was withdrawn before the master answered it: the change result's notice "%s cancelled the
    /// application" (kr-1256 sub_51E2C0).
    /// </summary>
    public const int Withdrawn = -50;
}

/// <summary>
/// A master's or deputy's change to another row (clan C2S <c>ClanMemberChange</c>, writer kr-1256 sub_527B80). The
/// senders pass the row's stored role as <see cref="OldRole"/> and one of 100 (approve or make a plain member), 102
/// (make deputy), -10 (turn the applicant away) or -30 (remove) as <see cref="NewRole"/>. Answered with
/// <see cref="ClanMemberChangeAnswered"/>.
/// </summary>
/// <param name="UserNum">The row's player id.</param>
/// <param name="Nickname">The row's nickname as the sender's list shows it.</param>
/// <param name="OldRole">The role the sender's list shows; the server reads the stored role instead.</param>
/// <param name="NewRole">The requested role or event word.</param>
public sealed record ClanMemberChangeRequested(int UserNum, string Nickname, int OldRole, int NewRole) : IMessage;

/// <summary>
/// The answer to <see cref="ClanMemberChangeRequested"/> (clan S2C <c>ClanMemberChangeResult</c>, reader kr-1256
/// sub_52D090, consumer kr-1256 sub_51E2C0).
/// </summary>
/// <param name="Result"><see cref="Changed"/>, <see cref="Full"/>, <see cref="DeputyTaken"/>, or another value for the general failure.</param>
/// <param name="Nickname">The row's nickname, shown in the notice.</param>
/// <param name="Role">The role or event word that was applied.</param>
/// <param name="Joined">1 when an applicant was seated, else 0.</param>
public sealed record ClanMemberChangeAnswered(int Result, string Nickname, int Role, int Joined) : IMessage
{
    /// <summary>The change was made.</summary>
    public const int Changed = 1;

    /// <summary>The clan has no free seat ("the maximum number of members was exceeded").</summary>
    public const int Full = 2;

    /// <summary>The clan already has a deputy.</summary>
    public const int DeputyTaken = -102;

    /// <summary>A value with no notice of its own: the client shows its general failure text.</summary>
    public const int Failed = 0;
}

/// <summary>
/// A change to one row, told to the clan's members (clan S2C <c>ClanMemberChanged</c>, reader kr-1256 sub_52CE70).
/// The player it is about stores <see cref="Role"/> as its own role and, for a seat or a removal, asks for its home.
/// </summary>
/// <param name="ClanName">The clan's name.</param>
/// <param name="UserNum">The row's player id.</param>
/// <param name="Nickname">The row's nickname.</param>
/// <param name="Role">The new role, or a <see cref="ClanMemberEvent"/> word.</param>
/// <param name="Joined">1 when an applicant was seated, else 0.</param>
public sealed record ClanMemberChanged(string ClanName, long UserNum, string Nickname, int Role, int Joined) : IMessage;

/// <summary>A member's request to leave the own clan (clan C2S <c>ClanLeave</c>, writer kr-1256 sub_527DC0, empty body).</summary>
public sealed record ClanLeaveRequested : IMessage;

/// <summary>
/// A member left, told to the clan's members and the member who left (clan S2C <c>ClanMemberLeft</c>, reader
/// kr-1256 sub_52E640). Its last two words reach the same consumers as <see cref="ClanMemberChanged"/> in swapped
/// order.
/// </summary>
/// <param name="ClanName">The clan's name.</param>
/// <param name="UserNum">The leaver's player id.</param>
/// <param name="Nickname">The leaver's nickname.</param>
/// <param name="Unknown3">The fourth field, which the consumers take as their joined flag and do not read for a leave; the server writes 0.</param>
/// <param name="Event">The event word, <see cref="ClanMemberEvent.Left"/>.</param>
public sealed record ClanMemberLeft(string ClanName, long UserNum, string Nickname, int Unknown3, int Event) : IMessage;
