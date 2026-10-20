using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>
/// A client asked to log in with an account name. Builds that authenticate the account
/// before the game connection (a web passport) carry only the name here.
/// </summary>
/// <param name="Account">The account name, trimmed; empty when the client sent none.</param>
public sealed record LoginRequested(string Account) : IMessage;

/// <summary>The login was accepted; the build's codec writes its login reply from these fields.</summary>
/// <param name="UserNumber">The player's user number.</param>
/// <param name="Account">The account name the player logged in with.</param>
/// <param name="Nickname">The player's nickname.</param>
/// <param name="ServerTime">The server's local time when the login was accepted.</param>
/// <param name="CharacterItemId">The player's equipped character item (300001 and up).</param>
public sealed record LoginAccepted(int UserNumber, string Account, string Nickname, DateTime ServerTime, int CharacterItemId) : IMessage;

/// <summary>
/// The login succeeded and the player's state is loaded. This is the one message login logic
/// sends on every build; each build's codec turns it into that build's whole login sequence
/// (reply, stats, wallet, inventory, main-menu room), so the order and the build-only packets
/// live with the layouts, not in the logic.
/// </summary>
/// <param name="UserNumber">The player's user number.</param>
/// <param name="Account">The account name the player logged in with.</param>
/// <param name="Nickname">The player's nickname.</param>
/// <param name="Level">The player's level.</param>
/// <param name="ServerTime">The server's local time when the login was accepted.</param>
/// <param name="CharacterItemId">The player's equipped character item (300001 and up).</param>
/// <param name="EquippedItemIds">Every equipped item, the character included.</param>
/// <param name="Wallet">The player's wallet, or <see langword="null"/> when the deployment keeps no wallet store.</param>
/// <param name="CharacterBag">The character bag, or <see langword="null"/> when the deployment keeps no inventory store.</param>
/// <param name="EquippedBag">The equipped bag, or <see langword="null"/> when the deployment keeps no inventory store.</param>
public sealed record LoginSucceeded(
    int UserNumber,
    string Account,
    string Nickname,
    int Level,
    DateTime ServerTime,
    int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds,
    PlayerWallet? Wallet,
    IReadOnlyList<InventorySlotRecord>? CharacterBag,
    IReadOnlyList<InventorySlotRecord>? EquippedBag) : IMessage
{
    /// <summary>Gets whether this login should offer the initial tutorial, or null to use the dialect profile's policy.</summary>
    public bool? OfferTutorial { get; init; }

    /// <summary>
    /// Gets the item ids whose time limit ended since the last login and were removed before the bag was
    /// loaded; the codec announces them once the menu is open. Empty when nothing expired.
    /// </summary>
    public IReadOnlyList<int> ExpiredItemIds { get; init; } = [];

    /// <summary>Gets the saved player-wide avatar material color.</summary>
    public ushort ColorIndex { get; init; }
}

/// <summary>The login was refused.</summary>
/// <param name="Reason">The build-neutral reason.</param>
public sealed record LoginRejected(LoginRejection Reason) : IMessage;

/// <summary>Why a login was refused.</summary>
public enum LoginRejection
{
    /// <summary>No player exists for the account and players are not created on demand.</summary>
    AccountNotFound = 1,
}

/// <summary>
/// The main menu's room session opens around the local player, whose character the menu
/// shows. The build's codec writes the session, the player's slot and the equipped items.
/// Tutorial play mode and map key are <c>WriteRoom</c> parameters on the dialect side, not
/// fields here: optional trailing ctor args made Room.dll call a six-argument constructor
/// the host Contracts assembly did not have (<c>MissingMethodException</c> on CreateTutorial).
/// </summary>
/// <param name="UserNumber">The player's user number.</param>
/// <param name="Nickname">The player's nickname.</param>
/// <param name="CharacterItemId">The equipped character item.</param>
/// <param name="EquippedItemIds">Every equipped item, the character included.</param>
public sealed record MainMenuRoomOpened(
    int UserNumber,
    string Nickname,
    int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds) : IMessage
{
    /// <summary>Gets whether to offer the initial tutorial, or null to use the dialect profile's policy.</summary>
    public bool? OfferTutorial { get; init; }

    /// <summary>Gets the saved avatar material color to install in the menu seat.</summary>
    public ushort ColorIndex { get; init; }
}
