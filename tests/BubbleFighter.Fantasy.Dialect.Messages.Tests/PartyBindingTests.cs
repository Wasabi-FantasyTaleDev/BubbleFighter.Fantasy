using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the legacy party packet bodies and the routes each build mounts.</summary>
public sealed class PartyBindingTests
{
    public static readonly TheoryData<string> PartyBuilds = new() { "kr-812", "cn-717", "cn-324", "cn-279", "id-400", "id-1809", "cn-706", "cn-404", "cn-408" };

    /// <summary>The party list is the state word, the counted members (user, nickname, two unknown words and level) and an empty equipped list.</summary>
    [Fact]
    public void PartyListCarriesEveryMemberLeaderFirst()
    {
        var bytes = PartyBinding.WriteParty(TestBuilds.Packets, "kr-812", new PartySnapshot([new(7, "Ab"), new(9, "C")]));
        var expected = new List<byte>();
        Word(expected, 2);
        Word(expected, 2);
        foreach (var (user, nick) in new[] { (7, "Ab"), (9, "C") })
        {
            Word(expected, user);
            Text(expected, nick);
            Word(expected, 0);
            Word(expected, 0);
            Word(expected, 1);
        }
        Word(expected, 0);
        Assert.Equal(58, bytes.Length);
        Assert.Equal(expected.ToArray(), bytes);
    }

