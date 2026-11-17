using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Couple adapters retain each audited build's request, result, appearance and breakup contract.</summary>
public sealed class CoupleBindingTests
{
    /// <summary>Each compact client has its own empty eligibility request and bool/name target check before confirmation.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    public void CompactPreflightUsesItsNativeSequenceAndResultCodes(string build)
    {
        var tables = TestBuilds.Packets;
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)938, opcodes.C2S("gcpReqCoupleInviterStatus"));
        Assert.Equal((ushort)934, opcodes.C2S("gcpReqCoupleInvite"));
        Assert.Equal((ushort)935, opcodes.C2S("gcpReqCoupleConfirmInvite"));
        Assert.Empty(PacketCodec.Read(tables, build, "CoupleInviterStatusRequest", []));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() =>
            PacketCodec.Read(tables, build, "CoupleInviterStatusRequest", new byte[] { 0 }));
        Assert.Equal(new CoupleInitialInviteRequested(true, "Bob"), CoupleBinding.ReadInitialInvite(tables, build,
            Convert.FromHexString("010000000300000042006F006200")));
        var binding = new CoupleBinding($"{build}/game", opcodes, tables, build);
        Assert.Equal("05000000", Convert.ToHexString(binding.WriteInviterStatus(CoupleInviterStatus.Ready)));
        Assert.Equal("07000000", Convert.ToHexString(binding.WriteInviterStatus(CoupleInviterStatus.Unavailable)));
        Assert.Equal("0A000000", Convert.ToHexString(binding.WriteInviterStatus(CoupleInviterStatus.Paired)));
        Assert.Equal("0B000000", Convert.ToHexString(binding.WriteInviterStatus(CoupleInviterStatus.Busy)));
        Assert.Equal("050000000300000042006F006200", Convert.ToHexString(binding.WriteConfirmation("Bob")));
    }

    /// <summary>The proposal is read as kr-1256 sub_E880A0 writes it; the answer's second flag is the acceptance.</summary>
    [Fact]
    public void RequestsAreReadByFieldName()
    {
        CoupleBinding.RequireLayouts(TestBuilds.Packets, "kr-1256");
        var proposal = CoupleBinding.ReadProposal(TestBuilds.Packets, "kr-1256",
            Convert.FromHexString("01000000" + "020000004200" + "4300" + "39300000" + "010000004D00"));
        Assert.Equal(new CoupleProposed(true, "BC", 12345, "M"), proposal);
        // kr-1256 sub_E83FE0 sets the second byte after a Yes answer.
        var answer = CoupleBinding.ReadAnswer(TestBuilds.Packets, "kr-1256",
            Convert.FromHexString("00000000" + "01000000" + "010000004100" + "010000004200"));
        Assert.Equal(new CoupleAnswered(true, "A", "B"), answer);
    }

    /// <summary>Every proposal result and breakup status has the build's code; a build without the table fails.</summary>
    [Fact]
    public void CodesAreTheBuildsOwn()
    {
        var results = CoupleBinding.ReadCodes<CoupleProposalResult>(TestBuilds.Packets, "kr-1256", "couple_proposal_results");
        Assert.Equal(8u, results[CoupleProposalResult.Sent]);
        Assert.Equal(2u, results[CoupleProposalResult.NotFound]);
        Assert.Equal(10u, results[CoupleProposalResult.SenderPaired]);
        var statuses = CoupleBinding.ReadCodes<CoupleBreakUpStatus>(TestBuilds.Packets, "kr-1256", "couple_breakup_statuses");
        Assert.Equal((0u, 1u, 2u), (statuses[CoupleBreakUpStatus.Done], statuses[CoupleBreakUpStatus.Refused], statuses[CoupleBreakUpStatus.Left]));
        var legacy = CoupleBinding.ReadCodes<CoupleBreakUpStatus>(TestBuilds.Packets, "kr-812", "couple_breakup_statuses");
        Assert.Equal((1u, 0u, 1u), (legacy[CoupleBreakUpStatus.Done], legacy[CoupleBreakUpStatus.Refused], legacy[CoupleBreakUpStatus.Left]));
        Assert.Equal(11u, CoupleBinding.ReadCodes<CoupleProposalResult>(TestBuilds.Packets, "kr-812", "couple_proposal_results")[CoupleProposalResult.Unavailable]);
        Assert.Equal(15u, results[CoupleProposalResult.Unavailable]);
        Assert.Throws<ConfigException>(() => CoupleBinding.ReadCodes<CoupleBreakUpStatus>(TestBuilds.Packets, "kr-812", "missing_couple_codes"));
    }

    /// <summary>The invitation carries the shown flag, the three names, the proposer's worn items and fighter record.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void InvitationCarriesTheProposersRecord(string build)
    {
        var binding = new CoupleBinding($"{build}/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build);
        var inviter = new PlayerIdentity(42, "inviter", "Ann", 7);
        var body = binding.WriteInvitation(new(inviter, new FighterAppearance(300003, [300003, 1001]), "Bob", "hi"));
        var read = PacketCodec.Read(TestBuilds.Packets, build, "CoupleInvite", body);
        Assert.Equal((true, "Ann", "Bob", "hi"), ((bool)read["shown"]!, read["inviter"], read["recipient"], read["message"]));
        var equipment = Assert.IsType<Dictionary<string, object?>>(read["equipped_items"]);
        Assert.Equal(300003, Convert.ToInt32(equipment["character_id"]));
        Assert.Equal(new object?[] { 300003, 1001 }, Assert.IsType<object?[]>(equipment["equipped_items"]).Select(Convert.ToInt32).Cast<object?>());
        var fighter = Assert.IsType<Dictionary<string, object?>>(read["fighter_info"]);
        Assert.Equal((42, "Ann", 3), (Convert.ToInt32(fighter["user_number"]), fighter["nickname"], Convert.ToInt32(fighter["character_type"])));
    }

    /// <summary>Legacy and CN/TW proposals contain nested appearance bytes; only server-owned state reaches the flow.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-1809")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ProposalConsumesButDoesNotTrustClientEquipment(string build)
    {
        CoupleBinding.RequireLayouts(TestBuilds.Packets, build);
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CoupleConfirmInviteRequest", new Dictionary<string, object?>
        {
            ["confirmed"] = true, ["target"] = "B", ["ring_item"] = 12345, ["message"] = "hi",
            ["equipped_items"] = new Dictionary<string, object?>
            {
                ["user_number"] = 999, ["character_item"] = 300099, ["character_id"] = 300099,
                ["equipped_items"] = new[] { 300099, 987654 },
            },
        });
        Assert.Equal(new CoupleProposed(true, "B", 12345, "hi"), CoupleBinding.ReadProposal(TestBuilds.Packets, build, body));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() =>
            CoupleBinding.ReadProposal(TestBuilds.Packets, build, body[..^1]));
    }

    /// <summary>KR812 accepts a true breakup flag, while modern replies use status words and native name ordering.</summary>
    [Theory]
    [InlineData("kr-812", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("kr-812", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("kr-812", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("kr-1256", CoupleBreakUpStatus.Done, "00000000010000004200")]
    [InlineData("kr-1256", CoupleBreakUpStatus.Left, "02000000010000004100")]
    [InlineData("cn-774", CoupleBreakUpStatus.Done, "01000000410001000000420000000000")]
    [InlineData("cn-774", CoupleBreakUpStatus.Refused, "01000000410001000000420001000000")]
    [InlineData("tw-454", CoupleBreakUpStatus.Left, "01000000410001000000420002000000")]
    [InlineData("cn-717", CoupleBreakUpStatus.Done, "000000000000000000000000")]
    [InlineData("cn-717", CoupleBreakUpStatus.Refused, "000000000000000001000000")]
    [InlineData("cn-717", CoupleBreakUpStatus.Left, "000000000000000002000000")]
    [InlineData("cn-706", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-706", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("cn-706", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("cn-406", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-406", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("cn-406", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("cn-404", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-404", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("cn-404", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("cn-705", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-705", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("cn-705", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("id-1809", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("id-1809", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("id-1809", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("cn-408", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-408", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("cn-408", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("id-400", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("id-400", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("id-400", CoupleBreakUpStatus.Left, "010000000000000000000000")]
    [InlineData("cn-324", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("cn-324", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    [InlineData("tw-410", CoupleBreakUpStatus.Done, "010000000000000000000000")]
    [InlineData("tw-410", CoupleBreakUpStatus.Refused, "000000000000000000000000")]
    public void BreakupWritesTheBuildsOwnSemantics(string build, CoupleBreakUpStatus status, string expected)
    {
        var binding = new CoupleBinding($"{build}/game", TestBuilds.Opcodes(build), TestBuilds.Packets, build);
        Assert.Equal(expected, Convert.ToHexString(binding.WriteBreakUp(new(status, "A", "B"))));
    }

    /// <summary>Each legacy activation has all native requests and complete layouts, with no invented ring request.</summary>
    [Theory]
    [InlineData("cn-717", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-705", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-408", false)]
    [InlineData("id-400", false)]
    public void LegacyAdaptersHaveTheirOwnRequiredOpcodesAndCodes(string build, bool hasRing)
    {
        CoupleBinding.RequireLayouts(TestBuilds.Packets, build, hasRing);
        var opcodes = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)934, opcodes.C2S("gcpReqCoupleConfirmInvite"));
        Assert.Equal((ushort)935, opcodes.C2S("gcpReqCoupleInviteExpire"));
        Assert.Equal((ushort)936, opcodes.C2S("gcpReqCoupleInviteAnswer"));
        Assert.Equal((ushort)937, opcodes.C2S("gcpReqCoupleBreakUp"));
        Assert.Equal((ushort)938, opcodes.S2C("gspResCoupleInvite"));
        Assert.Equal((ushort)939, opcodes.S2C("gspResCoupleInviteResult"));
        Assert.Equal((ushort)940, opcodes.S2C("gspResCoupleInviteAnswer"));
        Assert.Equal((ushort)941, opcodes.S2C("gspResCoupleBreakUp"));
        Assert.Equal((ushort)942, opcodes.S2C("gspResCoupleNotifyPartner"));
        var results = CoupleBinding.ReadCodes<CoupleProposalResult>(TestBuilds.Packets, build, "couple_proposal_results");
        Assert.Equal(8u, results[CoupleProposalResult.Sent]);
        Assert.Equal(11u, results[CoupleProposalResult.Unavailable]);
        if (hasRing) Assert.Equal((ushort)954, opcodes.C2S("gcpReqChangeCouplering"));
        else Assert.Empty(TestBuilds.Packets.Resolve(build, "CoupleChangeRingRequest"));
    }

    /// <summary>Only builds with native requests, full invitations and result semantics enable the couple adapter.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("kr-812", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-279", false)]
    [InlineData("kr-186", false)]
    [InlineData("tw-410", true)]
    [InlineData("cn-324", true)]
    [InlineData("cn-406", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-705", true)]
    [InlineData("cn-408", true)]
    [InlineData("id-400", true)]
    [InlineData("cn-735", false)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void CapabilityMarksTheBuildsWithCouples(string build, bool couples) =>
        Assert.Equal(couples, TestBuilds.Packets.CapabilityOf(build, "couple"));
}
