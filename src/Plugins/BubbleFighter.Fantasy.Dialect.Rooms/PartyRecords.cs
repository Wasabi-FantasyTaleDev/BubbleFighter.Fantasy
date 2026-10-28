using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Projects ordered membership and an optional appearance through the build's party table.</summary>
internal static class PartyRecords
{
    internal static byte[] Write(PacketTables tables, string build, PartyUpdated message)
    {
        ValidateLayouts(tables, build);
        var fields = new Dictionary<string, object?>
        {
            // kr-1256 sub_9064B0 replaces membership; its keyed profile loop updates only supplied entries.
            ["members"] = message.Party.Members.Select(member => new Dictionary<string, object?>
                { ["user_number"] = member.UserNum, ["nickname"] = member.Nickname, ["level"] = member.Level }).ToArray(),
        };
        if (message.Appearance is { } appearance)
        {
            if (!message.Party.Members.Any(member => member.UserNum == appearance.UserNum))
                throw new InvalidOperationException("The scene recipient is absent from its party membership.");
            if (appearance.CharacterItemId is < 300000 or > 301999)
                throw new ArgumentException("A party appearance requires a resolved native character item.", nameof(message));
            fields["profile_entries"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["user_number"] = appearance.UserNum,
                    ["character_id"] = appearance.CharacterItemId,
                    ["equipped_items"] = appearance.EquippedItemIds.Where(id => id != appearance.CharacterItemId).ToArray(),
                },
            };
        }
        return PacketCodec.Write(tables, build, "SoloPartySnapshot", fields);
    }

    /// <summary>
    /// Checks the rows the writer fills: the member vector every snapshot states, and the profile-entry vector the
    /// appearance branch sizes (kr-1256 sub_9064B0 installs one record per entry through the element reader
    /// kr-1256 sub_10C4290, whose user number, character item, worn list and stamp blob this writer supplies).
    /// This helper has no bind step of its own, so it checks before every use rather than on the first send.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    internal static void ValidateLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "SoloPartySnapshot", "Solo party records",
            ("members", "list<SoloPartyMember>"));
        var entries = tables.Resolve(build, "SoloPartySnapshot").Single(field => field.Name == "profile_entries");
        if (entries.Type is not ("list<RoomProfileEntry>" or "list<SoloPartyEquippedItem>"))
            throw new BubbleFighter.Fantasy.Abstractions.ConfigException("Solo party records require an audited appearance container.");
        PacketLayoutContract.RequireFields(tables, build, "SoloPartyMember", "Solo party records",
            ("user_number", "u32"), ("nickname", "wstr"));
        var element = entries.Type[5..^1];
        PacketLayoutContract.RequireFields(tables, build, element, "Solo party records",
            ("user_number", "u32"), ("character_id", "u32"), ("equipped_items", "list<u32>"));
        if (element == "RoomProfileEntry")
            PacketLayoutContract.RequireFields(tables, build, element, "Solo party records", ("unknown_n5", "list<u8>"));
    }
}