    /// <summary>The final member word preserves the actual rank rather than clearing the badge on an update.</summary>
    [Theory]
    [MemberData(nameof(PartyBuilds))]
    public void PartyMemberRankIsWrittenAtTheNativeSlot(string build)
    {
        var bytes = PartyBinding.WriteParty(TestBuilds.Packets, build,
            new PartySnapshot([new(7, "Ab", 17), new(9, "Cd", 42)]));
        // Each member is user (4), counted UTF-16 nickname (8), then two unknown words and its rank (12).
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52)));
        Assert.Equal(60, bytes.Length);
    }

    /// <summary>Every party request decodes from its native body in each build.</summary>
    [Theory]
    [MemberData(nameof(PartyBuilds))]
    public void RequestsDecodeTheNativeBodies(string build)
    {
        var binding = Binding(build, out _);
        var invite = new List<byte>();
        Word(invite, 1);
        Text(invite, "Bob");
        Assert.Equal(new PartyInviteRequested(true, "Bob"), binding.ReadInvite(invite.ToArray()));

        var accept = new List<byte>();
        Word(accept, 1);
        Text(accept, "Al");
        Word(accept, 0);
        accept.AddRange(new byte[2]);
        Assert.Equal(18, accept.Count);
        Assert.Equal(new PartyInviteAccepted(true, "Al"), binding.ReadAccept(accept.ToArray()));
        Assert.Throws<PacketFormatException>(() => binding.ReadAccept(accept.ToArray()[..^1]));
    }

    /// <summary>The refuse answer (wstr inviter, u32 answer) exists where the build has the class.</summary>
    [Fact]
    public void AnswerCarriesTheInviterAndTheAnswerCode()
    {
        var answer = new List<byte>();
        Text(answer, "Al");
        Word(answer, 3);
        Assert.Equal(new PartyInviteAnswered("Al", 3), Binding("kr-812", out _).ReadAnswer(answer.ToArray()));
    }

    /// <summary>Each build mounts its own numbers; cn-324 and cn-279 have no answer class, so their C2S 111 stays unrouted.</summary>
    [Theory]
    [MemberData(nameof(PartyBuilds))]
    public async Task EveryBuildMountsItsPartyRoutes(string build)
    {
        var binding = Binding(build, out var table);
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "party-binding", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            binding.Bind(context, new Messages());
            foreach (var name in PartyBinding.Requests)
                Assert.True(routes.Handlers.ContainsKey((build + "/game", table.C2S(name))), name);
            var answer = table.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriendAccept");
            Assert.Equal(build is not ("cn-324" or "cn-279"), answer is not null);
            // cn-324 and cn-279 visit directly; their C2S 111 belongs to matching, not a visit-answer packet.
            var visit = table.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriend");
            Assert.NotNull(visit);
            Assert.Equal(PartyBinding.Requests.Length + 1 + (answer is null ? 0 : 1), routes.Handlers.Count);
            Assert.Equal(answer is null ? 0 : 1, services.GetAll<IInboundMessageBinding<PartyVisitAnswered>>().Count);
            Assert.Equal(answer is null ? 0 : 1, services.GetAll<IMessageSender<PartyVisitAsked>>().Count);
            foreach (var name in PartyBinding.Replies)
                Assert.NotNull(table.Find(PacketDirection.ServerToClient, name));
            Assert.Single(services.GetAll<IMessageSender<PartyUpdated>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<PartyUpdated>>());
    }

    /// <summary>Direct Find completion has its own native success result and trailing word.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    public async Task DirectVisitSuccessResetsTheNativePendingMove(string build)
    {
        var binding = Binding(build, out var table);
        var session = new Session();
        await binding.SendAsync(session, new PartyVisitSucceeded("Bob"), default);
        var sent = Assert.Single(session.Sent);
        Assert.Equal(table.S2C("gspRepMoveToFriend"), sent.Opcode);
        Assert.Equal(Bytes(b => { Word(b, 0); Text(b, "Bob"); Word(b, 0); }), sent.Body);
        Assert.Equal(18, sent.Body.Length);
    }

    /// <summary>Invitation arrival completes the legacy native UI through its result and nickname reply.</summary>
    [Theory]
    [MemberData(nameof(PartyBuilds))]
    public async Task InvitationArrivalWritesTheNativeCompletionReply(string build)
    {
        var binding = Binding(build, out var table);
        var session = new Session();
        await binding.SendAsync(session, new PartyInvitationMoveSucceeded("Bob"), default);
        var sent = Assert.Single(session.Sent);
        Assert.Equal(table.S2C("gspRepMoveToInviter"), sent.Opcode);
        Assert.Equal(Bytes(b => { Word(b, 0); Text(b, "Bob"); }), sent.Body);
        Assert.Equal(14, sent.Body.Length);
    }

    /// <summary>The notices write the native shapes: result + nickname, two nicknames, and the handover reply.</summary>
    [Fact]
    public async Task NoticesWriteTheNativeShapes()
    {
        var binding = Binding("kr-812", out var table);
        var session = new Session();
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.PartyFull, "Bob"), default);
        await binding.SendAsync(session, new PartyLeaderReplaced("Al", "Bob"), default);
        await binding.SendAsync(session, new PartyLeaderChanged("Al", "Bob", PartyLeaderChangeResult.Changed), default);
        await binding.SendAsync(session, new PartyLeft(), default);
        await binding.SendAsync(session, new PartyInviteNotified(true, "Al"), default);
        Assert.Equal(table.S2C("gspRepInvite"), session.Sent[0].Opcode);
        Assert.Equal(Bytes(b => { Word(b, 7); Text(b, "Bob"); }), session.Sent[0].Body);
        Assert.Equal(table.S2C("gspUpdatePartyLeader"), session.Sent[1].Opcode);
        Assert.Equal(Bytes(b => { Text(b, "Al"); Text(b, "Bob"); }), session.Sent[1].Body);
        Assert.Equal(table.S2C("gspRepChangePartyLeader"), session.Sent[2].Opcode);
        Assert.Equal(Bytes(b => { Text(b, "Al"); Text(b, "Bob"); Word(b, 0); }), session.Sent[2].Body);
        Assert.Equal(table.S2C("gspNotifyPartyLeaveSuccess"), session.Sent[3].Opcode);
        Assert.Empty(session.Sent[3].Body);
        // The notice's NetAddress is left 0.0.0.0:0 and its +48 bool32 zero.
        Assert.Equal(table.S2C("gspNotifyInvite"), session.Sent[4].Opcode);
        Assert.Equal(Bytes(b => { Word(b, 1); Text(b, "Al"); Word(b, 0); b.AddRange(new byte[2]); Word(b, 0); }), session.Sent[4].Body);
    }

    /// <summary>The modern prompt writes the build's own shape: the token words or the tw-454 auto-accept word.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task ModernPromptWritesTheBuildsOwnShape(string build)
    {
        var binding = Binding(build, out var table);
        var session = new Session();
        await binding.SendAsync(session, new PartyInviteNotified(true, "Al", 0x01020304), default);
        var (opcode, body) = Assert.Single(session.Sent);
        if (build == "tw-454")
        {
            Assert.Equal(table.S2C("gspNotifyInvite"), opcode);
            Assert.Equal(Bytes(b => { Text(b, "Al"); Word(b, 0); b.AddRange(new byte[2]); Word(b, 0); }), body);
        }
        else
        {
            Assert.Equal(table.S2C("gspAskedInvitation"), opcode);
            Assert.Equal(Bytes(b => { Text(b, "Al"); Word(b, 0x01020304); Word(b, 0); b.AddRange(new byte[2]); Word(b, 0); Word(b, 0); }), body);
        }
    }

    /// <summary>The modern accept reads its own shape; the prompt's kind 1 and a granted visit's kind 0 join the party.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernAcceptCountsOnlyForAnInvitationPrompt(string build)
    {
        var binding = Binding(build, out _);
        var hasKind = build != "tw-454";
        byte[] Body(byte? kind) =>
            [.. T("Al"), .. W(0x7F000001), 0x39, 0x30, .. (kind is { } value ? new[] { value } : [])];
        Assert.Equal(new PartyInviteAccepted(true, "Al"), binding.ReadAccept(Body(hasKind ? (byte)1 : null)));
        if (hasKind)
        {
            Assert.Equal(new PartyInviteAccepted(true, "Al"), binding.ReadAccept(Body(0)));
            Assert.Throws<NotSupportedException>(() => binding.ReadAccept(Body(2)));
            Assert.Throws<PacketFormatException>(() => binding.ReadAccept(Body(null)));
        }
    }

    /// <summary>The modern refusal echoes the prompt's token (kr-1256, cn-774) or names the inviter (tw-454).</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernRefusalEchoesTheTokenOrNamesTheInviter(string build)
    {
        var binding = Binding(build, out _);
        if (build == "tw-454")
        {
            Assert.Equal(new PartyInviteAnswered("Al", 3), binding.ReadAnswer(T("Al")));
            Assert.Throws<PacketFormatException>(() => binding.ReadAnswer([.. T("Al"), 0]));
            return;
        }
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x01020304);
        Assert.Equal(new PartyInviteTokenRefused(0x01020304), binding.ReadTokenRefusal(body));
        Assert.Throws<PacketFormatException>(() => binding.ReadTokenRefusal(new byte[3]));
    }

    /// <summary>The inviter's answer uses the modern delivery/failure labels and the party/InviteFailed codes.</summary>
    [Fact]
    public async Task ModernResultWritesDeliveredAndFailed()
    {
        var binding = Binding("kr-1256", out var table);
        var session = new Session();
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.Sent, "Bo"), default);
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.Refused, "Bo"), default);
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.Offline, "Bo"), default);
        await binding.SendAsync(session, new PartyInviteReplied(PartyInviteResult.PartyFull, "Bo"), default);
        Assert.Equal(new[]
        {
            $"{table.S2C("gspInvitationDelivered")}:{Convert.ToHexString(T("Bo"))}",
            $"{table.S2C("gspInvitationFailed")}:{Convert.ToHexString([.. T("Bo"), .. W(26)])}",
            $"{table.S2C("gspInvitationFailed")}:{Convert.ToHexString([.. T("Bo"), .. W(24)])}",
            $"{table.S2C("gspInvitationFailed")}:{Convert.ToHexString([.. T("Bo"), .. W(14)])}",
        }, session.Sent.Select(frame => $"{frame.Opcode}:{Convert.ToHexString(frame.Body)}").ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await binding.SendAsync(session, new PartyInviteReplied((PartyInviteResult)99, "Bo"), default));
    }

    /// <summary>The party list carries the ordered members and, when supplied, the recipient's appearance.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public void ModernListCarriesOrderedMembersAndTheRecipientsAppearance(string build)
    {
        var body = PartyBinding.WriteParty(TestBuilds.Packets, build,
            new PartyUpdated(new([new(7, "Ab"), new(9, "C")]), new(9, 300017, [300017, 200100])));
        var read = PacketCodec.Read(TestBuilds.Packets, build, "SoloPartySnapshot", body);
        var members = Assert.IsAssignableFrom<IReadOnlyList<object?>>(read["members"]);
        Assert.Equal(new[] { 7, 9 }, members.Select(row => Convert.ToInt32(((IReadOnlyDictionary<string, object?>)row!)["user_number"])));
        var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<object?>>(read["profile_entries"])));
        Assert.Equal(9, Convert.ToInt32(profile["user_number"]));
        Assert.Equal(300017, Convert.ToInt32(profile["character_id"]));
    }

    /// <summary>
    /// The modern solo list with the recipient's appearance is pinned to the exact bytes the modern row's own
    /// party writer produced for the same values (state 2, one member with four zero words at +32..+44, then
    /// the profile record with the character and the non-character equipped ids): the same bytes the raw
    /// in-game path's S2C 201 blob carries. Readers: kr-1256 sub_9064B0 (handler) -> kr-1256 sub_905D20
    /// (body parser) -> kr-1256 sub_1141670 (member element), profile entry element kr-1256 sub_10C4290.
    /// </summary>
    [Fact]
    public void ModernSoloAppearancePinsTheBytesTheModernRowWriterSent()
    {
        int[] items = [300017, 200100, 207000];
        var body = PartyBinding.WriteParty(TestBuilds.Packets, "kr-1256",
            new PartyUpdated(new([new(11, "member")]), new(11, 300017, items)));
        Assert.Equal(84, body.Length);
        Assert.Equal(
            "02000000010000000B000000060000006D0065006D0062006500720000000000000000000000000000000000010000000B000000F193040002000000A40D03009828030000000000000000000000000000000000",
            Convert.ToHexString(body));
    }

    /// <summary>
    /// The modern group list without an appearance ends in the empty profile map: state 2, two members with
    /// their four trailing words, then the map count 0. Pinning the count word keeps the keyed map the only
    /// trailing element (kr-1256 sub_905D20 reads it after the member vector).
    /// </summary>
    [Fact]
    public void ModernGroupWithoutAppearanceEndsInTheEmptyProfileMap()
    {
        var body = PartyBinding.WriteParty(TestBuilds.Packets, "kr-1256",
            new PartyUpdated(new([new(22, "leader"), new(11, "member")]), null));
        Assert.Equal(84, body.Length);
        Assert.Equal(
            "020000000200000016000000060000006C0065006100640065007200000000000000000000000000000000000B000000060000006D0065006D006200650072000000000000000000000000000000000000000000",
            Convert.ToHexString(body));
    }

    /// <summary>
    /// The legacy group list keeps its own shape for the same values: three trailing member words (no word at
    /// +32, reader kr-812 sub_415C00) and the empty equipped vector, with no profile map at all.
    /// </summary>
    [Fact]
    public void LegacyGroupPinsTheKr812Shape()
    {
        var body = PartyBinding.WriteParty(TestBuilds.Packets, "kr-812",
            new PartyUpdated(new([new(22, "leader"), new(11, "member")]), null));
        Assert.Equal(76, body.Length);
        Assert.Equal(
            "020000000200000016000000060000006C00650061006400650072000000000000000000010000000B000000060000006D0065006D0062006500720000000000000000000100000000000000",
            Convert.ToHexString(body));
    }

    /// <summary>
    /// The modern row mounts the invitation and visit routes only; kick, leader and leave stay off until audited.
    /// cn-774 and tw-454 send the invitation as C2S 109 (cn-774 sub_B04050, tw-454 sub_B9FA50).
    /// </summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task ModernRowMountsTheInvitationRoutesOnly(string build, bool hasInvite)
    {
        var binding = Binding(build, out var table);
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var messages = new Messages();
        var context = new PluginContext(new PluginRow { Id = "party-binding", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            binding.Bind(context, messages);
            var expected = new List<ushort>
            {
                table.C2S("gcpReqMoveToInviter"), table.C2S("gcpReqMoveToFriendAccept"), table.C2S("gcpReqMoveToFriend"),
                table.C2S("gcpReqAcceptVisitation_"), table.C2S("gcpReqRefuseVisitation_"),
            };
            if (hasInvite) expected.Add(table.C2S("gcpReqInvite"));
            Assert.Equal(expected.Order(), routes.Handlers.Keys.Select(key => key.Item2).Order());
            Assert.Single(services.GetAll<IMessageSender<PartyInviteNotified>>());
            Assert.Single(services.GetAll<IMessageSender<PartyInviteReplied>>());
            Assert.Single(services.GetAll<IMessageSender<PartyUpdated>>());
            Assert.Empty(services.GetAll<IMessageSender<PartyKickReplied>>());
            Assert.Empty(services.GetAll<IMessageSender<PartyLeaderChanged>>());
            Assert.Equal(build == "tw-454" ? 1 : 0, services.GetAll<IInboundMessageBinding<PartyInviteAnswered>>().Count);
            Assert.Equal(build == "tw-454" ? 0 : 1, services.GetAll<IInboundMessageBinding<PartyInviteTokenRefused>>().Count);

            if (!hasInvite) return;
            var accept = routes.Handlers[(build + "/game", table.C2S("gcpReqMoveToInviter"))];
            await Assert.ThrowsAsync<PacketFormatException>(async () => await accept(new Session(), new InboundPacket(0, new byte[0]), default));
            Assert.Empty(messages.Received);
            var body = Bytes(b => { Text(b, "Al"); Word(b, 0x7F000001); b.Add(0x39); b.Add(0x30); if (build != "tw-454") b.Add(1); });
            await accept(new Session(), new InboundPacket(0, body), default);
            Assert.Equal(new PartyInviteAccepted(true, "Al"), Assert.Single(messages.Received));
            messages.Received.Clear();
            if (build == "tw-454") return; // tw-454's accept carries no kind byte.
            var wrongKind = Bytes(b => { Text(b, "Al"); Word(b, 0x7F000001); b.Add(0x39); b.Add(0x30); b.Add(2); });
            await accept(new Session(), new InboundPacket(0, wrongKind), default);
            Assert.Empty(messages.Received);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>
    /// The legacy answer packet carries both prompts: 3 refuses an invitation (kr-812 sub_81DEA0), 1 accepts a visit
    /// and 0 or 2 declines it (kr-812 sub_81ED40 case 17).
    /// </summary>
    [Fact]
    public void LegacyFriendAnswerSplitsInvitationAndVisit()
    {
        var binding = Binding("kr-812", out _);
        byte[] Answer(uint code) => Bytes(b => { Text(b, "Al"); Word(b, (int)code); });
        Assert.Equal(new PartyInviteAnswered("Al", 3), binding.ReadFriendAnswer(Answer(3)));
        Assert.Equal(new PartyVisitAnswered("Al", true), binding.ReadFriendAnswer(Answer(1)));
        Assert.Equal(new PartyVisitAnswered("Al", false), binding.ReadFriendAnswer(Answer(0)));
        Assert.Equal(new PartyVisitAnswered("Al", false), binding.ReadFriendAnswer(Answer(2)));
        Assert.Throws<NotSupportedException>(() => binding.ReadFriendAnswer(Answer(4)));
    }

    /// <summary>
    /// kr-812 visits through gspRepMoveToFriend (kr-812 sub_81ED40): result 17 prompts the visited player, the
    /// failures use the same packet, and the visitor is moved by a notice whose +48 skips the prompt (kr-812 sub_81DEA0).
    /// </summary>
    [Fact]
    public async Task LegacyVisitWritesTheMoveToFriendReplyAndASkippedNotice()
    {
        var binding = Binding("kr-812", out var table);
        Assert.Equal("legacy", binding.VisitFamily());
        Assert.Equal(new PartyVisitRequested("Bob"), binding.ReadVisit(Bytes(b => Text(b, "Bob"))));
        var session = new Session();
        await binding.SendAsync(session, new PartyVisitAsked("Al", 9), default);
        await binding.SendAsync(session, new PartyVisitFailed(PartyVisitFailure.Declined, "Bob"), default);
        await binding.SendAsync(session, new PartyVisitGranted("Bob"), default);
        Assert.Equal(table.S2C("gspRepMoveToFriend"), session.Sent[0].Opcode);
        Assert.Equal(Bytes(b => { Word(b, 17); Text(b, "Al"); Word(b, 0); }), session.Sent[0].Body);
        Assert.Equal(Bytes(b => { Word(b, 18); Text(b, "Bob"); Word(b, 0); }), session.Sent[1].Body);
        Assert.Equal(table.S2C("gspNotifyInvite"), session.Sent[2].Opcode);
        Assert.Equal(Bytes(b => { Word(b, 1); Text(b, "Bob"); Word(b, 0); b.AddRange(new byte[2]); Word(b, 1); }), session.Sent[2].Body);
    }

    /// <summary>The older direct Find request has no approval sender and preserves its native nickname and error layout.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    public async Task OlderFindUsesTheDirectFlowAndNativeReply(string build)
    {
        var binding = Binding(build, out var table);
        Assert.Equal("direct", binding.VisitFamily());
        Assert.Equal(110, table.C2S("gcpReqMoveToFriend"));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriendAccept"));
        var body = Bytes(bytes => Text(bytes, "Bob"));
        Assert.Equal(10, body.Length);
        Assert.Equal(new PartyVisitRequested("Bob", RequiresApproval: false), binding.ReadVisit(body));
        var session = new Session();
        await binding.SendAsync(session, new PartyVisitFailed(PartyVisitFailure.Offline, "Bob"), default);
        var reply = Assert.Single(session.Sent);
        Assert.Equal(114, reply.Opcode);
        Assert.Equal(Bytes(bytes => { Word(bytes, 3); Text(bytes, "Bob"); Word(bytes, 0); }), reply.Body);
        Assert.Equal(18, reply.Body.Length);
    }

    /// <summary>
    /// The modern family prompts with gspAskedVisitation (token, visitor, flag; kr-1256 sub_9AB8F0), answers by token
    /// and fails with gspVisitationFailed (nickname, party/VisitFailed code; kr-1256 sub_9AA990). kr-1256 and cn-774
    /// grant with gspVisitationAccepted; tw-454, which has none, with a notice that skips the prompt.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 112, 113, 114)]
    [InlineData("cn-774", 111, 112, 113)]
    [InlineData("tw-454", 112, 113, 114)]
    public async Task ModernVisitUsesTheTokenPromptAndTheFailureCodes(string build, int request, int refuse, int accept)
    {
        var binding = Binding(build, out var table);
        Assert.Equal("modern", binding.VisitFamily());
        Assert.Equal(request, table.C2S("gcpReqMoveToFriend"));
        Assert.Equal(refuse, table.C2S("gcpReqRefuseVisitation_"));
        Assert.Equal(accept, table.C2S("gcpReqAcceptVisitation_"));
        Assert.Equal(build == "kr-1256" ? 110 : 109, table.C2S("gcpReqInvite"));
        var session = new Session();
        await binding.SendAsync(session, new PartyVisitAsked("Al", 0x01020304), default);
        Assert.Equal(table.S2C("gspAskedVisitation"), session.Sent[0].Opcode);
        Assert.Equal(Bytes(b => { Word(b, 0x01020304); Text(b, "Al"); Word(b, 0); }), session.Sent[0].Body);
        await binding.SendAsync(session, new PartyVisitFailed(PartyVisitFailure.Declined, "Bob"), default);
        Assert.Equal(table.S2C("gspVisitationFailed"), session.Sent[1].Opcode);
        Assert.Equal(Bytes(b => { Text(b, "Bob"); Word(b, 28); }), session.Sent[1].Body);
        await binding.SendAsync(session, new PartyVisitGranted("Bob"), default);
        if (build == "tw-454")
        {
            Assert.Equal(table.S2C("gspNotifyInvite"), session.Sent[2].Opcode);
            Assert.Equal(Bytes(b => { Text(b, "Bob"); Word(b, 0); b.AddRange(new byte[2]); Word(b, 1); }), session.Sent[2].Body);
        }
        else
        {
            Assert.Equal(table.S2C("gspVisitationAccepted"), session.Sent[2].Opcode);
            Assert.Equal(Bytes(b => { Text(b, "Bob"); Word(b, 0); b.AddRange(new byte[2]); }), session.Sent[2].Body);
        }
    }

    /// <summary>A table that gives a consumed party field another type is refused when the row binds.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheRowBinds()
    {
        var directory = PacketTableEdits.WithFieldType("PartyNickname", "nickname", "wstr", "u32");
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", "kr-812", "opcodes.yaml"), profile);
        var binding = new PartyBinding("kr-812/game", table, PacketTables.Load(directory.FullName), "kr-812");
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "party-binding", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Contains("nickname", error.Message, StringComparison.Ordinal);
            Assert.Contains("PartyNickname", error.Message, StringComparison.Ordinal);
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IInboundMessageBinding<PartyInviteRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<PartyUpdated>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static byte[] Bytes(Action<List<byte>> write)
    {
        var bytes = new List<byte>();
        write(bytes);
        return [.. bytes];
    }

    private static PartyBinding Binding(string build, out OpcodeTable table)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        return new PartyBinding(build + "/game", table, TestBuilds.Packets, build);
    }

    private static void Word(List<byte> bytes, int value)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, value);
        bytes.AddRange(word);
    }

    private static void Text(List<byte> bytes, string text)
    {
        Word(bytes, text.Length);
        bytes.AddRange(Encoding.Unicode.GetBytes(text));
    }

    private static byte[] W(int value)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, value);
        return word;
    }

    private static byte[] T(string text) => [.. W(text.Length), .. Encoding.Unicode.GetBytes(text)];

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private sealed class Session : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => new("kr-812", "kr", 812, 0, 0, "kr-812");
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Received.Add(message);
            return ValueTask.FromResult(true);
        }
    }
}
