using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the legacy gift box packets (packets/present.yaml): the byte length of every structure per build family, the
/// request parsing, the opcodes each build mounts, and the capability that keeps the other families off.
/// </summary>
public sealed class PresentWireTests
{
    // The builds that send the task-bar count words, and the ones that do not (packets/present.yaml).
    private static readonly string[] BadgeBuilds = ["kr-812", "cn-717"];
    private static readonly string[] PlainBuilds = ["id-400", "id-1809", "cn-324", "cn-279", "cn-706", "cn-406", "cn-404", "cn-408"];

    public static TheoryData<string> AllBuilds
    {
        get
        {
            var builds = new TheoryData<string>();
            foreach (var build in BadgeBuilds.Concat(PlainBuilds))
                builds.Add(build);
            return builds;
        }
    }

    private static byte[] Bytes(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case int word: bytes.AddRange(BitConverter.GetBytes(word)); break;
                case uint word: bytes.AddRange(BitConverter.GetBytes(word)); break;
                case ushort half: bytes.AddRange(BitConverter.GetBytes(half)); break;
                case double real: bytes.AddRange(BitConverter.GetBytes(real)); break;
                case string text: bytes.AddRange(BitConverter.GetBytes(text.Length)); bytes.AddRange(Encoding.Unicode.GetBytes(text)); break;
                default: throw new ArgumentException($"Unsupported part {part}.");
            }
        }
        return bytes.ToArray();
    }

    private static readonly DateTime Sent = new(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc);

    private static PresentRecord Record(PresentState state = PresentState.Pending) => new(7, "x", 3, "ab", 4, "cd", 1000123,
        "hi", new DateTimeOffset(Sent).ToUnixTimeMilliseconds(), state);

    private static double Date() => new DateTimeOffset(Sent).ToLocalTime().DateTime.ToOADate();

    [Theory, MemberData(nameof(AllBuilds))]
    public void EveryBuildOfTheBundleHasTheCapability(string build) =>
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "present_box"));

    [Theory]
    [InlineData("cn-735")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TheTabledModernBuildsDoNotHaveTheCapability(string build) =>
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "present_box"));

    [Theory, MemberData(nameof(AllBuilds))]
    public void TheNewGiftWordIsTwoWordsWithTheCountAndOneWithout(string build)
    {
        var badge = BadgeBuilds.Contains(build);
        var body = PresentBinding.WriteNotice(TestBuilds.Packets, build, new(true, 3));
        Assert.Equal(badge ? 8 : 4, body.Length);
        Assert.Equal(badge ? Bytes(1, 3) : Bytes(1), body);
        Assert.Equal(badge ? Bytes(0, 0) : Bytes(0), PresentBinding.WriteNotice(TestBuilds.Packets, build, new(false, 0)));
    }

    [Theory, MemberData(nameof(AllBuilds))]
    public void ThePageOfReceivedGiftsIsTheCountedListOfSevenFieldRecords(string build)
    {
        var badge = BadgeBuilds.Contains(build);
        var empty = PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Received, 0, 0, []));
        Assert.Equal(badge ? 16 : 12, empty.Length);

        var body = PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Received, 5, 2, [Record(PresentState.Accepted)]));
        // status, total, [count], list count, then id, date, kind, sender, key, opened, message: 38 bytes for "ab" and "hi".
        var expected = Bytes(1, 5).Concat(badge ? Bytes(2) : Array.Empty<byte>()).Concat(Bytes(1)).Concat(
            Bytes(7, Date(), 1, "ab", 1000123, (ushort)1, "hi")).ToArray();
        Assert.Equal(expected, body);
        Assert.Equal((badge ? 16 : 12) + 38, body.Length);
    }

    [Theory, MemberData(nameof(AllBuilds))]
    public void ThePageOfSentGiftsIsTheCountedListOfTenFieldRecords(string build)
    {
        Assert.Equal(12, PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Sent, 0, 0, [])).Length);

        var body = PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Sent, 1, 9, [Record()]));
        // id, date, extra date, kind, receiver, key, opened, thanks state, message, thank-you message: 52 bytes.
        var expected = Bytes(1, 1, 1).Concat(
            Bytes(7, Date(), 0.0, 1, "cd", 1000123, (ushort)0, (ushort)0, "hi", "")).ToArray();
        Assert.Equal(expected, body);
        Assert.Equal(12 + 52, body.Length);
    }

    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TheModernRecordsAreTheLegacyOrderWithObjectOffsetsOfTheirOwnAndAnEightByteReceivedTail(string build)
    {
        // kr-1256 sub_114DBA0 reads id, date, status, sender, key, half-word, message and eight more bytes;
        // kr-1256 sub_114DEF0 reads the ten sent-record fields in the legacy order.
        var received = PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Received, 5, 2, [Record(PresentState.Accepted)]));
        Assert.Equal(16 + 4 + 8 + 4 + (4 + 4) + 4 + 2 + (4 + 4) + 8, received.Length);
        var sent = PresentBinding.WriteList(TestBuilds.Packets, build, new(PresentBox.Sent, 1, 9, [Record()]));
        Assert.Equal(12 + 52, sent.Length);
        Assert.Equal(Bytes(1, 1, 1).Concat(Bytes(7, Date(), 0.0, 1, "cd", 1000123, (ushort)0, (ushort)0, "hi", "")), sent);
    }

    [Theory, MemberData(nameof(AllBuilds))]
    public async Task EveryReplyHasTheLengthItsClientReaderConsumes(string build)
    {
        var badge = BadgeBuilds.Contains(build);
        var binding = Mounted(build, out _, out var session, out _);
        await binding.SendAsync(session, new PresentSendReplied(PresentSendStatus.Sent), default);
        await binding.SendAsync(session, new PresentSendReplied(PresentSendStatus.InsufficientFunds), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.Receive, true, 7, 1000123, 2), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.Receive, false, 7, 0, 2), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.Throw, true, 7, 0, 2), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.DeleteReceived, true, 7, 0, 2), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.DeleteSent, true, 7, 0, 2), default);
        var sent = session.Sent;
        Assert.Equal(Bytes(1), sent[0].Body);
        Assert.Equal(Bytes(-3), sent[1].Body);
        Assert.Equal(badge ? Bytes(1, 7, 1000123, 2) : Bytes(1, 7, 1000123), sent[2].Body);
        Assert.Equal(badge ? Bytes(-1, 7, 0, 2) : Bytes(-1, 7, 0), sent[3].Body);
        Assert.Equal(Bytes(1, 7), sent[4].Body);
        Assert.Equal(badge ? Bytes(1, 7, 2) : Bytes(1, 7), sent[5].Body);
        Assert.Equal(Bytes(1, 7), sent[6].Body);
    }

    [Fact]
    public void EverySendStatusHasTheResultWordTheClientSwitchReads()
    {
        // kr-812 sub_829EC0 and cn-324's equivalent switch name 1, -1, -2, -3, -15, -17, -18, -21 and -51.
        var expected = new Dictionary<PresentSendStatus, int>
        {
            [PresentSendStatus.Sent] = 1, [PresentSendStatus.ReceiverMissing] = -1, [PresentSendStatus.SenderInvalid] = -2,
            [PresentSendStatus.InsufficientFunds] = -3, [PresentSendStatus.NotGiftable] = -15,
            [PresentSendStatus.ReceiverOwnsItem] = -17, [PresentSendStatus.SelfGift] = -18,
            [PresentSendStatus.MessageRejected] = -21, [PresentSendStatus.CurrencyRefused] = -51,
            [PresentSendStatus.ReceiverBoxFull] = -22, [PresentSendStatus.SenderBoxFull] = -23,
        };
        Assert.Equal(Enum.GetValues<PresentSendStatus>().Length, expected.Count);
        foreach (var (status, word) in expected)
            Assert.Equal(word, PresentBinding.SendResult(status));
    }

    [Theory, MemberData(nameof(AllBuilds))]
    public void TheRequestsParseAndRejectAnyOtherLength(string build)
    {
        var send = Bytes("target", "", 1000123, "hello");
        Assert.Equal(new PresentSendRequested("target", 1000123, "hello"), PresentBinding.ReadSend(TestBuilds.Packets, build, send));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadSend(TestBuilds.Packets, build, send[..^1]));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadSend(TestBuilds.Packets, build, send.Append((byte)0).ToArray()));

        Assert.Equal(new PresentListRequested(PresentBox.Sent, 8, 4),
            PresentBinding.ReadList(TestBuilds.Packets, build, PresentBox.Sent, Bytes(8, 4)));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadList(TestBuilds.Packets, build, PresentBox.Sent, Bytes(8)));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadList(TestBuilds.Packets, build, PresentBox.Sent, Bytes(-1, 4)));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadList(TestBuilds.Packets, build, PresentBox.Sent, Bytes(0, 0)));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadList(TestBuilds.Packets, build, PresentBox.Sent, Bytes(0, PresentLimits.MaxPageCount + 1)));

        Assert.Equal(new PresentActionRequested(PresentAction.Throw, 4_000_000_000),
            PresentBinding.ReadAction(TestBuilds.Packets, build, PresentAction.Throw, Bytes(4_000_000_000u)));
        Assert.Throws<PacketFormatException>(() => PresentBinding.ReadAction(TestBuilds.Packets, build, PresentAction.Throw, []));
    }

    [Fact]
    public void AGiftThatDoesNotFitThirtyTwoBitsIsNeverWrittenAsAnotherGift()
    {
        var oversized = Record() with { Id = PresentLimits.MaxId + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PresentBinding.WriteList(TestBuilds.Packets, "kr-812", new(PresentBox.Received, 1, 1, [oversized])));
    }

    [Theory, MemberData(nameof(AllBuilds))]
    public async Task EachBuildMountsItsOwnOpcodesAndRoutesEachRequestToItsMessage(string build)
    {
        var shift = build is "cn-324" or "cn-279" ? 1 : 0;
        var binding = Mounted(build, out var routes, out var session, out var messages);
        var protocol = build + "/game";
        Assert.Equal(new ushort[] { 810, 813, 814, 816, 817, 818, 822, 823 }.Select(n => (ushort)(n + shift)).Order(),
            routes.Handlers.Keys.Select(key => key.Item2).Order());

        async Task Send(int number, byte[] body) =>
            await routes.Handlers[(protocol, (ushort)(number + shift))](session, new((ushort)(number + shift), body), default);

        await Send(810, []);
        await Send(813, Bytes(0, 4));
        await Send(814, Bytes(4, 4));
        await Send(816, Bytes("target", "", 1000123, "hello"));
        await Send(817, Bytes(9));
        await Send(818, Bytes(9));
        await Send(822, Bytes(9));
        await Send(823, Bytes(9));
        Assert.Equal<IMessage>(
        [
            new PresentNoticeRequested(), new PresentListRequested(PresentBox.Received, 0, 4),
            new PresentListRequested(PresentBox.Sent, 4, 4), new PresentSendRequested("target", 1000123, "hello"),
            new PresentActionRequested(PresentAction.Receive, 9), new PresentActionRequested(PresentAction.Throw, 9),
            new PresentActionRequested(PresentAction.DeleteSent, 9), new PresentActionRequested(PresentAction.DeleteReceived, 9),
        ], messages.Received);
        await Assert.ThrowsAsync<PacketFormatException>(() => Send(810, [0]));
        messages.Handled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(817, Bytes(9)));

        await binding.SendAsync(session, new PresentNoticeSent(true, 1), default);
        await binding.SendAsync(session, new PresentSendReplied(PresentSendStatus.Sent), default);
        await binding.SendAsync(session, new PresentListReplied(PresentBox.Received, 0, 0, []), default);
        await binding.SendAsync(session, new PresentListReplied(PresentBox.Sent, 0, 0, []), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.Receive, true, 1, 1, 0), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.Throw, true, 1, 0, 0), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.DeleteSent, true, 1, 0, 0), default);
        await binding.SendAsync(session, new PresentActionReplied(PresentAction.DeleteReceived, true, 1, 0, 0), default);
        Assert.Equal(new ushort[] { 815, 821, 818, 819, 822, 823, 827, 828 }.Select(n => (ushort)(n + shift)),
            session.Sent.Select(frame => frame.Opcode));
    }

    [Fact]
    public async Task ABuildWhoseTableLacksARowFailsBeforeAnyRouteOrServiceIsPublished()
    {
        var root = RepositoryRoot();
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var loaded = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var table = new OpcodeTable(profile, loaded.All.Where(entry => entry.Name != "gspRepPresentSend"));
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "present", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var binding = new PresentBinding("kr-812/game", table, TestBuilds.Packets, "kr-812");
            Assert.ThrowsAny<Exception>(() => binding.Bind(context, new Messages()));
            Assert.Empty(routes.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task UnloadingTheRowRemovesEveryRouteAndService()
    {
        var root = RepositoryRoot();
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "present", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        new PresentBinding("kr-812/game", table, TestBuilds.Packets, "kr-812").Bind(context, new Messages());
        Assert.Equal(8, routes.Handlers.Count);
        Assert.Single(services.GetAll<IInboundMessageBinding<PresentSendRequested>>());
        Assert.Single(services.GetAll<IMessageSender<PresentNoticeSent>>());
        await context.UnwindAsync();
        Assert.Empty(routes.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<PresentSendRequested>>());
        Assert.Empty(services.GetAll<IMessageSender<PresentNoticeSent>>());
    }

    private static PresentBinding Mounted(string build, out Routes routes, out Session session, out Messages messages)
    {
        var root = RepositoryRoot();
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        routes = new Routes();
        messages = new Messages();
        services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "present", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var binding = new PresentBinding(build + "/game", table, TestBuilds.Packets, build);
        binding.Bind(context, messages);
        session = new Session(profile);
        return binding;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    internal sealed class Session(ClientProfile profile) : ISession
    {
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public long Id => 1;
        public ClientProfile Profile => profile;
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    internal sealed class Routes : IPacketRouter
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

    internal sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        internal bool Handled { get; set; } = true;
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(message); return ValueTask.FromResult(Handled); }
    }
}
