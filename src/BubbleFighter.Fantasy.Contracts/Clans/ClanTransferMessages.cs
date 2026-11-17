using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The master offers the clan to a seated member (clan C2S <c>ClanTransferRequest</c>, writer kr-1256 sub_527F60, sent
/// from the member list kr-1256 sub_5B6A50 and the member dialog kr-1256 sub_58D660). Answered with
/// <see cref="ClanTransferRequestAnswered"/>; the member is sent <see cref="ClanTransferOffered"/>.
/// </summary>
/// <param name="UserNum">The member offered the clan.</param>
public sealed record ClanTransferRequested(int UserNum) : IMessage;

/// <summary>
/// The answer to <see cref="ClanTransferRequested"/> (clan S2C <c>ClanTransferRequestResult</c>, reader kr-1256
/// sub_52F320): a cleared flag or result 2 shows "the transfer request failed; it can only be sent to an online
/// member"; anything else shows nothing.
/// </summary>
/// <param name="Sent">1 when the offer reached the member, else 0.</param>
/// <param name="Result">1 when the offer was sent, 2 when it was refused.</param>
public sealed record ClanTransferRequestAnswered(int Sent, int Result) : IMessage;

/// <summary>
/// The offer shown to the member (clan S2C <c>ClanTransferOffer</c>, reader kr-1256 sub_52F620: "%s sent a clan transfer
/// request. Accept?"). The client answers with <see cref="ClanTransferAnswerRequested"/> and echoes
/// <see cref="Unknown1"/>.
/// </summary>
/// <param name="Nickname">The master's nickname.</param>
/// <param name="Unknown1">A word the client sends back unchanged; the server writes the master's id.</param>
public sealed record ClanTransferOffered(string Nickname, int Unknown1) : IMessage;

/// <summary>The member's answer to <see cref="ClanTransferOffered"/> (clan C2S <c>ClanTransferAnswer</c>, written inside kr-1256 sub_52F620).</summary>
/// <param name="Accepted">True when the member pressed accept.</param>
/// <param name="Unknown1">The offer's word, echoed.</param>
public sealed record ClanTransferAnswerRequested(bool Accepted, int Unknown1) : IMessage;

/// <summary>
/// The answer to an accepted <see cref="ClanTransferAnswerRequested"/> (clan S2C <c>ClanTransferAnswerResult</c>, reader
/// kr-1256 sub_5300C0): any value but 1 shows "accepting the clan transfer failed".
/// </summary>
/// <param name="Result">1 when the clan was handed over, else 0.</param>
public sealed record ClanTransferAnswerAnswered(int Result) : IMessage;

/// <summary>
/// The member's answer, told to the master (clan S2C <c>ClanTransferAnswered</c>, reader kr-1256 sub_52FB70): "%s
/// accepted the transfer", after which the client sends <see cref="ClanTransferConfirmed"/>, or "%s declined the
/// transfer".
/// </summary>
/// <param name="Accepted">True when the member accepted.</param>
/// <param name="UserNum">The member's id.</param>
/// <param name="Nickname">The member's nickname.</param>
public sealed record ClanTransferAnswered(bool Accepted, int UserNum, string Nickname) : IMessage;

/// <summary>
/// The old master's client confirming an accepted transfer (clan C2S <c>ClanTransferConfirm</c>, writer kr-1256
/// sub_528070, sent by kr-1256 sub_52FB70). Answered by telling the clan with <see cref="ClanMasterChanged"/>.
/// </summary>
/// <param name="UserNum">The new master's id.</param>
/// <param name="Nickname">The new master's nickname.</param>
public sealed record ClanTransferConfirmed(int UserNum, string Nickname) : IMessage;

/// <summary>
/// The clan has a new master (clan S2C <c>ClanMasterChanged</c>, reader kr-1256 sub_52E430 -> kr-1256 sub_51A830, which
/// makes the old master's client a member (100) and the new master's client the master (101)).
/// </summary>
/// <param name="Unknown0">A leading word the reader skips; the server writes 0.</param>
/// <param name="OldMasterUserNum">The old master's id.</param>
/// <param name="OldMasterNickname">The old master's nickname.</param>
/// <param name="NewMasterUserNum">The new master's id.</param>
/// <param name="NewMasterNickname">The new master's nickname.</param>
public sealed record ClanMasterChanged(int Unknown0, long OldMasterUserNum, string OldMasterNickname, long NewMasterUserNum, string NewMasterNickname)
    : IMessage;
