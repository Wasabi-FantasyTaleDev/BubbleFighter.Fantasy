using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Party;

/// <summary>Projects ordered membership and an optional appearance through the build's party table.</summary>
internal static class ModernPartyRecords
{
    internal static byte[] Write(PacketTables tables, string build, PartyUpdated message)
    {
        var fields = new Dictionary<string, object?>
        {
            // kr-1256 sub_9064B0 replaces membership; its keyed profile loop updates only supplied entries.
            ["members"] = message.Party.Members.Select(member => new Dictionary<string, object?>
                { ["user_number"] = member.UserNum, ["nickname"] = member.Nickname }).ToArray(),
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
}
