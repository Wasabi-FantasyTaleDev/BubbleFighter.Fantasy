using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Party;

/// <summary>One party member as the party list shows it.</summary>
/// <param name="UserNum">Account id.</param>
/// <param name="Nickname">Display nickname.</param>
/// <param name="Level">The player's displayed level, used where the build's party record includes its rank.</param>
public sealed record PartyMember(int UserNum, string Nickname, int Level = 1);

/// <summary>
/// A party's members in order. The client takes the first member as the leader (kr-812 sub_926900 reads no leader
/// flag); a player outside any party is a party of one.
/// </summary>
/// <param name="Members">The members, leader first; never empty.</param>
public sealed record PartySnapshot(IReadOnlyList<PartyMember> Members)
{
    /// <summary>Gets the leader, the first member.</summary>
    public PartyMember Leader => Members[0];
}

/// <summary>The answer the inviter gets for an invitation (S2C gspRepInvite, kr-812 sub_81CDF0 codes).</summary>
public enum PartyInviteResult
{
    /// <summary>The invitation was delivered.</summary>
    Sent = 0,

    /// <summary>The player invited themselves.</summary>
    Self = 1,

    /// <summary>No player has that nickname.</summary>
    Unregistered = 2,

    /// <summary>The player is not logged in; also clears the inviter's pending row.</summary>
    Offline = 3,

    /// <summary>The player is in a round.</summary>
    Playing = 4,

    /// <summary>The player is already in the inviter's party.</summary>
    SameParty = 5,

    /// <summary>The party is full; also clears the inviter's pending row.</summary>
    PartyFull = 7,

    /// <summary>The player cannot be invited; also clears the inviter's pending row.</summary>
    Refused = 14,
}

/// <summary>The answer the leader gets for a kick (S2C gspRepKickFromParty, kr-812 sub_9285D0 codes).</summary>
public enum PartyKickResult
{
    /// <summary>The member was kicked.</summary>
    Kicked = 0,

    /// <summary>The member is not logged in.</summary>
    Offline = 1,

    /// <summary>The leader tried to kick themselves.</summary>
    Self = 2,

    /// <summary>The member already left.</summary>
    AlreadyLeft = 3,

    /// <summary>The member cannot be kicked (the requester is not the leader).</summary>
    NotAllowed = 4,
}

/// <summary>The answer to a leader handover (S2C gspRepChangePartyLeader, kr-812 sub_928C50 codes).</summary>
public enum PartyLeaderChangeResult
{
    /// <summary>The leadership was handed over.</summary>
    Changed = 0,

    /// <summary>The target is not in the party.</summary>
    NotMember = 1,

    /// <summary>The target already leads the party.</summary>
    AlreadyLeader = 2,

    /// <summary>Only the leader can hand over the leadership.</summary>
    NotLeader = 3,
}

/// <summary>A player invites another into their party or room (C2S gcpReqInvite).</summary>
/// <param name="IsParty">True for a party invitation, false for a room invitation.</param>
/// <param name="Target">The invited player's nickname.</param>
public sealed record PartyInviteRequested(bool IsParty, string Target) : IMessage;

/// <summary>The invited player accepts and moves to the inviter (C2S gcpReqMoveToInviter).</summary>
/// <param name="IsParty">The invitation's party flag, echoed by the client.</param>
/// <param name="Inviter">The inviter's nickname.</param>
public sealed record PartyInviteAccepted(bool IsParty, string Inviter) : IMessage;

/// <summary>The invited player answers without moving (C2S gcpReqMoveToFriendAccept; answer 3 refuses).</summary>
/// <param name="Inviter">The inviter's nickname.</param>
/// <param name="Answer">The client's answer code.</param>
public sealed record PartyInviteAnswered(string Inviter, int Answer) : IMessage;

/// <summary>A member leaves the party (C2S gcpReqLeaveParty).</summary>
public sealed record PartyLeaveRequested : IMessage;

/// <summary>The leader removes a member (C2S gcpReqKickFromParty).</summary>
/// <param name="Target">The member's nickname.</param>
public sealed record PartyKickRequested(string Target) : IMessage;

