using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// The host's client reports that a runner reached the BFRunner goal (round mode 21). Only the host tests the goal box
/// (kr-812 sub_513DE0) and it sends the finishing actor's team once, from C2S <c>gcpBFRunnerOver</c>.
/// </summary>
/// <param name="Team">The finishing runner's team code, 1 or 2 for a valid report.</param>
public sealed record RoundBfRunnerGoalReported(int Team) : IMessage;

/// <summary>
/// A client reports that its own runner entered a checkpoint region it had not just reported (kr-812 sub_580E10),
/// from C2S <c>gcpBFRunnerCheckPoint</c>.
/// </summary>
/// <param name="Region">The checkpoint region's identifier from the map data.</param>
public sealed record RoundBfRunnerCheckpointReported(int Region) : IMessage;

/// <summary>One seat's race progress as a client sees it.</summary>
/// <param name="Seat">The room seat index.</param>
/// <param name="Progress">10000 times the seat's checkpoint region plus its distance to that region (kr-812 sub_513DE0).</param>
public readonly record struct BfRunnerSeatProgress(int Seat, int Progress);

/// <summary>
/// The once-a-second progress list every client sends (kr-812 sub_50DFC0), from C2S
/// <c>gcpBFRunnerCheckPointValue</c>.
/// </summary>
/// <param name="Seats">One entry per seat the client has a live character for.</param>
public sealed record RoundBfRunnerProgressReported(IReadOnlyList<BfRunnerSeatProgress> Seats) : IMessage;

/// <summary>
/// Tells a client that a seat reached a checkpoint, as S2C <c>gspBFRunnerCheckPointValue</c>: the client stores the
/// region on that seat's character (kr-812 sub_5295D0), including the reporting player's own.
/// </summary>
/// <param name="Seat">The room seat index.</param>
/// <param name="Region">The checkpoint region's identifier.</param>
public sealed record RoundBfRunnerCheckpointReached(int Seat, int Region) : IMessage;

/// <summary>
/// Tells a client that the race is over, as S2C <c>gspBFRunnerOver</c>: the client stores the winning team and marks
/// the race finished (kr-812 sub_529590); kr-812 sub_52AC10 later plays that team's victory action.
/// </summary>
/// <param name="Team">The winning team code.</param>
public sealed record RoundBfRunnerFinished(int Team) : IMessage;

/// <summary>
/// A client reports that its own runner touched a track item (kr-812 sub_580E10), from C2S
/// <c>gcpBFRunnerEatItem</c>: a speed pad (region type 2) or an item box (region type 3).
/// </summary>
/// <param name="Kind">101 for a speed pad, 0 for an item box.</param>
/// <param name="Region">The item box region's identifier; -1 for a speed pad.</param>
public sealed record RoundBfRunnerItemTouched(int Kind, int Region) : IMessage;

/// <summary>
/// Puts an item box's item into a runner's held slot, as S2C <c>gspAddItemToSlot</c>: in round mode 21 the client
/// stores the item in the named slot (kr-812 sub_517DC0 -> kr-812 sub_57CB60 -> kr-812 sub_501DF0).
/// </summary>
/// <param name="Seat">The runner's room seat index.</param>
/// <param name="Kind">The item identifier from the client's ItemSpecBFRunner sheet.</param>
/// <param name="ItemSlot">The held slot the item goes into.</param>
public sealed record RoundBfRunnerItemGranted(int Seat, int Kind, int ItemSlot) : IMessage;
