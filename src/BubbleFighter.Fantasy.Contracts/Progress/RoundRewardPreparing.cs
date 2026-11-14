using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>Declares that one protocol freezes prepared rewards before its single atomic round settlement.</summary>
public interface IPreparedRoundRewards
{
    /// <summary>Gets the game protocol whose terminal settlements invoke reward preparation.</summary>
    string Protocol { get; }
}

/// <summary>
/// Prepares one player's terminal reward from equipment frozen when that player entered the match.
/// Middleware must be free of external effects; the caller may prepare competing plans before adopting one.
/// </summary>
/// <param name="Protocol">The game protocol that owns this settlement.</param>
/// <param name="MatchId">The identity retained by every retry of the same settlement.</param>
/// <param name="UserNum">The positive login player identity.</param>
/// <param name="ModeId">The native mode of the completed match.</param>
/// <param name="Reason">The terminal reason already accepted by the match logic.</param>
/// <param name="Equipment">The player's immutable equipment at admission to this match.</param>
/// <param name="BaseReward">The unmodified reward returned by the waterfall's terminal delegate.</param>
public sealed record RoundRewardPreparing(string Protocol, string MatchId, long UserNum, int ModeId,
    RoundEndReason Reason, RoundEquipmentSnapshot Equipment, RoundRewardAmounts BaseReward) : IEvent;

/// <summary>Nonnegative reward components kept separate until a settlement command is created.</summary>
/// <param name="Experience">Experience credited for completing the match.</param>
/// <param name="CompletionLucci">Lucci computed from the match outcome.</param>
/// <param name="CollectedLucci">Lucci from accepted in-match pickups.</param>
public sealed record RoundRewardAmounts(int Experience, int CompletionLucci, int CollectedLucci)
{
    /// <summary>Gets the checked sum of completion Lucci and collected Lucci.</summary>
    public int TotalLucci => checked(CompletionLucci + CollectedLucci);

    /// <summary>Rejects negative credits or a combined Lucci amount that cannot fit the settlement contract.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Experience);
        ArgumentOutOfRangeException.ThrowIfNegative(CompletionLucci);
        ArgumentOutOfRangeException.ThrowIfNegative(CollectedLucci);
        _ = TotalLucci;
    }
}

/// <summary>Owns bounded copies of worn item identities, native modifiers and every modifier value vector.</summary>
public sealed class RoundEquipmentSnapshot
{
    /// <summary>Gets an immutable snapshot for a player who entered without worn equipment.</summary>
    public static RoundEquipmentSnapshot Empty { get; } = new([], []);

    /// <summary>
    /// Copies a published equipment view within the inventory count bounds, rejecting unrelated modifiers
    /// and aggregate modifier text larger than a native 16-bit room packet can retain.
    /// </summary>
    /// <param name="wornItemIds">Distinct positive identities worn when the player entered the match.</param>
    /// <param name="itemAttributes">Native modifiers belonging only to those identities.</param>
    public RoundEquipmentSnapshot(IReadOnlyList<int> wornItemIds, IReadOnlyList<RoomItemAttribute> itemAttributes)
    {
        ArgumentNullException.ThrowIfNull(wornItemIds);
        ArgumentNullException.ThrowIfNull(itemAttributes);
        if (wornItemIds.Count > InventoryLimits.MaxEquippedSlots
            || itemAttributes.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidDataException("Round equipment exceeds the inventory snapshot bounds.");

        var ids = new int[wornItemIds.Count];
        var selected = new HashSet<int>();
        for (var index = 0; index < ids.Length; index++)
        {
            var itemId = wornItemIds[index];
            if (itemId <= 0 || !selected.Add(itemId))
                throw new InvalidDataException("Round equipment must contain distinct positive item identities.");
            ids[index] = itemId;
        }

        var attributes = new RoomItemAttribute[itemAttributes.Count];
        var textCharacters = 0;
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = itemAttributes[index];
            if (attribute is null || !selected.Contains(attribute.ItemId) || attribute.Values is null
                || attribute.Values.Count > InventoryLimits.MaxInventorySlotsPerBag || attribute.Unknown32 is null)
                throw new InvalidDataException("Round equipment contains an unrelated or malformed item modifier.");
            if (attribute.Unknown32.Length > ushort.MaxValue / sizeof(char) - textCharacters)
                throw new InvalidDataException("Round equipment modifier text exceeds the native room packet bound.");
            textCharacters += attribute.Unknown32.Length;
            var values = new uint[attribute.Values.Count];
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
                values[valueIndex] = attribute.Values[valueIndex];
            attributes[index] = attribute with { Values = Array.AsReadOnly(values) };
        }
        WornItemIds = Array.AsReadOnly(ids);
        ItemAttributes = Array.AsReadOnly(attributes);
    }

    /// <summary>Gets the copied worn identities in their published order.</summary>
    public IReadOnlyList<int> WornItemIds { get; }

    /// <summary>Gets the copied native modifiers with read-only value vectors.</summary>
    public IReadOnlyList<RoomItemAttribute> ItemAttributes { get; }
}
