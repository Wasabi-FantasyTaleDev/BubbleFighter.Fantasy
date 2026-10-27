using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks installed native success signals, connection scoping and publication cleanup.</summary>
public sealed class MatchRepliesMountTests
{
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task NativeRepliesUseReviewedRelayNamesAndEmptySignalBodies(string build, bool remap)
    {
        var profile = Profile(build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var table = remap ? new OpcodeTable(profile, full.All.Concat(full.ForConnection(OpcodeConnection.Relay).All)
            .Distinct().Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : full;
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.Null(table.Find(PacketDirection.ServerToClient, "rspRelayJoinAccepted_"));
        Assert.Null(table.Find(PacketDirection.ServerToClient, "rspRelaySceneAccepted_"));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/relay", "dialect.match-replies");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new MatchRepliesPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
            var join = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
            Assert.Equal(build + "/relay", join.Protocol);
            await join.SendAsync(session, new(), default);
            Assert.Equal((ushort)(remap ? 5001 : 1), Assert.Single(session.Sent));
            Assert.Empty(Assert.Single(session.Bodies));
            var scenes = fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>();
            Assert.Equal(build == "cn-279" ? 0 : 1, scenes.Count);
            if (build != "cn-279")
            {
                await scenes[0].SendAsync(session, new(0x11223344), default);
                Assert.Equal((ushort)(remap ? 5013 : 13), session.Sent[^1]);
                Assert.Empty(session.Bodies[^1]);
                Assert.Equal(2, session.Sent.Count);
            }
            var other = new MatchMessagesMountTests.Session(Profile(build == "kr-812" ? "cn-324" : "kr-812"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => join.SendAsync(other, new(), default).AsTask());
            if (scenes.Count != 0)
                await Assert.ThrowsAsync<InvalidOperationException>(() => scenes[0].SendAsync(other, new(1), default).AsTask());
            Assert.Empty(other.Sent);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join.SendAsync(session, new(), stop.Token).AsTask());
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => join.SendAsync(session, new(), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
    }

    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task ModernRepliesSendTheSettingsAndSeedFramesAroundTheSignals(string build)
    {
        // The kr-1256 accept pair is S2C 22 then S2C 1 (kr-1256 sub_9601E0 case 22 arms the handshake record
        // before case 1 starts the C2S 12 exchange), and a scene accept initializes the entity's seed tables
        // before the empty S2C 13. The exact bodies are pinned by RelayHandshakeTableParityTests.
        var profile = Profile(build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var relay = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(relay);
        var context = fixture.Context(build, build + "/relay", "dialect.match-replies");
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new MatchRepliesPlugin().ActivateAsync(context, default);
            var join = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
            await join.SendAsync(session, new(), default);
            Assert.Equal(new ushort[] { 22, 1 }, session.Sent);
            Assert.Equal(10, session.Bodies[0].Length);
            Assert.Equal(4, session.Bodies[1].Length);

            var scene = Assert.Single(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
            await scene.SendAsync(session, new(1000000), default);
            Assert.Equal(new ushort[] { 22, 1, 4, 5, 6, 13 }, session.Sent);
            Assert.Equal(new[] { 268, 1008, 136, 0 },
                session.Bodies.Skip(2).Select(body => body.Length).ToArray());
            foreach (var frame in session.Bodies.Skip(2).Take(3))
                Assert.Equal(1000000, BitConverter.ToInt32(frame));
            Assert.Equal("00000000000000000000", Convert.ToHexString(session.Bodies[0]));
            Assert.Equal("00000000", Convert.ToHexString(session.Bodies[1]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
    }

    [Theory]
    [InlineData("missing-build")]
    [InlineData("game-protocol")]
    [InlineData("other-protocol")]
    [InlineData("other-table")]
    [InlineData("missing-join")]
    [InlineData("missing-scene")]
    public async Task InvalidScopeOrMissingSignalFailsBeforePublishingSenders(string fault)
    {
        var build = fault == "other-table" ? "cn-324" : "kr-812";
        var entries = new List<OpcodeEntry>();
        if (fault != "missing-join") entries.Add(Entry(1, "rspRelayJoinAccepted_"));
        if (fault != "missing-scene") entries.Add(Entry(13, "rspRelaySceneAccepted_"));
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(Profile(build), entries));
        var protocol = fault switch { "game-protocol" => "kr-812/game", "other-protocol" => "cn-324/relay", _ => "kr-812/relay" };
        var context = fixture.Context(fault == "missing-build" ? null : "kr-812", protocol, "dialect.match-replies");
        try { await Assert.ThrowsAsync<ConfigException>(() => new MatchRepliesPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
    }

    /// <summary>The reply row validates its own seed layouts even without an inbound match row.</summary>
    [Theory]
    [InlineData("RelayCriticalSeedTable", "unknown_8", "list<u32>", "list<u16>")]
    [InlineData("RelayCriticalSeedTable", "unknown_24", "list<u32>", "list<u16>")]
    [InlineData("RelayDefenceSeedTable", "unknown_8", "list<u8>", "list<u16>")]
    [InlineData("RelayCriticalSeedCancelTable", "unknown_8", "list<u32>", "list<u16>")]
    [InlineData("RelayCriticalSeedTable", "entity_id", "u32", "u16")]
    public async Task InvalidSeedLayoutFailsBeforePublishingSenders(string structure, string field, string original, string replacement)
    {
        var directory = PacketTableEdits.WithFieldType(structure, field, original, replacement);
        const string build = "kr-1256";
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), Profile(build))
            .ForConnection(OpcodeConnection.Relay);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/relay", "dialect.match-replies", directory.FullName);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() =>
                new MatchRepliesPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains(structure, error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchSceneAccepted>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static OpcodeEntry Entry(ushort value, string name) => new(value, PacketDirection.ServerToClient, name, Evidence.Verified, null, null)
        { Connection = OpcodeConnection.Relay };
    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
}
