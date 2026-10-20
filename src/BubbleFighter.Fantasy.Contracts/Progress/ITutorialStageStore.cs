namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>
/// One tutorial-room stage a player has cleared, with the best record the client shows beside it.
/// </summary>
/// <param name="Stage">
/// The stage's MapMeta <c>stageNum</c>; the tutorial room looks a row up as row plus ten times its difficulty tab
/// (kr-812 <c>sub_6B3AC0</c>, <c>sub_6B4110</c>).
/// </param>
/// <param name="BestCount">The best count record, shown when above 0 (kr-812 <c>sub_6B4110</c>).</param>
/// <param name="BestSeconds">The best time in seconds, shown as minutes and seconds when there is no count.</param>
public sealed record TutorialStageClear(int Stage, int BestCount, int BestSeconds);

/// <summary>Keeps which tutorial-room stages each player has cleared, so the room can mark them and grant first-clear gifts.</summary>
public interface ITutorialStageStore
{
    /// <summary>Returns the stages the player has cleared, in stage order.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<TutorialStageClear>> ListAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Records a clear of <paramref name="clear"/>'s stage, keeping the better count (higher) and time (lower) of
    /// this clear and any earlier one.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="clear">The stage and this clear's record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether this was the player's first clear of the stage.</returns>
    ValueTask<bool> RecordClearAsync(long userNum, TutorialStageClear clear, CancellationToken ct);
}
