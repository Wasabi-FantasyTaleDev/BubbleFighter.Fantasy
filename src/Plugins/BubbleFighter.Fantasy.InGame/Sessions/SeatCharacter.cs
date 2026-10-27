using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Resolves scene characters from the same stored selection that supplies the equipped view.</summary>
internal static class SeatCharacter
{
    /// <summary>The Dao reference character item used when no equipment service is mounted.</summary>
    internal const int DaoCharacterItemId = 300001;

    internal static async ValueTask<int> ResolveAsync(ISession session, int userNum,
        IEquipmentSelection? selection, ISeatCharacterResolver? characters, CancellationToken ct) =>
        (await ResolveEquipmentAsync(session, userNum, selection, characters, ct).ConfigureAwait(false)).CharacterId;

    internal static async ValueTask<SeatEquipment> ResolveEquipmentAsync(ISession session, int userNum,
        IEquipmentSelection? selection, ISeatCharacterResolver? characters, CancellationToken ct)
    {
        // kr-1256 sub_81D060 / sub_81CCE0 overwrite the same character field for 117/201/503/806.
        // A login snapshot or optional fighter roster must not override the current worn bag.
        if (selection is not null)
        {
            var bag = await selection.ReadAsync(userNum, ct).ConfigureAwait(false);
            var selected = bag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                && row.ItemId is >= 300001 and <= 300031).Select(row => row.ItemId).Distinct().ToArray();
            if (selected.Length != 1)
                throw new InvalidOperationException($"Player {userNum} must have exactly one equipped character; found {selected.Length}.");
            return new SeatEquipment(selected[0], Array.AsReadOnly(bag
                .Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                .Select(row => row.ItemId).Distinct().ToArray()));
        }

        if (characters is not null)
        {
            var equipped = await characters.GetEquippedAsync(userNum, ct).ConfigureAwait(false);
            if (equipped is >= 300000 and <= 301999)
                return new SeatEquipment(equipped, null);
        }
        // Without an equipment service the scene shows the reference character, item 300001: the
        // item id is 300000 + character type and Dao is type 1 (packets/builds.yaml
        // creation_character_types; packets/room.yaml UserSlot.character_type — kr-812 sub_6CBFF0
        // hands the id to the actor builder kr-812 sub_92B160). What the client does with a type-0
        // item 300000 is not traced; 300001 is the safe reference.
        return new SeatEquipment(DaoCharacterItemId, null);
    }
}

/// <summary>One resolved scene appearance; null items preserve the legacy profile without inventory services.</summary>
internal sealed record SeatEquipment(int CharacterId, IReadOnlyList<int>? ItemIds)
{
    internal void Apply(RoomSeat seat)
    {
        seat.CharacterId = CharacterId;
        seat.EquippedItemIds = ItemIds;
    }
}