/// <summary>The leader hands the leadership to a member (C2S gcpReqChangePartyLeader).</summary>
/// <param name="Target">The member's nickname.</param>
public sealed record PartyLeaderChangeRequested(string Target) : IMessage;

/// <summary>The inviter's answer (S2C gspRepInvite).</summary>
/// <param name="Result">The result code.</param>
/// <param name="Target">The invited player's nickname.</param>
public sealed record PartyInviteReplied(PartyInviteResult Result, string Target) : IMessage;

/// <summary>
/// The invited player declines an invitation that the client identifies by the token of its prompt (the modern
/// family's refusal, which echoes a server-issued word instead of naming the inviter).
/// </summary>
/// <param name="Token">The <see cref="PartyInviteNotified.Token"/> of the prompt being declined.</param>
public sealed record PartyInviteTokenRefused(uint Token) : IMessage;

/// <summary>The invited player's prompt (S2C gspNotifyInvite).</summary>
/// <param name="IsParty">True for a party invitation.</param>
/// <param name="Inviter">The inviter's nickname.</param>
/// <param name="Token">
/// A word the party row issues for this one pending invitation. Builds whose refusal does not name the inviter write it
/// into the prompt and read it back in <see cref="PartyInviteTokenRefused"/>; other builds ignore it. Zero means the
/// invitation carries none.
/// </param>
public sealed record PartyInviteNotified(bool IsParty, string Inviter, uint Token = 0) : IMessage;

/// <summary>
/// Why a visit cannot go ahead. The values are the server's own; each build's dialect writes its client's code (the
/// legacy gspRepMoveToFriend texts of kr-812, the modern party/VisitFailed texts of kr-1256 sub_9AA990).
/// </summary>
public enum PartyVisitFailure
{
    /// <summary>The player asked to visit themselves.</summary>
    Self,

    /// <summary>No player has that nickname.</summary>
    Unregistered,

    /// <summary>The player is not logged in.</summary>
    Offline,

    /// <summary>The visited player is in a round.</summary>
    TargetPlaying,

    /// <summary>The visitor is in a round.</summary>
    VisitorPlaying,

    /// <summary>The two players are already in the same party.</summary>
    SameParty,

    /// <summary>The visited player's party is full.</summary>
    PartyFull,

    /// <summary>The visited player declined.</summary>
    Declined,

    /// <summary>The visit cannot be served (the visitor already has a party, or the server is out of capacity).</summary>
    Unavailable,
}

/// <summary>A player asks to visit a friend (C2S gcpReqMoveToFriend, kr-812 sender sub_92A050).</summary>
/// <param name="Target">The visited player's nickname.</param>
/// <param name="RequiresApproval">Whether this build has a visited-player prompt and its answer packet.</param>
public sealed record PartyVisitRequested(string Target, bool RequiresApproval = true) : IMessage;

/// <summary>
/// The visited player answers a visit prompt that names the visitor (kr-812 gcpReqMoveToFriendAccept with answer 1
/// accept, 0 decline or 2 declined by the player's option: the result 17 branch of kr-812 sub_81ED40).
/// </summary>
/// <param name="Visitor">The visitor's nickname.</param>
/// <param name="Accepted">Whether the visit was accepted.</param>
public sealed record PartyVisitAnswered(string Visitor, bool Accepted) : IMessage;

/// <summary>
/// The visited player answers a visit prompt by its token (the modern family: kr-1256 sub_9D7890 accepts,
/// kr-1256 sub_9D77E0 declines).
/// </summary>
/// <param name="Token">The <see cref="PartyVisitAsked.Token"/> of the prompt.</param>
/// <param name="Accepted">Whether the visit was accepted.</param>
public sealed record PartyVisitTokenAnswered(uint Token, bool Accepted) : IMessage;

/// <summary>The visited player's prompt (kr-812 gspRepMoveToFriend result 17, kr-1256 gspAskedVisitation).</summary>
/// <param name="Visitor">The visitor's nickname.</param>
/// <param name="Token">The word the modern prompt carries and its answer echoes; legacy builds ignore it.</param>
public sealed record PartyVisitAsked(string Visitor, uint Token) : IMessage;

