using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks that room, party and plaza projections retain the same committed native modifier vector.</summary>
public sealed class EquipmentSceneAttributeTests
{
    private static readonly RoomItemAttribute Modifier = new(203100, 7, [1, 2, 3, 4], uint.MaxValue, "A");

    /// <summary>Both party writers preserve appearance modifiers while membership-only updates leave profiles absent.</summary>
    [Theory]
    [InlineData("kr-1256", "unknown_n6")]
    [InlineData("cn-774", "unknown_56")]
    [InlineData("tw-454", "unknown_44")]
    public void PartySceneRefreshPreservesNativeAttributes(string build, string attributeField)
    {
        var seat = new RoomMemberView(0, 42, "member", 1, 0, false, 300001, [300001, 203100]) { ItemAttributes = [Modifier] };
        var party = new PartySnapshot([new(42, "member")]);
        var message = new PartyUpdated(party, new(42, 300001, seat.EquippedItemIds) { ItemAttributes = [Modifier] });
        foreach (var bytes in new[]
        {
            RoomRecords.Party(TestBuilds.Packets, build, seat),
            PartyBinding.WriteParty(TestBuilds.Packets, build, message),
        })
        {
            var fields = PacketCodec.Read(TestBuilds.Packets, build, "SoloPartySnapshot", bytes);
            CheckProfile(OnlyRecord(fields["profile_entries"]), attributeField);
        }
        var membershipOnly = PacketCodec.Read(TestBuilds.Packets, build, "SoloPartySnapshot",
            PartyBinding.WriteParty(TestBuilds.Packets, build, party));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(membershipOnly["profile_entries"]));
    }

    /// <summary>The full modern plaza install and the subsequent room seat update carry identical attribute values.</summary>
    [Theory]
    [InlineData("cn-774", "unknown_56")]
    [InlineData("tw-454", "unknown_44")]
    public void ModernPlazaInstallAndSeatUpdatePreserveNativeAttributes(string build, string attributeField)
    {
        var seat = new RoomMemberView(0, 42, "member", 1, 0, false, 300001, [300001, 203100]) { ItemAttributes = [Modifier] };
        var installed = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom",
            PlazaRecords.Install(TestBuilds.Packets, build, [seat], 0, 3, false));
        CheckProfile(OnlyRecord(installed["equipped_objects"]), attributeField);
        var changed = PacketCodec.Read(TestBuilds.Packets, build, "RoomSeatUpdate",
            RoomRecords.SeatUpdate(TestBuilds.Packets, build, seat, [seat]));
        CheckProfile(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(changed["equipment"]), attributeField);
    }

    /// <summary>Legacy plaza installation and equipped-seat updates retain modifiers beside the same worn item IDs.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    public void LegacyPlazaInstallAndSeatUpdatePreserveNativeAttributes(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var member = new PlazaMember(0, 42, "member", 1, 300001, [300001, 203100]) { ItemAttributes = [Modifier] };
        var installed = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom",
            PlayRoomCodec.WritePlazaInstall(TestBuilds.Packets, build, schema, new(member, [])));
        CheckProfile(OnlyRecord(installed["equipped_item_lists"]), "unknown_48");
        var changed = PacketCodec.Read(TestBuilds.Packets, build, "RoomSeatUpdate",
            PlayRoomCodec.WritePlazaSeatSlot(TestBuilds.Packets, build, schema, member));
        CheckProfile(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(changed["equipment"]), "unknown_48");
    }

    private static IReadOnlyDictionary<string, object?> OnlyRecord(object? value) =>
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(value)));

    private static void CheckProfile(IReadOnlyDictionary<string, object?> profile, string field)
    {
        Assert.Contains(203100, Assert.IsAssignableFrom<IReadOnlyList<object?>>(profile["equipped_items"]));
        var attribute = OnlyRecord(profile[field]);
        Assert.Equal(Convert.FromHexString("5C190300070000000400000001000000020000000300000004000000FFFFFFFF010000004100"),
            PacketCodec.Write(TestBuilds.Packets, "kr-812", "ItemAttr", attribute));
    }
}
