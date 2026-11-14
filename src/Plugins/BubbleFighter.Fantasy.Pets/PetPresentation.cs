using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets;

internal sealed class PetPresentation(PetCatalog catalog, PetsOptions options, TimeProvider time)
{
    internal InventoryItemPresentation? Project(InventorySlotRecord slot)
    {
        if (!catalog.Items.TryGetValue(slot.ItemId, out var item) || item.Function != PetCatalog.PetFunction)
            return null;
        var state = slot.Pet?.Find(options.BuildKey);
        if (state is null && (slot.Quantity <= 0 || InventoryExpiry.IsExpired(slot, time.GetUtcNow()))) return null;
        if (state is null) throw new InvalidDataException("Pet presentation requires durable initialization for the selected build.");
        state.Validate();
        var stamp = state.LastFedAt is { } fed
            ? fed.ToOffset(TimeSpan.FromMinutes(options.ClientUtcOffsetMinutes
                ?? throw new InvalidOperationException("Native pet feeding dates require an explicit client civil-clock offset.")))
                .DateTime.ToOADate() : 0;
        var whole = (int)Math.Truncate(stamp);
        var fraction = (int)Math.Truncate((stamp - whole) * 100000);
        // kr-1256 sub_114F850 reconstructs a local civil OA date; kr-1256 sub_AAC490 does not use server time.
        var attributes = new List<RoomItemAttribute>
        {
            new(slot.ItemId, PetCatalog.BaseSelector,
                new uint[] { (uint)state.Grade, (uint)state.Experience, unchecked((uint)whole), unchecked((uint)fraction) }, 0, ""),
        };
        Add(state.FirstSkill, 1);
        Add(state.SecondSkill, 2);
        return new(attributes.AsReadOnly(), state.FullnessExpiresAt);

        void Add(PetSkill? skill, uint stage)
        {
            if (skill is null) return;
            catalog.ValidateSkill(skill);
            attributes.Add(new(slot.ItemId, (uint)skill.Selector,
                new uint[] { unchecked((uint)skill.Value0), unchecked((uint)skill.Value1),
                    unchecked((uint)skill.Value2), unchecked((uint)skill.Value3) }, stage, ""));
        }
    }
}
