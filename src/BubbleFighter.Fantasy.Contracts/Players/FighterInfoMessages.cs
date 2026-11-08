using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Players;

/// <summary>
/// A player asks to see another player's fighter information, the right-click "view info" item of the player menu
/// (legacy C2S <c>gcpReqFighterInfo</c>, modern C2S <c>gcpReqFighterInfo</c> under the build's own number). The client
/// names the target by account id, taken from the clicked row of its room or plaza list.
/// </summary>
/// <param name="TargetUserNum">The account id of the player whose information is wanted.</param>
public sealed record FighterInfoRequested(int TargetUserNum) : IMessage;

/// <summary>
/// The answer to a <see cref="FighterInfoRequested"/> (S2C <c>gspRepFighterInfo</c>). Only the fields a build's reader
/// is proven to show are carried; every other field of the client's record keeps its constructor default.
/// </summary>
/// <param name="Fighter">
/// The requested player as the server knows them, or <see langword="null"/> when a complete current snapshot is unavailable. A
/// <see langword="null"/> answer is written as the build's own failure result, never as another player's record.
/// </param>
/// <param name="Appearance">The same player's worn snapshot; required for a successful reply.</param>
/// <param name="BattleRecords">
/// The same account's detached, provider-bounded recorded modes. Null keeps the native constructor defaults;
/// a supplied snapshot requires a representable, known primary-page history or the dialect writes a failure.
/// Each dialect projects only the modes its native page establishes, without inventing earlier history.
/// </param>
public sealed record FighterInfoReplied(PlayerIdentity? Fighter, FighterAppearance? Appearance = null,
    IReadOnlyList<PlayerBattleRecord>? BattleRecords = null) : IMessage;

/// <summary>A bounded, immutable copy of the target's selected character and worn item identities.</summary>
public sealed class FighterAppearance
{
    /// <summary>Copies a complete worn set, with exactly one selected character at its head.</summary>
    /// <param name="characterItemId">The selected character item, 300000 plus its native type.</param>
    /// <param name="equippedItemIds">The owned, unexpired worn items, including the selected character.</param>
    public FighterAppearance(int characterItemId, IReadOnlyList<int> equippedItemIds)
    {
        ArgumentNullException.ThrowIfNull(equippedItemIds);
        if (characterItemId is < 300001 or > 300031)
            throw new ArgumentOutOfRangeException(nameof(characterItemId));
        if (equippedItemIds.Count is 0 or > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(equippedItemIds));
        var copy = equippedItemIds.ToArray();
        if (copy[0] != characterItemId || copy.Any(id => id <= 0)
            || copy.Distinct().Count() != copy.Length
            || copy.Count(id => id is >= 300001 and <= 300031) != 1)
            throw new ArgumentException("A fighter appearance requires one character first and distinct positive worn items.", nameof(equippedItemIds));
        CharacterItemId = characterItemId;
        EquippedItemIds = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the selected character's inventory identity.</summary>
    public int CharacterItemId { get; }

    /// <summary>Gets the copied worn identities, with the selected character first.</summary>
    public IReadOnlyList<int> EquippedItemIds { get; }
}
