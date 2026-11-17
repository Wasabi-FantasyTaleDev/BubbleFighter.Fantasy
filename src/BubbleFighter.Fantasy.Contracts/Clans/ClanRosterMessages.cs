using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The member list request (clan C2S <c>ClanRosterRequest</c>, empty body, kr-1256 sub_527890). It is answered with
/// <see cref="ClanRosterSent"/>.
/// </summary>
public sealed record ClanRosterRequested : IMessage;

/// <summary>
/// The detailed member list request (clan C2S <c>ClanMemberDetailsRequest</c>, kr-1256 sub_527910). It is answered
/// with <see cref="ClanMemberDetailsSent"/>.
/// </summary>
/// <param name="Unknown0">The request's one word; the client always writes 1.</param>
public sealed record ClanMemberDetailsRequested(int Unknown0) : IMessage;

/// <summary>
/// Two words the client reports about itself (clan C2S <c>ClanMemberReport</c>, writer kr-1256 sub_527AB0, caller
/// kr-1256 sub_517E70). The client puts the same two words into its own roster row, so the server keeps the last
/// report of each connected member and echoes it in <see cref="ClanRosterSent"/> and <see cref="ClanMemberDetailsSent"/>.
/// </summary>
/// <param name="Unknown0">The local player's word at kr-1256 dword_18E56E8 +60.</param>
/// <param name="Unknown4">The word at kr-1256 dword_18E56A4 +72.</param>
public sealed record ClanMemberReported(int Unknown0, int Unknown4) : IMessage;

/// <summary>One row of <see cref="ClanRosterSent"/>.</summary>
/// <param name="UserNum">The member's player id.</param>
/// <param name="Nickname">The member's nickname.</param>
/// <param name="Reported">The member's last <see cref="ClanMemberReported"/>; null when the member has no live clan connection here.</param>
/// <param name="Role">The member's role word, one of <see cref="ClanRole"/>.</param>
public sealed record ClanRosterRow(long UserNum, string Nickname, ClanMemberReported? Reported, int Role);

/// <summary>
/// The member list of the player's own clan (clan S2C <c>ClanRoster</c>, handler kr-1256 sub_52B4B0); empty for a
/// player without a seat.
/// </summary>
/// <param name="Members">The seated members, in join order.</param>
public sealed record ClanRosterSent(IReadOnlyList<ClanRosterRow> Members) : IMessage;

/// <summary>
/// The detailed member list of the player's own clan (clan S2C <c>ClanMemberDetails</c>, handler kr-1256 sub_52C010);
/// empty for a player without a seat. Its words after the role are not established and are sent as 0.
/// </summary>
/// <param name="Members">The seated members, in join order.</param>
public sealed record ClanMemberDetailsSent(IReadOnlyList<ClanRosterRow> Members) : IMessage;
