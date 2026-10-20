using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Contracts.Presents;

/// <summary>How a gift purchase ended; the build's codec turns it into that client's result word.</summary>
public enum PresentSendStatus
{
    /// <summary>The gift was bought and stored.</summary>
    Sent = 0,

    /// <summary>No player has the typed nickname.</summary>
    ReceiverMissing = 1,

    /// <summary>The sending connection has no usable player.</summary>
    SenderInvalid = 2,

    /// <summary>The sender's balance does not cover the price.</summary>
    InsufficientFunds = 3,

    /// <summary>The key is not an item this build's shop sells as a gift.</summary>
    NotGiftable = 4,

    /// <summary>The receiver already owns the item, or the shop's item terms refuse another copy.</summary>
    ReceiverOwnsItem = 5,

    /// <summary>The receiver is the sender.</summary>
    SelfGift = 6,

    /// <summary>The message is longer than this server keeps.</summary>
    MessageRejected = 7,

    /// <summary>The item is sold for a currency this server does not accept for gifts.</summary>
    CurrencyRefused = 8,

    /// <summary>The receiver keeps the most gifts a player may.</summary>
    ReceiverBoxFull = 9,

    /// <summary>The sender keeps the most sent gifts a player may.</summary>
    SenderBoxFull = 10,
}

/// <summary>The player asks to buy a shop item as a gift for another player.</summary>
/// <param name="ReceiverNickname">The nickname the player typed.</param>
/// <param name="StockKey">The Stock or StockSet key of the shop item.</param>
/// <param name="Message">The message to go with the gift.</param>
public sealed record PresentSendRequested(string ReceiverNickname, int StockKey, string Message) : IMessage;

/// <summary>Completes a gift purchase dialog.</summary>
/// <param name="Status">How the purchase ended.</param>
public sealed record PresentSendReplied(PresentSendStatus Status) : IMessage;

/// <summary>The player asks for one page of a gift list.</summary>
/// <param name="Box">Which list.</param>
/// <param name="Start">How many gifts to skip.</param>
/// <param name="Count">The page size the client shows.</param>
public sealed record PresentListRequested(PresentBox Box, int Start, int Count) : IMessage;

/// <summary>One page of a gift list.</summary>
/// <param name="Box">Which list.</param>
/// <param name="Total">How many gifts the whole list holds.</param>
/// <param name="PendingCount">The received gifts not taken yet, for the task-bar badge.</param>
/// <param name="Records">The page, newest first.</param>
public sealed record PresentListReplied(PresentBox Box, int Total, int PendingCount, IReadOnlyList<PresentRecord> Records)
    : IMessage;

/// <summary>What the player does to one gift.</summary>
public enum PresentAction
{
    /// <summary>Takes a pending gift into the bag.</summary>
    Receive = 0,

    /// <summary>Throws a pending gift away.</summary>
    Throw = 1,

    /// <summary>Removes a gift from the received list.</summary>
    DeleteReceived = 2,

    /// <summary>Removes a gift from the sent list.</summary>
    DeleteSent = 3,
}

/// <summary>The player acts on one gift of theirs.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Id">The gift, as the list showed it.</param>
public sealed record PresentActionRequested(PresentAction Action, long Id) : IMessage;

/// <summary>Completes one gift action.</summary>
/// <param name="Action">What was asked.</param>
/// <param name="Succeeded">Whether the action was carried out.</param>
/// <param name="Id">The gift.</param>
/// <param name="StockKey">The shop key of the gift (zero when the gift is unknown).</param>
/// <param name="PendingCount">The received gifts not taken yet, for the task-bar badge.</param>
public sealed record PresentActionReplied(PresentAction Action, bool Succeeded, long Id, int StockKey, int PendingCount)
    : IMessage;

/// <summary>The player asks whether a new gift is waiting; the client sends it when it closes the gift box.</summary>
public sealed record PresentNoticeRequested : IMessage;

/// <summary>Tells the client whether a gift is waiting, which it shows as a blinking task-bar icon and a count.</summary>
/// <param name="HasNew">Whether any received gift is not taken yet.</param>
/// <param name="PendingCount">How many received gifts are not taken yet.</param>
public sealed record PresentNoticeSent(bool HasNew, int PendingCount) : IMessage;

/// <summary>
/// The main menu is installed at the end of a login: the client's task bar exists, so state that lives in it can be
/// sent now. Raised by the build's login codec after its login sequence.
/// </summary>
/// <param name="Protocol">The codec's connection protocol.</param>
/// <param name="Session">The connection that just logged in.</param>
/// <param name="Login">The authoritative state supplied by login logic.</param>
public sealed record LoginMenuInstalled(string Protocol, ISession Session, LoginSucceeded Login) : IEvent;
