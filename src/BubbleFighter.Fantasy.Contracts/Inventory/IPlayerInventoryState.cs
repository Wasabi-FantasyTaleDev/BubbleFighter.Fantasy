namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Per-player weapon-preset state that must survive a session: the active slot, and the server-owned
/// preset pages behind the default-off <c>persist_preset_pages</c> switch. The client rebuilds its local
/// store at profile record +28 from the S2C 837 leading <c>u8</c> on every login (<c>sub_81B820</c>) and
/// from every status-0 S2C 836 (<c>sub_9D2FF0:9D5094-9D5097</c>), and no C2S save opcode exists - so the
/// server decides the active slot and the page contents after every login only if it persists what it
/// granted and materialized. This is menu-context player state, not room state: out-of-round swaps are
/// granted too.
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

    /// <summary>
    /// Returns the stored weapon-preset pages for one player, or <see langword="null"/> when no pages were
    /// materialized yet. Page content is server-owned - the swap chain has no client-to-server save packet, so
    /// the S2C 837/833 records the pages carry are whatever this store kept - and once pages exist they win over
    /// re-deriving them from the worn set.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Five pages in page order, or <see langword="null"/> before the first materialization.</returns>
    ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Stores a player's weapon-preset pages for the S2C 837/833 bar to republish. The write replaces every
    /// stored page of the player, so a partial list cannot leave stale pages behind.
    ///
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="pages">The pages to store; exactly
    /// <see cref="WeaponPresetSlots.Count"/> entries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="userNum"/>
    /// is not a positive login id.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pages"/> does not carry
    /// exactly <see cref="WeaponPresetSlots.Count"/> pages or holds a null entry.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the store cannot
    /// attach the pages to a known player row; nothing is stored then.</exception>
    ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct);

    /// <summary>Atomically returns existing pages or stores the supplied initial table. Concurrent initializers
    /// receive the same winning table; an initialization never replaces pages already stored.</summary>
    /// <param name="userNum">Positive player identity.</param>
    /// <param name="initialPages">Exactly five initial pages, defensively copied before storage.</param>
    /// <param name="ct">Cancellation of the operation.</param>
    /// <returns>A defensive copy of the table committed for this player.</returns>
    ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct);
}
