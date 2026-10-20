namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Per-player active weapon-preset slot, the one byte of weapon-preset state that must survive a
/// session: the client rebuilds its local store at profile record +28 from the S2C 837 leading <c>u8</c> on
/// every login (<c>sub_81B820</c>) and from every status-0 S2C 836 (<c>sub_9D2FF0:9D5094-9D5097</c>), and no
/// C2S save opcode exists - so the server decides the active slot after every login only if it persists the
/// slot it granted. This is menu-context player state, not room state: out-of-round swaps are granted too.
///
/// </summary>
public interface IPlayerInventoryState
{
    /// <summary>
    /// Returns the stored active weapon-preset slot for one player. A player
    /// with nothing stored reads as 0, the slot a login starts on.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored slot, 0..<see cref="InventoryLimits.MaxWeaponPresetSlot"/>.</returns>
    ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Stores the active weapon-preset slot granted by a status-0 S2C 836 swap
    /// reply. A slot outside 0..<see cref="InventoryLimits.MaxWeaponPresetSlot"/>
    /// is rejected before any state is touched.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="slot">The granted slot, 0..<see cref="InventoryLimits.MaxWeaponPresetSlot"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="userNum"/>
    /// is not a positive login id or <paramref name="slot"/> exceeds
    /// <see cref="InventoryLimits.MaxWeaponPresetSlot"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the store cannot
    /// attach the slot to a known player row; nothing is stored then.</exception>
    ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct);
}
