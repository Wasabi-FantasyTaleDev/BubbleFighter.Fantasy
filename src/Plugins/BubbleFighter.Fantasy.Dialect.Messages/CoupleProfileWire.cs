using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Projects the same committed couple state into own-profile updates and ordinary user-stat refreshes.</summary>
internal static class CoupleProfileWire
{
    /// <summary>Reads the connection's latest profile without caching it in a packet writer.</summary>
    internal static CoupleProfileState? Current(ISession session) =>
        session.TryFeature<ICoupleProfile>(out var profile) ? profile.Current : null;

    /// <summary>Requires the optional profile body consumed by kr-1256 sub_9C33C0 and sub_10E1CF0.</summary>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "CoupleProfileUpdate", "Own couple profile",
            ("unknown_48", "optional<0x3A,FighterInfoTail48>"));
        PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Own couple profile",
            ("unknown_48", "optional<0x3A,FighterInfoTail48>"));
        PacketLayoutContract.RequireFields(tables, build, "FighterInfoTail48", "Own couple profile",
            ("couple_since", "u64"), ("couple_ring_item", "u32"), ("unknown_60", "u32"), ("couple_partner_online", "bool"));
    }

    /// <summary>Writes the dedicated own-couple update, including the absent-tag form after breakup.</summary>
    internal static byte[] Write(PacketTables tables, string build, CoupleProfileState? profile)
    {
        RequireLayouts(tables, build);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddToFighter(tables, build, fields, profile);
        return PacketCodec.Write(tables, build, "CoupleProfileUpdate", fields);
    }

    /// <summary>Adds the committed optional block without changing unrelated fighter statistics.</summary>
    internal static void AddToFighter(PacketTables tables, string build, Dictionary<string, object?> fields,
        CoupleProfileState? profile)
    {
        if (profile is null) return;
        var optional = tables.Resolve(build, "FighterInfo").Any(field => field.Name == "unknown_48"
            && field.Type == "optional<0x3A,FighterInfoTail48>");
        if (optional) RequireLayouts(tables, build);
        else
        {
            PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Own couple profile",
                ("couple_since", "u64"), ("couple_ring_item", "u32"), ("couple_partner_online", "bool"));
            // cn-324 sub_750710 gates the partner panel on FighterInfo+4 (user number); +56 is losses.
            // Other inline readers require their independently proven presence word.
            var presenceField = PresenceFromUserNumber(tables, build) ? "user_number" : "couple_present";
            PacketLayoutContract.RequireFields(tables, build, "FighterInfo", "Own couple profile", (presenceField, "u32"));
        }
        // Missing display-ring data in this client must not remove a persisted relationship. The native
        // kr-1256 sub_5DB670 and kr-812 sub_881780 guard a missing ring lookup before rendering its name.
        ArgumentOutOfRangeException.ThrowIfNegative(profile.DisplayRingItemId);
        // kr-1256 sub_5DB670 reads +48 as a double and formats it through sub_1116B70 (VarUdateFromDate).
        // Its current-time path sub_1116A80 -> sub_11165E0 uses local SYSTEMTIME -> SystemTimeToVariantTime.
        // Keep the same local wall-clock OLE-date convention used by the present and login writers.
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["couple_since"] = BitConverter.DoubleToInt64Bits(profile.CreatedAt.ToLocalTime().DateTime.ToOADate()),
            // kr-1256 sub_5DB670 and sub_E807F0 resolve +56 through the ShopItem lookup sub_A01310.
            ["couple_ring_item"] = profile.DisplayRingItemId,
            // +60 is rendered numerically by sub_5DB670; its meaning remains unproven and its table default is retained.
            // kr-1256 sub_5DB670 passes +64 to sub_5DC4E0, which selects "Offline" when false.
            ["couple_partner_online"] = profile.PartnerOnline,
        };
        if (optional) fields["unknown_48"] = values;
        else
        {
            if (!PresenceFromUserNumber(tables, build)) fields["couple_present"] = 1;
            foreach (var (key, value) in values) fields[key] = value;
        }
    }

    private static bool PresenceFromUserNumber(PacketTables tables, string build)
    {
        var value = tables.ValuesOf(build).GetValueOrDefault("couple_presence_from_user_number");
        return value switch
        {
            null => false,
            bool enabled => enabled,
            string text when bool.TryParse(text, out var enabled) => enabled,
            _ => throw new BubbleFighter.Fantasy.Abstractions.ConfigException(
                $"Build '{build}' couple_presence_from_user_number must be a boolean."),
        };
    }
}
