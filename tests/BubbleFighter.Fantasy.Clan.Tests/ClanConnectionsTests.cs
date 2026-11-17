using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The node's clan connections are shared by every build's clan row: a notice from one build reaches members on another
/// build through that build's own delivery, a build without the message is skipped, and a retiring row takes its
/// connections with it.
/// </summary>
public sealed class ClanConnectionsTests
{
    [Fact]
    public async Task ABroadcastReachesMembersOnEveryBuildThroughTheirOwnDelivery()
    {
        var table = new ClanConnections();
        var kr1256 = new RecordingDelivery("kr-1256/clan", carries: true);
        var kr812 = new RecordingDelivery("kr-812/clan", carries: true);
        var hub = new ClanHub(table, kr1256);
        var local = new RecordingSender<ClanChatReceived>("kr-1256/clan");
        var a = new FakeSession(1);
        var b = new FakeSession(2);
        table.Attach(10, a, kr1256);
        table.Attach(20, b, kr812);

        await hub.BroadcastAsync([Member(10), Member(20), Member(30)], local, new ClanChatReceived("Ten", "hi"), seatedOnly: true, default);

        // The member on this build is written with this row's sender, the other build's member through its delivery,
        // and the member with no live connection here is skipped.
        Assert.Equal([a.Id], local.Sessions);
        Assert.Equal([b.Id], kr812.Sessions);
        Assert.Empty(kr1256.Sessions);
    }

    [Fact]
    public async Task AMemberWhoseBuildLacksTheMessageIsNotToldAndAWhisperReportsIt()
    {
        var table = new ClanConnections();
        var kr1256 = new RecordingDelivery("kr-1256/clan", carries: true);
        var kr812 = new RecordingDelivery("kr-812/clan", carries: false);
        table.Attach(20, new FakeSession(2), kr812);
        var hub = new ClanHub(table, kr1256);

        Assert.False(await hub.SendToAsync(20, new RecordingSender<ClanChatReceived>("kr-1256/clan"), new ClanChatReceived("Ten", "hi"), default));
        Assert.False(await hub.SendToAsync(99, new RecordingSender<ClanChatReceived>("kr-1256/clan"), new ClanChatReceived("Ten", "hi"), default));
    }

    [Fact]
    public void TheMasterTransferOnlySeesConnectionsOfItsOwnBuildAndARetiringRowReleasesItsConnections()
    {
        var table = new ClanConnections();
        var kr1256 = new RecordingDelivery("kr-1256/clan", carries: true);
        var kr812 = new RecordingDelivery("kr-812/clan", carries: true);
        table.Attach(10, new FakeSession(1), kr1256);
        table.Attach(20, new FakeSession(2), kr812);
        var hub = new ClanHub(table, kr1256);

        Assert.True(hub.TryGetLocal(10, out _));
        Assert.False(hub.TryGetLocal(20, out _));

        table.Release(kr812);
        Assert.False(table.TryGet(20, out _, out _));
        Assert.True(table.TryGet(10, out _, out _));

        table.Detach(1);
        Assert.False(table.TryGet(10, out _, out _));
    }

    [Fact]
    public async Task MembersNotConnectedHereAreHandedToTheBoundRelayInOneNotice()
    {
        var table = new ClanConnections();
        var kr1256 = new RecordingDelivery("kr-1256/clan", carries: true);
        var hub = new ClanHub(table, kr1256);
        table.Attach(10, new FakeSession(1), kr1256);
        var relay = new RecordingRelay();
        await hub.BroadcastAsync([Member(10), Member(20), Member(30)], new RecordingSender<ClanChatReceived>("kr-1256/clan"),
            new ClanChatReceived("Ten", "hi"), seatedOnly: true, default);
        Assert.Empty(relay.Notices);

        using (table.Bind(relay))
        {
            Assert.Throws<InvalidOperationException>(() => table.Bind(new RecordingRelay()));
            await hub.BroadcastAsync([Member(10), Member(20), Member(30), Member(40) with { Role = ClanRole.Applicant }],
                new RecordingSender<ClanChatReceived>("kr-1256/clan"), new ClanChatReceived("Ten", "hi"), seatedOnly: true, default);
            var notice = Assert.Single(relay.Notices);
            Assert.Equal(nameof(ClanChatReceived), notice.Kind);
            Assert.Equal([20L, 30L], notice.Recipients);
            // A whisper to a member elsewhere is relayed and counts as sent; the master transfer is not a relayed kind.
            Assert.True(await hub.SendToAsync(99, new RecordingSender<ClanWhisperReceived>("kr-1256/clan"), new ClanWhisperReceived(1, "Ten", "Ninety", "psst", 0), default));
            Assert.False(table.Forward([99], new ClanTransferOffered("Ten", 0)));
        }
        Assert.False(table.Forward([99], new ClanChatReceived("Ten", "after")));
    }

