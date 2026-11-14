using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the earlier CN laboratory widths, native names and closed-only operation surface.</summary>
public sealed class TransitionalLabDialectTests
{
    private const string Request = "gcpReqEnchant";
    private const string Reply = "gspRepEnchant";
    private const string RequestBody = "0200000000010000FFFFFFFF05000000";
    private const string ClosedBody = "0400000000000000000000000000000000000000";
    private const string ConfigurationBody =
        "00000000000000000000000000000000050000009FE602009AE602009BE602009CE602009DE60200718D01000000000000000000000000000100000001000000010000000000000000000000";

    /// <summary>Both operation and empty configuration requests keep their native registered spellings and numeric spaces.</summary>
    [Theory]
    [InlineData("cn-735", 144, 148)]
    [InlineData("cn-738", 144, 149)]
    [InlineData("cn-742", 145, 150)]
    public void NativeNamesResolveToTheAuditedNumbers(string build, int enchantReply, int configurationReply)
    {
        var table = LabTestTables.Opcodes(build);
        Assert.Equal((ushort)132, table.C2S(Request));
        Assert.Equal((ushort)enchantReply, table.S2C(Reply));
        Assert.Equal((ushort)136, table.C2S("gcpReqSciendLabConfig"));
        Assert.Equal((ushort)configurationReply, table.S2C("gspRepSciendLabConfig"));
        Assert.All(table.All.Where(row => row.Name is Request or Reply or "gcpReqSciendLabConfig" or "gspRepSciendLabConfig"),
            row => Assert.Equal(Evidence.Verified, row.Evidence));
    }

    /// <summary>Only the audited enchant route is bound, and its closed reply survives opcode remapping and unloads cleanly.</summary>
    [Theory]
    [InlineData("cn-735", false)]
    [InlineData("cn-735", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-738", true)]
    [InlineData("cn-742", false)]
    [InlineData("cn-742", true)]
    public async Task EnchantHasOnlyTheWordSizedClosedFallback(string build, bool remap)
    {
        var original = LabTestTables.Opcodes(build);
        var table = remap ? new OpcodeTable(original.Profile, original.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : original;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new LabBinding(protocol, table, TestBuilds.Packets, build);
        try
        {
            binding.Mount(context);
            Assert.Equal((protocol, table.C2S(Request)), Assert.Single(fixture.Packets.Handlers).Key);
            Assert.Single(fixture.Services.GetAll<IMessageSender<LabOperationRefused>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S(Request), Convert.FromHexString(RequestBody)), default);
            var decoded = Assert.IsType<LabOperationRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(LabOperation.Enchant, decoded.Operation);
            Assert.Equal(new long[] { 256, -1 }, decoded.Keys);
            Assert.Equal(5, decoded.Argument);
            Assert.Equal(table.S2C(Reply), Assert.Single(session.Sent));
            Assert.Equal(ClosedBody, Convert.ToHexString(Assert.Single(session.Bodies)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await binding.SendAsync(session, new LabOperationRefused(LabOperation.Compound), default));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await binding.SendAsync(session, new LabEnchantCompleted(true, [PetArticleKeys.First]), default));
            Assert.Single(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationRefused>>());
    }

    /// <summary>A typed handler can own the earlier request without receiving a second reply from the fallback.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task TypedHandlerOwnsTheWordSizedRequest(string build)
    {
        var table = LabTestTables.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S(Request), Convert.FromHexString(RequestBody)), default);
            Assert.IsType<LabOperationRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Request identifiers and both reply lists stay four bytes while the reply tail keeps all eight bytes.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void OperationTablesPinBothWordListsAndTheWideTail(string build)
    {
        var request = PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabRequest", new Dictionary<string, object?>
        {
            ["keys"] = new object[] { 256, -1 }, ["argument"] = 5,
        });
        Assert.Equal(16, request.Length);
        Assert.Equal(RequestBody, Convert.ToHexString(request));
        var decodedRequest = PacketCodec.Read(TestBuilds.Packets, build, "ScienceLabRequest", request);
        Assert.Equal(request, PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabRequest", decodedRequest));
        const string replyBody = "040000000200000004030201FFFFFFFF0100000088776655EFCDAB8967452301";
        var reply = PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabReply", new Dictionary<string, object?>
        {
            ["result"] = 4, ["unknown_8"] = new object[] { 0x01020304, -1 },
            ["unknown_24"] = new object[] { 0x55667788 }, ["unknown_40"] = 0x0123456789ABCDEFL,
        });
        Assert.Equal(32, reply.Length);
        Assert.Equal(replyBody, Convert.ToHexString(reply));
        var decodedReply = PacketCodec.Read(TestBuilds.Packets, build, "ScienceLabReply", reply);
        Assert.Equal(new object?[] { 0x01020304, -1 }, (object?[])decodedReply["unknown_8"]!);
        Assert.Equal(new object?[] { 0x55667788 }, (object?[])decodedReply["unknown_24"]!);
        Assert.Equal(0x0123456789ABCDEFL, decodedReply["unknown_40"]);
        Assert.Equal(reply, PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabReply", decodedReply));
    }

    /// <summary>The extra sixth bool32 begins in CN738 and remains present in its CN742 descendant.</summary>
    [Theory]
    [InlineData("cn-735", 76)]
    [InlineData("cn-738", 80)]
    [InlineData("cn-742", 80)]
    public void ConfigurationTablesPinFiveThenSixBooleanWords(string build, int length)
    {
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabConfigurationRequest", new Dictionary<string, object?>()));
        var body = PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabConfiguration", new Dictionary<string, object?>
        {
            ["enchant_materials"] = new object[] { 190111, 190106, 190107, 190108, 190109 },
            ["additive_item_id"] = 101745,
        });
        Assert.Equal(length, body.Length);
        Assert.Equal(ConfigurationBody + (length == 80 ? "00000000" : ""), Convert.ToHexString(body));
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "ScienceLabConfiguration", body);
        Assert.Equal(new object?[] { 190111, 190106, 190107, 190108, 190109 }, (object?[])decoded["enchant_materials"]!);
        Assert.Equal(101745, decoded["additive_item_id"]);
        Assert.Equal(length == 80, decoded.ContainsKey("unknown_9"));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "ScienceLabConfiguration", decoded));
    }

    /// <summary>Malformed earlier requests are rejected before typed dispatch or a closed reply can occur.</summary>
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task MalformedWordBodiesDoNotDispatchOrSend(string build)
    {
        var table = LabTestTables.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            var valid = Convert.FromHexString(RequestBody);
            byte[][] invalid = [[], valid[..^1], [.. valid, 0], Convert.FromHexString("0110000000000000")];
            foreach (var body in invalid)
                await Assert.ThrowsAsync<PacketFormatException>(async () =>
                    await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S(Request), body), default));
            Assert.Empty(fixture.Messages.Received);
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }
}
