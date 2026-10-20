using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Protocol;
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

    private static OpcodeEntry Entry(ushort value, string name) => new(value, PacketDirection.ServerToClient, name, Evidence.Verified, null, null)
        { Connection = OpcodeConnection.Relay };
    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
}
