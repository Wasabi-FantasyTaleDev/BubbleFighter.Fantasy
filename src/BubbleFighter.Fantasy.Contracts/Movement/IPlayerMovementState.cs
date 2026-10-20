namespace BubbleFighter.Fantasy.Contracts.Movement;

/// <summary>
/// Per-actor movement state for the others-only movement broadcast. The state
/// is menu-context player state, not room state: the client numbers its
/// in-session actor per seat and streams partial samples that only make sense
/// against the full record an earlier sample established, so the merged state
/// must survive room switches within the process. Keys are the actor id each
/// sample carries in its own body - the server adds nothing.
///
/// </summary>
public interface IPlayerMovementState
{
    /// <summary>
    /// Returns the merged movement state of one actor, or
    /// <see langword="null"/> when the actor has none. An actor without state
    /// has not sent anything mergeable yet; nothing is fabricated.
    ///
    /// </summary>
    /// <param name="actorId">The actor id the samples carry.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerMovementState?> FindAsync(int actorId, CancellationToken ct);

    /// <summary>
    /// Merges one mask-gated sample into the actor's stored state and returns the merged record. Only
    /// mask-selected fields are overwritten; the rest keep their stored values. A full mask (<see
    /// cref="PlayerMovementState.FullMask"/>) initializes or overwrites the whole record, so a first FULL
    /// sample is accepted for an unknown actor. A narrower mask for an actor with no stored state returns <see
    /// langword="null"/>: the merge is refused rather than fabricating zeros, because the client always sends a
    /// FULL before any partial. </summary> <param name="actorId">The actor id the sample carries.</param>
    /// <param name="mask">Presence bits: 1/2/4 gate x/y/z, 8/0x10/0x20 gate yaw/pitch/state.</param> <param
    /// name="x">Position x; ignored when mask bit 1 is clear.</param> <param name="y">Position y; ignored when
    /// mask bit 2 is clear.</param> <param name="z">Position z; ignored when mask bit 4 is clear.</param>
    /// <param name="yaw">Yaw byte; ignored when mask bit 8 is clear.</param> <param name="pitch">Pitch byte;
    /// ignored when mask bit 0x10 is clear.</param> <param name="state">State byte; ignored when mask bit 0x20
    /// is clear.</param> <param name="ct">Cancellation token.</param> <returns>The merged state, or <see
    /// langword="null"/> when a partial sample named an actor with no stored state.</returns> <exception
    /// cref="InvalidOperationException">Thrown when the store is at its actor cap and a new actor must be
    /// tracked; the sample is the caller's signal to refuse rather than grow without limit.</exception>
    ValueTask<PlayerMovementState?> MergeAsync(
        int actorId,
        byte mask,
        float x,
        float y,
        float z,
        byte yaw,
        byte pitch,
        byte state,
        CancellationToken ct);
}