    [Fact]
    public async Task ARelayedNoticeIsToldToItsRecipientsHereInTheirOwnBuildAndBadNoticesAreSkipped()
    {
        var sender = new ClanConnections();
        var relay = new RecordingRelay();
        using var binding = sender.Bind(relay);
        IMessage[] messages =
        [
            new ClanChatReceived("Ten", "hi"), new ClanWhisperReceived(1, "Ten", "Twenty", "psst", 0),
            new ClanMemberChanged("Clan", 20, "Twenty", ClanRole.Member, 1), new ClanMemberLeft("Clan", 20, "Twenty", 0, 1),
            new ClanIntroductionChanged(0, "intro"), new ClanNoticeChanged("notice"), new ClanMarkChanged(3),
            new ClanMasterChanged(0, 10, "Ten", 20, "Twenty"), new ClanDisbandChanged(0, 1),
            new ClanHomeSent(false, 1000, ClanRole.Member, "Clan", new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)),
        ];
        foreach (var message in messages) Forward(sender, message);
        Assert.Equal(messages.Length, relay.Notices.Count);

        var receiver = new ClanConnections();
        var kr812 = new CapturingDelivery();
        receiver.Attach(20, new FakeSession(2), kr812);
        var broken = new ClanRelayNotice(nameof(ClanChatReceived), [20], "{\"unexpected\":1}");
        var unknown = new ClanRelayNotice("ClanTransferOffered", [20], "{}");
        await receiver.DeliverAsync([.. relay.Notices.Take(1), broken, unknown, .. relay.Notices.Skip(1)], default);
        Assert.Equal(messages, kr812.Messages);
    }

    private static void Forward<T>(ClanConnections table, T message) where T : IMessage => Assert.True(table.Forward([20], message));

    private sealed class RecordingRelay : IClanRelayLink
    {
        public List<ClanRelayNotice> Notices { get; } = [];

        public void Publish(ClanRelayNotice notice) => Notices.Add(notice);
    }

    private sealed class CapturingDelivery : IClanDelivery
    {
        public List<IMessage> Messages { get; } = [];

        public string Protocol => "kr-812/clan";

        public ValueTask<bool> SendAsync<T>(ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Messages.Add(message);
            return ValueTask.FromResult(true);
        }
    }

    private static ClanMemberRecord Member(long user) => new(1000, user, "M" + user, 1, ClanRole.Member, DateTime.UnixEpoch, DateTime.UnixEpoch);

    private sealed class RecordingDelivery(string protocol, bool carries) : IClanDelivery
    {
        public List<long> Sessions { get; } = [];

        public string Protocol { get; } = protocol;

        public ValueTask<bool> SendAsync<T>(ISession session, T message, CancellationToken ct) where T : IMessage
        {
            if (carries)
                Sessions.Add(session.Id);
            return ValueTask.FromResult(carries);
        }
    }

    private sealed class RecordingSender<T>(string protocol) : IMessageSender<T> where T : IMessage
    {
        public List<long> Sessions { get; } = [];

        public string Protocol { get; } = protocol;

        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            Sessions.Add(session.Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession(long id) : ISession
    {
        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection(id);
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