/// <summary>The visitor learns that the visit cannot go ahead.</summary>
/// <param name="Reason">Why.</param>
/// <param name="Target">The visited player's nickname.</param>
public sealed record PartyVisitFailed(PartyVisitFailure Reason, string Target) : IMessage;

/// <summary>
/// The visitor's client is told to move to the visited player, which it answers with its own C2S gcpReqMoveToInviter
/// (kr-812 sub_81DEA0 on a notice that skips the prompt, kr-1256 sub_9A9710 on gspVisitationAccepted).
/// </summary>
/// <param name="Host">The visited player's nickname.</param>
public sealed record PartyVisitGranted(string Host) : IMessage;

/// <summary>The party list of one member (S2C gspUpdateParty).</summary>
/// <param name="Party">The member's party; a party of one when the member is alone.</param>
/// <param name="Appearance">An optional equipped appearance refreshed when the recipient changes scenes.</param>
public sealed record PartyUpdated(PartySnapshot Party, PartyAppearance? Appearance = null) : IMessage;

/// <summary>A party member's committed appearance supplied to a scene refresh.</summary>
/// <param name="UserNum">The member whose client profile is refreshed.</param>
/// <param name="CharacterItemId">The resolved native character item.</param>
/// <param name="EquippedItemIds">The committed worn items, including the character when present.</param>
public sealed record PartyAppearance(int UserNum, int CharacterItemId, IReadOnlyList<int> EquippedItemIds);

/// <summary>A new leader after the old one left (S2C gspUpdatePartyLeader).</summary>
/// <param name="Left">The nickname of the member who left.</param>
/// <param name="Leader">The new leader's nickname.</param>
public sealed record PartyLeaderReplaced(string Left, string Leader) : IMessage;

/// <summary>The leaver's own confirmation (S2C gspNotifyPartyLeaveSuccess).</summary>
public sealed record PartyLeft : IMessage;

/// <summary>A member left (S2C gspNotifyMemberLeave).</summary>
/// <param name="Nickname">The member who left.</param>
public sealed record PartyMemberLeft(string Nickname) : IMessage;

/// <summary>A member joined (S2C gspNotifyNewMemberJoin); the joiner gets it with its own nickname.</summary>
/// <param name="Nickname">The member who joined.</param>
public sealed record PartyMemberJoined(string Nickname) : IMessage;

/// <summary>The leader's kick answer (S2C gspRepKickFromParty).</summary>
/// <param name="Result">The result code.</param>
/// <param name="Target">The member's nickname.</param>
public sealed record PartyKickReplied(PartyKickResult Result, string Target) : IMessage;

/// <summary>The kicked member's notice (S2C gspNotifyKickFromParty).</summary>
/// <param name="Leader">The leader who kicked.</param>
public sealed record PartyKicked(string Leader) : IMessage;

/// <summary>The other members' kick notice (S2C gspNotifyKickMember).</summary>
/// <param name="Leader">The leader who kicked.</param>
/// <param name="Kicked">The member who was kicked.</param>
public sealed record PartyMemberKicked(string Leader, string Kicked) : IMessage;

/// <summary>A handover answer (S2C gspRepChangePartyLeader), sent to every member.</summary>
/// <param name="Previous">The previous leader's nickname.</param>
/// <param name="Leader">The target's nickname.</param>
/// <param name="Result">The result code.</param>
public sealed record PartyLeaderChanged(string Previous, string Leader, PartyLeaderChangeResult Result) : IMessage;

/// <summary>The party of each logged-in player, read by the room flow to bring members along with their leader.</summary>
public interface IPartyDirectory
{
    /// <summary>Gets the party of a player.</summary>
    /// <param name="userNum">The account id.</param>
    /// <returns>The party, or <see langword="null"/> when the player is alone.</returns>
    PartySnapshot? PartyOf(int userNum);
}

/// <summary>
/// A session feature on every party member's game connection: the party as last sent to that member. Scene codecs
/// that rewrite S2C gspUpdateParty (the main-menu install) read it, so a member who returns to the menu keeps its party
/// list instead of a party of one.
/// </summary>
public interface IPartyMembership
{
    /// <summary>Gets the member's party, leader first.</summary>
    PartySnapshot Party { get; }
}
