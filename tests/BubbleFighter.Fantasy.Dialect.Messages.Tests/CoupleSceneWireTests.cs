using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Couple scene identity follows each native slot reader, independently of player level or equipment.</summary>
public sealed class CoupleSceneWireTests
{
    private static RoomMemberView Member(CoupleSceneState? couple) =>
        new(0, 42, "self", 19, 1, false, 300001, [300001]) { Couple = couple };

    /// <summary>TW410 identifies a partner by account; relationship dates and rings do not overwrite losses.</summary>
    [Fact]
    public void Tw410PartnerProfilePinsItsOwnDateRingAndOnlineFields()
    {
        var fields = new Dictionary<string, object?> { ["user_number"] = 77, ["primary_losses"] = 29 };
        var created = new DateTimeOffset(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local));
        CoupleProfileWire.AddToFighter(TestBuilds.Packets, "tw-410", fields, new(created, 100176, true));
        var body = PacketCodec.Write(TestBuilds.Packets, "tw-410", "FighterInfo", fields);
        Assert.Equal(Convert.FromHexString("0000000000000040508701000100000000000000"), body.AsSpan(28, 20).ToArray());
        Assert.Equal(Convert.FromHexString("1D000000"), body.AsSpan(52, 4).ToArray());
        var decoded = PacketCodec.Read(TestBuilds.Packets, "tw-410", "FighterInfo", body);
        Assert.Equal(77, Convert.ToInt32(decoded["user_number"]));
        Assert.False(decoded.ContainsKey("couple_present"));
        Assert.All(TestBuilds.Packets.Resolve("tw-410", "FighterInfo"), field => Assert.Equal("tw-410", field.Origin));
    }

    /// <summary>KR1256 consumes one account and one narrow record, without a list count or full seat.</summary>
    [Fact]
    public void Kr1256UpdatePinsTheNarrowRelationshipBody()
    {
        Assert.Equal(Convert.FromHexString("2A0000004D0000000100000041000000000000000000"),
            CoupleSlotBinding.Write(TestBuilds.Packets, "kr-1256", Member(new(77, "A"))));
        Assert.Equal(Convert.FromHexString("2A00000000000000000000000000000000000000"),
            CoupleSlotBinding.Write(TestBuilds.Packets, "kr-1256", Member(null)));
    }

    /// <summary>These consumers read one counted full seat, then copy the identity/name/flags at their own offsets.</summary>
    [Theory]
    [InlineData("kr-812", 124)]
    [InlineData("cn-774", 126)]
    [InlineData("tw-454", 120)]
    public void FullSlotUpdatesPinCountAndRelationshipBytes(string build, int relationshipWireOffset)
    {
        // Own UserSlot readers: kr-812 sub_4196F0, cn-774 sub_493E20, tw-454 sub_48BD60.
        // Offsets include the vector count, four-character nickname and the KR812 one-item worn vector.
        var body = CoupleSlotBinding.Write(TestBuilds.Packets, build, Member(new(77, "A")));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, body[..4]);
        Assert.Equal(Convert.FromHexString("4D0000000100000041000000000000000000"),
            body.AsSpan(relationshipWireOffset, 18).ToArray());
        var seat = OnlySeat(PacketCodec.Read(TestBuilds.Packets, build, "CoupleSlotUpdate", body)["user_slots"]);
        CheckPartner(seat, 77, "A");
        var cleared = CoupleSlotBinding.Write(TestBuilds.Packets, build, Member(null));
        Assert.Equal(Convert.FromHexString("FFFFFFFF000000000000000000000000"),
            cleared.AsSpan(relationshipWireOffset, 16).ToArray());
        CheckPartner(OnlySeat(PacketCodec.Read(TestBuilds.Packets, build, "CoupleSlotUpdate", cleared)["user_slots"]), -1, "");
    }

    /// <summary>Older scene consumers retain their own compact or expanded relationship tail.</summary>
    [Theory]
    [InlineData("cn-717", 124, true, true)]
    [InlineData("cn-706", 120, true, true)]
    [InlineData("cn-406", 120, false, false)]
    [InlineData("cn-404", 120, true, false)]
    [InlineData("cn-705", 120, true, true)]
    [InlineData("id-1809", 120, true, true)]
    [InlineData("cn-408", 120, true, true)]
    [InlineData("id-400", 120, false, false)]
    [InlineData("cn-324", 120, false, false)]
    [InlineData("tw-410", 120, false, false)]
    public void OlderUpdatesKeepTheirOwnRelationshipShape(string build, int offset, bool name, bool secondFlag)
    {
        var body = CoupleSlotBinding.Write(TestBuilds.Packets, build, Member(new(77, "A")));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, body[..4]);
        Assert.Equal(Convert.FromHexString("4D000000"), body.AsSpan(offset, 4).ToArray());
        var seat = OnlySeat(PacketCodec.Read(TestBuilds.Packets, build, "CoupleSlotUpdate", body)["user_slots"]);
        Assert.Equal(name, seat.ContainsKey("couple_partner_name"));
        Assert.Equal(secondFlag, seat.ContainsKey("couple_unknown_flag_2"));
        CheckPartner(seat, 77, "A");
        var cleared = CoupleSlotBinding.Write(TestBuilds.Packets, build, Member(null));
        Assert.Equal(Convert.FromHexString("FFFFFFFF"), cleared.AsSpan(offset, 4).ToArray());
    }

    /// <summary>Initial room and plaza installs retain the same committed partner identity as subsequent updates.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    [InlineData("cn-717")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-705")]
    [InlineData("id-1809")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RoomAndPlazaRecordsCarryPartnerIdentityRatherThanLevel(string build)
    {
        var member = Member(new(77, "A"));
        byte[] roomBody;
        byte[] plazaBody;
        if (build is not ("kr-1256" or "cn-774" or "tw-454"))
        {
            var schema = TestBuilds.Of(build).Schema;
            var slot = new RoomSlotValues(true, false, 42, "self", 300001, [300001], false)
                { Couple = member.Couple, Level = 19 };
            roomBody = PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", RoomCodec.PlaySeatValues(schema, slot, false));
            plazaBody = PlayRoomCodec.WritePlazaSeatRecord(TestBuilds.Packets, build, schema,
                new(0, 42, "self", 19, 300001, [300001]) { Couple = member.Couple });
        }
        else
        {
            roomBody = PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", PlazaRecords.Seat(TestBuilds.Packets, build, member));
            var install = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom",
                PlazaRecords.Install(TestBuilds.Packets, build, [member], 0, 3, false));
            plazaBody = PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", OnlySeat(install["user_slots"]));
        }
        foreach (var body in new[] { roomBody, plazaBody })
        {
            var seat = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", body);
            CheckPartner(seat, 77, "A");
            if (build is not ("cn-774" or "tw-454")) Assert.Equal(19, Convert.ToInt32(seat["level"]));
            else Assert.False(seat.ContainsKey("level"));
        }
    }

    /// <summary>Recipient-local game bindings publish both scene types and refuse another build before transport.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    [InlineData("cn-717")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-705")]
    [InlineData("id-1809")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task TypedSendersUseTheRecipientsOpcodeAndUnload(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        var binding = new CoupleSlotBinding(build + "/game", table, TestBuilds.Packets, build);
        try
        {
            binding.Mount(context);
            var room = Assert.Single(fixture.Services.GetAll<IMessageSender<RoomCoupleUpdated>>());
            var plaza = Assert.Single(fixture.Services.GetAll<IMessageSender<PlazaCoupleUpdated>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await room.SendAsync(session, new(Member(new(77, "A"))), default);
            await plaza.SendAsync(session, new(new(0, 42, "self", 19, 300001, [300001]) { Couple = new(77, "A") }), default);
            Assert.Equal(new[] { table.S2C("gspUpdateCoupleSlot"), table.S2C("gspUpdateCoupleSlot") }, session.Sent);
            var other = new MatchMessagesMountTests.Session(TestBuilds.Opcodes(build == "kr-812" ? "kr-1256" : "kr-812").Profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => room.SendAsync(other, new(Member(null)), default).AsTask());
            Assert.Empty(other.Bodies);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomCoupleUpdated>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<PlazaCoupleUpdated>>());
    }

    /// <summary>Having the baseline's inherited update row alone does not prove this client's update consumer.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-735")]
    public void InheritedUpdatesRemainUnavailable(string build)
    {
        Assert.False(CoupleSlotBinding.Supported(TestBuilds.Packets, build));
    }

    /// <summary>Removing or retyping a partner field cannot turn a malformed seat into an accepted level-free layout.</summary>
    [Theory]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public void LevelFreeLayoutsRequireTheCompleteProvenRelationship(string build, bool missing)
    {
        var directory = PacketTableEdits.WithFieldType("UserSlot@" + build, "couple_partner_name", "wstr", "u32");
        if (missing)
        {
            var path = Path.Combine(directory.FullName, "room.yaml");
            var text = File.ReadAllText(path);
            var start = text.IndexOf("  UserSlot@" + build + ":", StringComparison.Ordinal);
            var target = text.IndexOf("- couple_partner_name: u32", start, StringComparison.Ordinal);
            text = text.Remove(target, "- couple_partner_name: u32".Length).Insert(target, "- unproven_partner_name: u32");
            File.WriteAllText(path, text);
        }
        var tables = PacketTables.Load(directory.FullName);
        Assert.Throws<ConfigException>(() => MainMenuInstall.RequireLayouts(tables, build));
        Assert.Throws<ConfigException>(() => CoupleSlotBinding.RequireLayouts(tables, build));
    }

    private static IReadOnlyDictionary<string, object?> OnlySeat(object? vector) =>
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(vector)));

    private static void CheckPartner(IReadOnlyDictionary<string, object?> values, int account, string name)
    {
        Assert.Equal(account, Convert.ToInt32(values["couple_partner_user_num"]));
        if (values.TryGetValue("couple_partner_name", out var partnerName)) Assert.Equal(name, partnerName);
        if (values.TryGetValue("couple_unknown_flag_1", out var first)) Assert.False(Assert.IsType<bool>(first));
        if (values.TryGetValue("couple_unknown_flag_2", out var second)) Assert.False(Assert.IsType<bool>(second));
    }
}
