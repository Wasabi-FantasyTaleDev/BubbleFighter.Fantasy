using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Overlays retained native selectors on the slot fields each build's own consumer writes.</summary>
internal static class RoomInventoryProjection
{
    internal static (string Character, string[] Weapons) Fields(PacketTables packets, string build)
    {
        if (!packets.ValuesOf(build).TryGetValue("room_inventory_selection_fields", out var raw)
            || raw is not Dictionary<string, object?> map || map.Count != 2
            || !map.TryGetValue("character", out var characterRaw) || characterRaw is not string character
            || !map.TryGetValue("weapons", out var weaponsRaw) || weaponsRaw is not string[] list)
            throw new ConfigException("Room inventory selection requires native slot field aliases.");
        var weapons = list;
        if (weapons.Length is not (4 or 5) || weapons.Append(character).Distinct(StringComparer.Ordinal).Count() != weapons.Length + 1)
            throw new ConfigException("Room inventory selection requires distinct character and weapon slot aliases.");
        var fields = packets.Resolve(build, "UserSlot");
        foreach (var name in weapons.Append(character))
            if (!fields.Any(field => field.Name == name && field.Type == "u16"))
                throw new ConfigException($"Room inventory selector {name} must name a native UserSlot u16 field.");
        return (character, weapons);
    }

    internal static void Apply(PacketTables packets, string build, Dictionary<string, object?> values,
        RoomInventorySelection selection)
    {
        var fields = Fields(packets, build);
        if (!values.ContainsKey(fields.Character))
            return;
        if (selection.WeaponSelections.Count != fields.Weapons.Length)
            throw new InvalidOperationException("The retained room inventory selector count disagrees with the native slot.");
        values[fields.Character] = checked((ushort)selection.CharacterSelection);
        for (var index = 0; index < fields.Weapons.Length; index++)
            values[fields.Weapons[index]] = unchecked((ushort)selection.WeaponSelections[index]);
    }
}
