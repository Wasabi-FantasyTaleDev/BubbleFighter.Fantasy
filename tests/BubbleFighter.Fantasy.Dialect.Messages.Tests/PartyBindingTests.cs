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

    /// <summary>The party list is the state word, the counted members (user, nickname, three zero words) and an empty equipped list.</summary>
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
            Word(expected, 0);
        }
        Word(expected, 0);
        Assert.Equal(58, bytes.Length);
        Assert.Equal(expected.ToArray(), bytes);
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
            Assert.Equal(PartyBinding.Requests.Length + (answer is null ? 0 : 1), routes.Handlers.Count);
            foreach (var name in PartyBinding.Replies)
                Assert.NotNull(table.Find(PacketDirection.ServerToClient, name));
            Assert.Single(services.GetAll<IMessageSender<PartyUpdated>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IMessageSender<PartyUpdated>>());
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

    /// <summary>The modern accept reads its own shape; only the kind that answers a prompt is a party accept.</summary>
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
            Assert.Throws<NotSupportedException>(() => binding.ReadAccept(Body(0)));
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

    /// <summary>The modern row mounts the invitation routes only; kick, leader and leave stay off until audited.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
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
            var expected = new List<ushort> { table.C2S("gcpReqMoveToInviter"), table.C2S("gcpReqMoveToFriendAccept") };
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
            var body = Bytes(b => { Text(b, "Al"); Word(b, 0x7F000001); b.Add(0x39); b.Add(0x30); b.Add(1); });
            await accept(new Session(), new InboundPacket(0, body), default);
            Assert.Equal(new PartyInviteAccepted(true, "Al"), Assert.Single(messages.Received));
            messages.Received.Clear();
            var wrongKind = Bytes(b => { Text(b, "Al"); Word(b, 0x7F000001); b.Add(0x39); b.Add(0x30); b.Add(2); });
            await accept(new Session(), new InboundPacket(0, wrongKind), default);
            Assert.Empty(messages.Received);
        }
        finally { await context.UnwindAsync(); }
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
