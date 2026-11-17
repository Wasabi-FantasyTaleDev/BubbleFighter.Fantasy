using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Couples;

/// <summary>
/// A proposal (C2S <c>gcpReqCoupleConfirmInvite</c>; kr-1256 <c>sub_E880A0</c>): the yes/no answer of the propose
/// box, the fighter proposed to, the couple ring the invite dialog holds and the dialog's message. A cleared
/// <paramref name="Confirmed"/> withdraws the sender's pending proposal.
/// </summary>
/// <param name="Confirmed">Whether the sender answered Yes.</param>
/// <param name="Target">The proposed-to fighter's nickname, as typed or clicked; resolved by the server.</param>
/// <param name="RingItemId">The ring item the proposal uses; the server requires the sender to own it.</param>
/// <param name="Message">The proposal text.</param>
public sealed record CoupleProposed(bool Confirmed, string Target, int RingItemId, string Message) : IMessage;

/// <summary>A proposal the recipient let lapse (C2S <c>gcpReqCoupleInviteExpire</c>, one flag).</summary>
/// <param name="Value">The client's flag word, kept as read.</param>
public sealed record CoupleProposalLapsed(bool Value) : IMessage;

/// <summary>The recipient's answer (C2S <c>gcpReqCoupleInviteAnswer</c>; kr-1256 <c>sub_E881A0</c>).</summary>
/// <param name="Accepted">Whether the recipient accepted.</param>
/// <param name="Inviter">The proposer's nickname as the dialog shows it.</param>
/// <param name="Recipient">The recipient's own nickname as the dialog shows it.</param>
public sealed record CoupleAnswered(bool Accepted, string Inviter, string Recipient) : IMessage;

/// <summary>A breakup (C2S <c>gcpReqCoupleBreakUp</c>; kr-1256 <c>sub_E86B50</c>), naming the partner.</summary>
/// <param name="Partner">The partner's nickname as the client shows it.</param>
public sealed record CoupleBreakUpRequested(string Partner) : IMessage;

/// <summary>A couple ring change (C2S <c>gcpReqChangeCouplering</c>; kr-1256 <c>sub_EA0430</c>).</summary>
/// <param name="RingItemId">The ring item to show from now on; the server requires the sender to own it.</param>
public sealed record CoupleRingChangeRequested(int RingItemId) : IMessage;

/// <summary>Why a proposal was not delivered, or that it was; the build's data maps each to its client code.</summary>
public enum CoupleProposalResult
{
    /// <summary>The proposal reached the recipient (kr-1256 code 8 opens the waiting box).</summary>
    Sent,
    /// <summary>The sender proposed to themselves.</summary>
    Self,
    /// <summary>No fighter has that nickname (kr-1256 code 2 shows it).</summary>
    NotFound,
    /// <summary>The fighter is not online on this server.</summary>
    Offline,
    /// <summary>The fighter is in a game (kr-1256 code 4 shows it).</summary>
    Playing,
    /// <summary>The sender does not own the proposal's ring.</summary>
    NoRing,
    /// <summary>The recipient already has a partner.</summary>
    RecipientPaired,
    /// <summary>The sender already has a partner (kr-1256 code 10 shows it).</summary>
    SenderPaired,
    /// <summary>A proposal involving either fighter is still pending.</summary>
    Busy,
    /// <summary>The message is not acceptable text (kr-1256 code 13 shows it).</summary>
    BadMessage,
    /// <summary>The proposal cannot be made right now (kr-1256 code 15 shows it).</summary>
    Unavailable,
}

/// <summary>A proposal shown to its recipient (S2C <c>gspResCoupleInvite</c>) with the proposer's fighter record.</summary>
/// <param name="Inviter">The proposer.</param>
/// <param name="Appearance">The proposer's worn snapshot, shown in the proposal box.</param>
/// <param name="Recipient">The recipient's nickname.</param>
/// <param name="Message">The proposal text.</param>
public sealed record CoupleProposalDelivered(PlayerIdentity Inviter, FighterAppearance Appearance, string Recipient, string Message) : IMessage;

/// <summary>The proposer's result (S2C <c>gspResCoupleInviteResult</c>): the outcome and the fighter it concerns.</summary>
/// <param name="Result">The outcome.</param>
/// <param name="Name">The proposed-to fighter's nickname.</param>
public sealed record CoupleProposalReplied(CoupleProposalResult Result, string Name) : IMessage;

/// <summary>An answer shown to both fighters (S2C <c>gspResCoupleInviteAnswer</c>).</summary>
/// <param name="Accepted">Whether the couple was formed.</param>
/// <param name="Inviter">The proposer's nickname.</param>
/// <param name="Recipient">The recipient's nickname.</param>
public sealed record CoupleAnswerPublished(bool Accepted, string Inviter, string Recipient) : IMessage;

/// <summary>How a breakup ended for the fighter receiving the reply.</summary>
public enum CoupleBreakUpStatus
{
    /// <summary>The requester's own breakup succeeded.</summary>
    Done,
    /// <summary>The breakup was refused: no partner of that name, or the couple is younger than a day.</summary>
    Refused,
    /// <summary>The partner broke up with this fighter.</summary>
    Left,
}

/// <summary>A breakup reply (S2C <c>gspResCoupleBreakUp</c>; kr-1256 <c>sub_9C15C0</c> reads the status and one name).</summary>
/// <param name="Status">The outcome for this fighter.</param>
/// <param name="Actor">The fighter who asked for the breakup.</param>
/// <param name="Partner">The other fighter.</param>
public sealed record CoupleBreakUpReplied(CoupleBreakUpStatus Status, string Actor, string Partner) : IMessage;

/// <summary>
/// A fighter's current partner (S2C <c>gspResCoupleNotifyPartner</c>; kr-1256 <c>sub_9C1BD0</c>), sent after a change and
/// on entering the lobby.
/// </summary>
/// <param name="PartnerOnline">Whether the partner is online; this flag does not establish a relationship.</param>
/// <param name="PartnerUserNum">The partner's account number, zero for none.</param>
/// <param name="PartnerName">The partner's nickname, empty for none.</param>
public sealed record CouplePartnerPublished(bool PartnerOnline, long PartnerUserNum, string PartnerName) : IMessage;

/// <summary>A couple ring change reply (S2C <c>gspResChangeCouplering</c>), sent to both partners.</summary>
/// <param name="Changed">Whether the ring was changed.</param>
/// <param name="RingItemId">The couple ring now shown, zero when refused.</param>
public sealed record CoupleRingChangeReplied(bool Changed, int RingItemId) : IMessage;
