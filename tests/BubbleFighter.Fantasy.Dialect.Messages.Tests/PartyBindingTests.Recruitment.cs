using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class PartyBindingTests
{
    /// <summary>The supported native recruitment families and their own seat flag locations.</summary>
    public static readonly TheoryData<string, int, int> RecruitmentBuilds = new()
    {
        { "cn-279", 104, 112 }, { "cn-324", 108, 116 }, { "cn-404", 140, 148 },
        { "cn-406", 136, 144 }, { "cn-408", 140, 148 }, { "cn-706", 140, 148 },
        { "cn-717", 140, 148 }, { "id-400", 140, 148 }, { "id-1809", 140, 148 },
        { "kr-812", 140, 148 }, { "kr-1256", 104, 112 }, { "cn-774", 128, 136 }, { "tw-454", 128, 136 },
    };

    /// <summary>Each native request route reads its complete body, sends a seat update, and unloads all registrations.</summary>
    [Theory]
    [MemberData(nameof(RecruitmentBuilds))]
    public async Task RecruitmentRoutesUseNativeBodiesAndSeatFlags(string build, int gate, int active)
    {
        _ = Binding(build, out var table);
        var binding = new PlazaRecruitmentBinding(build + "/game", table, TestBuilds.Packets, build, NullLogger.Instance);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var router = services.Register<IPacketRouter>(routes);
        using var dispatch = services.Register<IMessageRouter>(messages);
        var context = new PluginContext(new PluginRow { Id = "recruitment", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var session = new Session();
        var document = build is "kr-1256" or "cn-774";
        try
        {
            binding.Mount(context);
            var name = document ? "gcpReqPublishPartyRecruitment_" : "gcpReqPartyRecruiting";
            var opcode = table.C2S(name);
            var body = document ? Bytes(b => { Word(b, 0); Word(b, 4); b.Add(2); Text(b, "Go"); }) : W(1);
            Assert.Equal(document ? 17 : 4, body.Length);
            var route = routes.Handlers[(build + "/game", opcode)];
            await route(session, new(opcode, body), default);
            var request = Assert.IsType<PlazaRecruitmentRequested>(Assert.Single(messages.Received));
            Assert.True(request.Active);
            Assert.Equal(document ? "Go" : null, request.Text);
            if (document) Assert.Equal((4, (byte)2), (request.MemberLimit, request.Category));
            await Assert.ThrowsAsync<PacketFormatException>(() => route(session, new(opcode, body[..^1]), default).AsTask());
            await Assert.ThrowsAsync<PacketFormatException>(() => route(session, new(opcode, (byte[])[.. body, 0]), default).AsTask());
            if (!document)
            {
                Assert.Throws<PacketFormatException>(() => binding.ReadRequest(W(2)));
                var textOpcode = table.C2S("gcpRoomSendRecMsg");
                await routes.Handlers[(build + "/game", textOpcode)](session, new(textOpcode, T("Go")), default);
                Assert.Equal(new PlazaRecruitmentTextRequested("Go"), messages.Received[^1]);
                await binding.SendAsync(session, new PlazaRecruitmentTextReceived(3, "Go"), default);
                Assert.Equal((byte[])[3, 0, 2, 0, 0, 0, (byte)'G', 0, (byte)'o', 0], session.Sent[^1].Body);
            }
            else
            {
                var listOpcode = table.C2S("gcpReqPartyRecruitDocuments_");
                await routes.Handlers[(build + "/game", listOpcode)](session, new(listOpcode, new byte[] { 2, 3, 0, 0, 0 }), default);
                Assert.Equal(new PlazaRecruitmentListRequested(2, 3), messages.Received[^1]);
                await binding.SendAsync(session, new PlazaRecruitmentRejected(), default);
                Assert.Equal(new byte[] { 0 }, session.Sent[^1].Body);
            }
            var member = new PlazaMember(3, 77, "Ab", 10, 300001, [300001])
                { Recruitment = new(true, true, "Go") { MemberLimit = 4 } };
            await binding.SendAsync(session, new PlazaRecruitmentChanged(member), default);
            var fields = PacketCodec.Read(TestBuilds.Packets, build, "RoomSeatLeave", session.Sent[^1].Body);
            Assert.Equal(3, fields["slot"]);
            var user = Assert.IsType<Dictionary<string, object?>>(fields["user"]);
            Assert.Equal(77, user["user_number"]);
            Assert.Equal(true, user["unknown_" + gate]);
            Assert.Equal(true, user["unknown_" + active]);
            if (document) Assert.Equal("Go", user["unknown_" + (active + 4)]);
            var declared = typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!.ConditionalProvidesMany;
            Assert.Empty(typeof(PlazaRecruitmentBinding).GetInterfaces().Where(type => type.IsGenericType).Except(declared));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<PlazaRecruitmentChanged>>());
    }

    /// <summary>The document list pins the native nested strings, endpoint, category, count and page suffix.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public async Task RecruitmentDocumentsPinTheNativeReplyAndDirectJoinKind(string build)
    {
        var party = Binding(build, out var table);
        var binding = new PlazaRecruitmentBinding(build + "/game", table, TestBuilds.Packets, build, NullLogger.Instance);
        var session = new Session();
        var member = new PlazaMember(3, 77, "Ab", 10, 300001, [300001])
            { Recruitment = new(true, true, "Go") { MemberLimit = 4, Category = 2 } };
        await binding.SendAsync(session, new PlazaRecruitmentListReceived([new(member, 2)], 17, 1), default);
        var expected = Bytes(b =>
        {
            Word(b, 1); Word(b, 77); Text(b, "Ab"); Word(b, 0); Word(b, 0); b.AddRange(new byte[2]);
            Text(b, "Go"); b.Add(2); Word(b, 2); Word(b, 4); Word(b, 0); Word(b, 17); Word(b, 1);
        });
        Assert.Equal(55, expected.Length);
        Assert.Equal(expected, Assert.Single(session.Sent).Body);
        var services = new ServiceRegistry();
        var routes = new Routes();
        var messages = new Messages();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "party", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            party.Bind(context, messages);
            var opcode = table.C2S("gcpReqMoveToInviter");
            var body = Bytes(b => { Text(b, "Ab"); Word(b, 0); b.AddRange(new byte[2]); b.Add(2); });
            await routes.Handlers[(build + "/game", opcode)](session, new(opcode, body), default);
            Assert.Equal(new PartyRecruitmentJoinRequested("Ab"), Assert.Single(messages.Received));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Taiwan's native zero-valued failure codes remain valid and distinct from successful invitations.</summary>
    [Fact]
    public async Task TaiwanFailureMapsPreserveNativeZeroValues()
    {
        var binding = Binding("tw-454", out _);
        var session = new Session();
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.Self, "Al"), default);
        await binding.SendAsync(session, new PartyVisitFailed(PartyVisitFailure.Offline, "Al"), default);
        Assert.Equal(Bytes(b => { Text(b, "Al"); Word(b, 0); }), session.Sent[1].Body);
        Assert.Equal(Bytes(b => { Text(b, "Al"); Word(b, 0); }), session.Sent[0].Body);
    }
}
