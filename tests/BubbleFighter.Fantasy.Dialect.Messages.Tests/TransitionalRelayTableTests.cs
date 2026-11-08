using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the account-bearing join and legacy scene tree used by the intermediate Chinese clients.</summary>
public sealed class TransitionalRelayTableTests
{
    private const string Key = "4433221188776655CCBBAA99EEDD5713E0AC6824";
    private const string Join = Key + "6A4030201088776655010000004100";
    private const string Equipment = "01000000020000000300000002000000040000000500000002000000AABB"
        + "0100000006000000070000000200000008000000090000000A000000010000004100"
        + "010000000B000000000000000000F83F";
    private const string Presets = "01000000010000000102030000000400000002000000AABB";
    private const string Report = "4433221101000000410088776655" + Equipment + Presets
        + "0100000042000304000500060007000802000000090A01000000";

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void SignatureRetainsWireWidthsAfterNativeOffsetChanges(string build)
    {
        Assert.Equal(Convert.FromHexString("07000000000000"), LoginBurstWire.WriteSessionSignature(TestBuilds.Packets, build, 7));
        Assert.Equal(new byte[7], LoginBurstWire.WriteSessionSignatureEmpty(TestBuilds.Packets, build));
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void JoinReadsTheFullTailBeforeProjectingItsOpaqueKey(string build)
    {
        var body = Convert.FromHexString(Join);
        Assert.Equal(35, body.Length);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "RelayJoinRequest", body);
        Assert.Equal(0x55667788, values["unknown_36"]);
        Assert.Equal("A", values["unknown_40"]);
        Assert.Equal(Convert.FromHexString(Key), RelayConnectionWire.ReadJoinTicket(TestBuilds.Packets, build, body));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RelayJoinRequest", values));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayConnectionWire.ReadJoinTicket(TestBuilds.Packets, build, body[..length]));
        Assert.Throws<PacketFormatException>(() => RelayConnectionWire.ReadJoinTicket(TestBuilds.Packets, build, [.. body, 0]));
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void CompleteSceneKeepsTenBytePresetEntriesAndTheBoolTail(string build)
    {
        var body = Convert.FromHexString(Report);
        Assert.Equal(144, body.Length);
        var values = RelayValidationWire.ReadReport(TestBuilds.Packets, build, body);
        Assert.Equal(0x11223344, values["entity_id"]);
        Assert.Equal("A", values["nickname"]);
        Assert.Equal("B", values["unknown_160"]);
        Assert.Equal((byte)3, values["unknown_192"]);
        Assert.Equal((ushort)4, values["unknown_196"]);
        Assert.Equal((ushort)5, values["unknown_198"]);
        Assert.Equal((ushort)6, values["unknown_200"]);
        Assert.Equal(true, values["unknown_236"]);
        var presets = Assert.IsType<Dictionary<string, object?>>(values["presets"]);
        var entries = Assert.IsType<object?[]>(presets["entries"]);
        var entry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(entries));
        Assert.Equal((byte)1, entry["unknown_4"]);
        Assert.Equal((byte)2, entry["unknown_5"]);
        Assert.Equal(3, entry["unknown_8"]);
        Assert.Equal(4, entry["unknown_12"]);
        Assert.Equal(new object?[] { (byte)0xAA, (byte)0xBB }, presets["unknown_24"]);
        var signature = Assert.IsType<Dictionary<string, object?>>(values["signature"]);
        Assert.Equal((ushort)7, signature["unknown_4"]);
        Assert.Equal((byte)8, signature["unknown_12"]);
        Assert.Equal(new object?[] { (byte)9, (byte)10 }, signature["unknown_16"]);
        Assert.Equal(new NativeRelaySceneIdentity(0x11223344, "A"),
            RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, body));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RelayValidationReport", values));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, body[..length]));
        Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, [.. body, 0]));
        Assert.Throws<PacketFormatException>(() => RelayValidationWire.ReadIdentity(TestBuilds.Packets, build, body,
            new PacketReadLimits(MaxCollectionElements: 1)));
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task MountedHybridUsesLegacySceneAdmissionAndSettingsWithoutAReconnectMap(string build)
    {
        var profile = new ClientProfile(build, "cn", int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.Null(table.Find(PacketDirection.ClientToServer, "rcpReqJoinRelaySession"));
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "rcpReqRelayReconnectMap_"));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var requests = fixture.Context(build, build + "/relay");
        var replies = fixture.Context(build, build + "/relay", "dialect.match-replies");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new MatchMessagesPlugin().ActivateAsync(requests, default);
            await new MatchRepliesPlugin().ActivateAsync(replies, default);
            Assert.Equal(2, fixture.Packets.Handlers.Count);
            await fixture.Packets.DispatchAsync(build + "/relay", session,
                new(relay.C2S("rcpReqJoinRelaySession"), Convert.FromHexString(Join)), default);
            Assert.Equal(Convert.FromHexString(Key),
                Assert.IsType<MatchCapabilityAuthenticate>(Assert.Single(fixture.Messages.Received)).Ticket.ToArray());
            await fixture.Packets.DispatchAsync(build + "/relay", session,
                new(relay.C2S("rcpReqReportHackCheckData"), Convert.FromHexString(Report)), default);
            Assert.Equal(new MatchSceneIdentityRegistered(0x11223344, "A"), fixture.Messages.Received[^1]);
            var join = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
            await join.SendAsync(session, new(), default);
            var scene = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
            await scene.SendAsync(session, new(1000000), default);
            Assert.Equal(new ushort[] { 22, 1, 4, 5, 6, 13 }, session.Sent);
            Assert.Equal(new[] { 10, 0, 268, 1008, 136, 0 }, session.Bodies.Select(body => body.Length));
            Assert.Equal("00000000000000000000", Convert.ToHexString(session.Bodies[0]));
            foreach (var body in session.Bodies.Skip(2).Take(3)) Assert.Equal(1000000, BitConverter.ToInt32(body));
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchMapNamed>>());
        }
        finally
        {
            await replies.UnwindAsync();
            await requests.UnwindAsync();
        }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
    }
}
