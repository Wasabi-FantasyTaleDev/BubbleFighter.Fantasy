using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>
/// The plaza booth a request is for. The values are the client's request subcommands, which pair with its
/// <c>Reply*@LmgProtocol</c> keys (kr-1256 <c>sub_83E1C0</c>).
/// </summary>
public enum PlazaGame
{
    /// <summary>A basketball shot (subcommand 1, <c>ReplyShootBasketball</c>).</summary>
    Basketball = 1,

    /// <summary>Casting a fishing rod (subcommand 10, <c>ReplyStartFishing</c>).</summary>
    FishingStart = 10,

    /// <summary>Lifting the rod (subcommand 11, <c>ReplyLiftFishingRod</c>).</summary>
    RodLift = 11,

    /// <summary>Drawing a fortune paper (subcommand 12, <c>ReplyFortune</c>).</summary>
    Fortune = 12,
}

/// <summary>What a plaza booth reply shows. The dialect maps each outcome to the build's own result code.</summary>
public enum PlazaGameOutcome
{
    /// <summary>The request is refused; the client shows its first refusal text and nothing is charged.</summary>
    Refused = 0,

    /// <summary>The ball missed; the wallet after the charge is shown.</summary>
    BasketballMiss,

    /// <summary>The ball went in; the wallet after the charge and the hit reward is shown.</summary>
    BasketballHit,

    /// <summary>The ball went in and won a prize item.</summary>
    BasketballItem,

    /// <summary>The cast is accepted and the client starts its fishing timers.</summary>
    FishingStarted,

    /// <summary>A fish was landed without a prize item.</summary>
    Catch,

    /// <summary>A fish was landed with one prize item.</summary>
    CatchOneItem,

    /// <summary>A fish was landed with two prize items.</summary>
    CatchTwoItems,

    /// <summary>The fortune paper is shown.</summary>
    FortunePaper,

    /// <summary>The fortune paper is shown with a prize item.</summary>
    FortuneItem,
}

/// <summary>
/// A plaza booth request (C2S <c>gcpLmgRequest_</c>). <paramref name="DisplayedCost"/> is the price the client
/// showed; <see cref="PublishedCost"/> is the price the build publishes for it, which the server charges. A request
/// whose two prices differ did not come from the published configuration and is refused.
/// </summary>
/// <param name="Game">The booth.</param>
/// <param name="DisplayedCost">The price the client displayed, or 0 for the rod lift, which writes none.</param>
public sealed record PlazaGameRequested(PlazaGame Game, int DisplayedCost) : IMessage
{
    /// <summary>Gets whether the basketball gauge ended on a hit (kr-1256 <c>sub_845050</c>).</summary>
    public bool Hit { get; init; }

    /// <summary>Gets whether a fish was on the hook when the rod was lifted (kr-1256 <c>sub_D66970</c>).</summary>
    public bool Hooked { get; init; }

    /// <summary>Gets the fortune paper the client drew (kr-1256 <c>sub_D66DE0</c>); zero for other booths.</summary>
    public ulong Paper { get; init; }

    /// <summary>
    /// Gets the price the build's published mini-game configuration sets for this request: one shot, one paper, or a
    /// cast's base price, which the client multiplies by the day's catches plus one (kr-1256 <c>sub_842600</c>);
    /// zero for the rod lift.
    /// </summary>
    public int PublishedCost { get; init; }
}

/// <summary>A plaza booth reply (S2C <c>gspLmgReply_</c>) for the requesting player.</summary>
/// <param name="Game">The booth the request was for.</param>
/// <param name="Outcome">What the reply shows.</param>
public sealed record PlazaGameReplied(PlazaGame Game, PlazaGameOutcome Outcome) : IMessage
{
    /// <summary>Gets the Lucci balance after the request, shown by every non-refusal branch that carries one.</summary>
    public long Wallet { get; init; }

    /// <summary>Gets the Lucci the rod lift paid out.</summary>
    public int Paid { get; init; }

    /// <summary>Gets the fish landed today, after this lift.</summary>
    public int FishingCount { get; init; }

    /// <summary>Gets the fortune paper to show.</summary>
    public ulong Paper { get; init; }

    /// <summary>Gets the prize items, as many as the outcome carries (zero, one or two).</summary>
    public IReadOnlyList<int> Items { get; init; } = [];
}

/// <summary>A booth animation step (C2S <c>gcpLmgSync_</c>): the <c>@LmgSync</c> key and its one value.</summary>
/// <param name="Subcommand">100 ShootFire, 101 ChangeState, 200 CastFishing or 201 LiftFishing.</param>
/// <param name="Value">The step's value word.</param>
public sealed record PlazaGameSyncRequested(int Subcommand, uint Value) : IMessage;

/// <summary>A booth animation step of another player (S2C <c>gspLmgSyncState_</c>).</summary>
/// <param name="UserNumber">The acting player's plaza user number; the client ignores its own.</param>
/// <param name="Subcommand">The <c>@LmgSync</c> key.</param>
/// <param name="Value">The step's value word.</param>
public sealed record PlazaGameSyncNotified(int UserNumber, int Subcommand, uint Value) : IMessage;

/// <summary>The day's plaza booth record (S2C <c>gspRepLmgUserRecord</c>), restored on entry and after each change.</summary>
/// <param name="FishingCount">Fish landed today.</param>
/// <param name="Paper">Today's fortune paper, or zero for none yet.</param>
public sealed record PlazaGameRecordPublished(int FishingCount, ulong Paper) : IMessage;

/// <summary>
/// The client asks for the day's plaza booth record (C2S <c>gcpReqLmgUserRecord</c>, an empty body). kr-812 sends it
/// when its plaza stage starts and again every ten seconds until the record arrives (kr-812 sub_471530, kr-812
/// sub_467230); the answer is <see cref="PlazaGameRecordPublished"/>.
/// </summary>
public sealed record PlazaGameRecordRequested : IMessage;
