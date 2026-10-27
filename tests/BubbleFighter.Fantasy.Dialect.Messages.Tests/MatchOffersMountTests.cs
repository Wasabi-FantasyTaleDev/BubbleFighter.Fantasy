using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins installed native game-side offers and reports, including the additional ID2014 flag.</summary>
public sealed class MatchOffersMountTests
{
    [Theory]
    [InlineData("kr-812", 20, false)]
    [InlineData("cn-717", 20, false)]
    [InlineData("cn-324", 18, false)]
    [InlineData("cn-279", 12, false)]
    [InlineData("cn-406", 20, false)]
    [InlineData("id-400", 20, false)]
    [InlineData("id-1809", 20, false)]
    [InlineData("kr-1256", 20, false, 571)]
    [InlineData("cn-774", 20, false, 571)]
    [InlineData("tw-454", 20, false, 571)]
    [InlineData("kr-812", 20, true)]
    public async Task OwnTablesDriveOfferBytesReportLengthsAndRemovableGameServices(string build, int keyLength, bool remap, int nativeOpcode = 601)
    {
        var profile = Profile(build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.match-offers");
        var session = new MatchMessagesMountTests.Session(profile);
        var opcode = checked((ushort)(nativeOpcode + (remap ? 1000 : 0)));
        try
        {
            await new MatchOffersPlugin().ActivateAsync(context, default);
            var sender = Assert.Single(fixture.Services.GetAll<IMatchCapabilityOfferSender>());
            Assert.Equal(build + "/game", sender.Protocol);
            Assert.Equal(keyLength, sender.TicketLength);
            Assert.Equal(build + "/game", Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchConnectionReported>>()).Protocol);
            var key = Enumerable.Range(1, keyLength).Select(value => (byte)value).ToArray();
            await sender.SendAsync(session, new(IPAddress.Parse("1.2.3.4"), 7121, key), default);
            Assert.Equal(opcode, Assert.Single(session.Sent));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 0xD1, 0x1B }.Concat(key), Assert.Single(session.Bodies));
            Assert.Equal(6 + keyLength, session.Bodies[0].Length);
            var body = Convert.FromHexString(build == "id-400" ? "0100000000000000" : "01000000");
            await fixture.Packets.DispatchAsync(build + "/game", session, new(opcode, body), default);
            Assert.Equal(new MatchConnectionReported(true), Assert.Single(fixture.Messages.Received));
            if (build == "id-400")
            {
                await fixture.Packets.DispatchAsync(build + "/game", session, new(opcode, Convert.FromHexString("0000000001000000")), default);
                Assert.Equal(new MatchConnectionReported(false), fixture.Messages.Received[^1]);
            }
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(build + "/game", session,
                new(opcode, body[..^1]), default).AsTask());
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(build + "/game", session,
                new(opcode, new byte[body.Length + 1]), default).AsTask());
            var other = new MatchMessagesMountTests.Session(Profile("kr-999"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(other, new(IPAddress.Loopback, 7121, key), default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Packets.DispatchAsync(build + "/game", other, new(opcode, body), default).AsTask());
            await Assert.ThrowsAsync<PacketFormatException>(() => sender.SendAsync(session, new(IPAddress.Loopback, 7121, key[..^1]), default).AsTask());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(session, new(IPAddress.Loopback, 7121, key), canceled.Token).AsTask());
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Packets.DispatchAsync(build + "/game", session, new(opcode, body), default).AsTask());
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(session, new(IPAddress.Loopback, 7121, key), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMatchCapabilityOfferSender>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchConnectionReported>>());
    }

    [Theory]
    [InlineData("missing-build")]
    [InlineData("relay-protocol")]
    [InlineData("other-protocol")]
    [InlineData("other-table")]
    [InlineData("missing-offer")]
    [InlineData("missing-report")]
    public async Task InvalidNativeOfferScopeFailsBeforePublishingEffects(string fault)
    {
        var build = fault == "other-table" ? "cn-324" : "kr-812";
        var entries = new List<OpcodeEntry>();
        if (fault != "missing-offer") entries.Add(new(601, PacketDirection.ServerToClient, "gspConnectToRelayServer", Evidence.Verified, null, null));
        if (fault != "missing-report") entries.Add(new(601, PacketDirection.ClientToServer, "gcpReportRelayConnected", Evidence.Verified, null, null));
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(Profile(build), entries));
        var protocol = fault switch { "relay-protocol" => "kr-812/relay", "other-protocol" => "cn-324/game", _ => "kr-812/game" };
        var context = fixture.Context(fault == "missing-build" ? null : "kr-812", protocol, "dialect.match-offers");
        try { await Assert.ThrowsAsync<ConfigException>(() => new MatchOffersPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMatchCapabilityOfferSender>());
    }

    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
}
