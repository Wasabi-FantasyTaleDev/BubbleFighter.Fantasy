using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The clan home request (clan C2S <c>ClanHomeRequest</c>, empty body): the client sends it when its clan login
/// is accepted and again after every change of its own membership (kr-1256 sub_526E90). It is answered with
/// <see cref="ClanHomeSent"/>.
/// </summary>
public sealed record ClanHomeRequested : IMessage;

/// <summary>
/// The player's own clan state (clan S2C <c>ClanHome</c>, handler kr-1256 sub_526330 into kr-1256 sub_51A3A0):
/// which clan the player belongs to or applied to, and with which role.
/// </summary>
/// <param name="ProfileFollows">Whether the client should ask for the clan profile next: it sends <see cref="ClanProfileRequested"/> when this is set.</param>
/// <param name="ClanId">The clan id; 0 for a player without a clan.</param>
/// <param name="Role">The role word, one of <see cref="ClanRole"/>; 0 for a player without a clan.</param>
/// <param name="ClanName">The clan name; empty for a player without a clan.</param>
/// <param name="DisbandAt">When the clan's pending disband takes effect (UTC); null while none is pending.</param>
public sealed record ClanHomeSent(bool ProfileFollows, long ClanId, int Role, string ClanName, DateTime? DisbandAt) : IMessage;

/// <summary>
/// The clan profile request (clan C2S <c>ClanProfileRequest</c>, empty body, kr-1256 sub_526F50), sent by the
/// client when a <see cref="ClanHomeSent"/> asked for it. It is answered with <see cref="ClanProfileSent"/>.
/// </summary>
public sealed record ClanProfileRequested : IMessage;

/// <summary>
/// The profile of the player's own clan (clan S2C <c>ClanProfile</c>, handler kr-1256 sub_5268A0 into
/// kr-1256 sub_51A5F0).
/// </summary>
/// <param name="Clan">The clan.</param>
/// <param name="MasterName">The master's nickname.</param>
/// <param name="DeputyUserNum">The deputy's player id; 0 when the clan has none.</param>
/// <param name="DeputyName">The deputy's nickname; empty when the clan has none.</param>
public sealed record ClanProfileSent(ClanRecord Clan, string MasterName, long DeputyUserNum, string DeputyName) : IMessage;

/// <summary>
/// A request to found a clan (clan C2S <c>ClanCreate</c>, writer kr-1256 sub_5275B0, called by the create dialog
/// kr-1256 sub_5C3740). The dialog opens only for an owned creation card (kr-1256 sub_51BA40), so the request
/// names the card it is made with.
/// </summary>
/// <param name="Name">The clan name.</param>
/// <param name="Unknown1">The word after the name; the dialog always passes 0.</param>
/// <param name="Introduction">The introduction text.</param>
/// <param name="Item">The creation card's item id (the dialog's +1640, set by kr-1256 sub_51BA40).</param>
/// <param name="Capacity">
/// The card's item-table parameter (the dialog's +1644): the member capacity its ShopItem row gives it. The server
/// takes the capacity from its own card table and only checks this word against it. <see langword="null"/> for a
/// build whose request has no such word (kr-812 sub_499760 writes the name, a word, the introduction and the item).
/// </param>
public sealed record ClanCreateRequested(string Name, int Unknown1, string Introduction, int Item, int? Capacity) : IMessage;

/// <summary>
/// The answer to <see cref="ClanCreateRequested"/> (clan S2C <c>ClanCreateResult</c>, handler kr-1256 sub_517FE0).
/// </summary>
/// <param name="Result">
/// The result the client switches on: <see cref="Created"/> shows the success notice and requests the clan home
/// again; 2 and -100 to -105 each show one refusal notice; any other value shows a general failure.
/// </param>
/// <param name="Item">
/// On <see cref="Created"/> the client takes one of this item out of its own inventory view (kr-1256 sub_805C50)
/// and looks it up in its item table without a null check (kr-1256 sub_D9A2A0 into kr-1256 sub_A01290), so a
/// success must carry an item id the client knows. Not read on a refusal.
/// </param>
/// <param name="Unknown8">The third word, handed to kr-1256 sub_D9A2A0 on success, which never reads it.</param>
public sealed record ClanCreateAnswered(int Result, int Item, int Unknown8) : IMessage
{
    // The result words and the notice kr-1256 sub_517FE0 shows for each.

    /// <summary>The clan was created: "clan creation succeeded".</summary>
    public const int Created = 1;

    /// <summary>"The clan name already exists; choose another name."</summary>
    public const int NameTaken = 2;

    /// <summary>"Creation failed: wrong clan name length."</summary>
    public const int NameLength = -100;

    /// <summary>"Creation failed: wrong clan description length."</summary>
    public const int IntroductionLength = -101;

    /// <summary>"The clan name contains a forbidden word."</summary>
    public const int NameForbidden = -102;

    /// <summary>"The clan description contains a forbidden word."</summary>
    public const int IntroductionForbidden = -103;

    /// <summary>"You have already applied to a clan; cancel the application before creating one."</summary>
    public const int AlreadyApplied = -104;

    /// <summary>"You are already in a clan; leave it before creating one."</summary>
    public const int AlreadyMember = -105;

    /// <summary>Any word without a notice of its own shows "clan creation failed" (the switch's default).</summary>
    public const int Failed = 0;
}
