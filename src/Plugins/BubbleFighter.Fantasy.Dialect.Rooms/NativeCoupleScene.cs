using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Projects partner identity into the native scene record without deriving it from a display level.</summary>
internal static class NativeCoupleScene
{
    internal static int PartnerUserNumber(CoupleSceneState couple)
    {
        ArgumentNullException.ThrowIfNull(couple);
        if (couple.PartnerUserNum is <= 0 or > int.MaxValue || string.IsNullOrWhiteSpace(couple.PartnerName))
            throw new ArgumentException("A couple scene requires a positive native account identity and partner name.", nameof(couple));
        return checked((int)couple.PartnerUserNum);
    }

    internal static void Project(PacketTables tables, string build, IDictionary<string, object?> values,
        CoupleSceneState? couple)
    {
        if (!CoupleSlotBinding.Supported(tables, build)) return;
        var fields = tables.Resolve(build, "UserSlot");
        var partner = fields.SingleOrDefault(field => field.Name == "couple_partner_user_num");
        if (partner is null) return;
        // kr-1256 sub_921790, kr-812 sub_6CEF10, cn-774 sub_B33980 and tw-454 sub_8E5500 match account IDs.
        values[partner.Name] = couple is null ? unchecked((int)partner.Default) : PartnerUserNumber(couple);
        values["couple_partner_name"] = couple?.PartnerName ?? string.Empty;
        // The first flag enables extra native text; neither flag is required for the mutual-pair icon.
        values["couple_unknown_flag_1"] = false;
        values["couple_unknown_flag_2"] = false;
    }
}
