namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Reads the persisted preset pages selected by the current build's equipment-view policy.
/// A shared player's stored pages do not opt other builds into that policy.</summary>
public interface IWeaponPresetPageSource
{
    /// <summary>Returns the pages used by this build, or null before their first publication.</summary>
    /// <param name="userNum">The authenticated player identity.</param>
    /// <param name="ct">Cancellation of the read.</param>
    /// <returns>A defensive copy of the published page table, or null when not materialized.</returns>
    ValueTask<WeaponPresetPage[]?> ReadAsync(long userNum, CancellationToken ct);
}
