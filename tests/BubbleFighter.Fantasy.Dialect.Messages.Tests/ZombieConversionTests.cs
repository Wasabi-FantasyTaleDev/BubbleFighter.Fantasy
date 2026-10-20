using BubbleFighter.Fantasy.Contracts.Rooms;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native seat and allegiance words used by cooperative conversion.</summary>
public sealed class ZombieConversionTests
{
    /// <summary>Writes the eight-byte player seat followed by zombie allegiance 15.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void ConversionIsASeatAndAnAllegianceWord(string build)
    {
        var body = ZombieConversionCodec.Encode(TestBuilds.Packets, build, new(1, 15));
        Assert.Equal(8, body.Length);
        Assert.Equal("010000000F000000", Convert.ToHexString(body));
    }

    /// <summary>Mounts native team and revival services without requiring rescue or counted-item features.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task ConversionAndZombieRevivalUseInstalledBuildRoutesAndUnload(string build, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = new PluginContext(new PluginRow
        {
            Id = "room", Plugin = "dialect.rooms", Source = "conversion-mount-test",
            ClientBuild = new(build[..2], int.Parse(build[3..]), 0),
            Config = new Dictionary<string, object?>
            {
                ["protocol"] = protocol, ["packets_directory"] = TestBuilds.PacketsDirectory(),
                ["zombie_conversion"] = true, ["instant_revival"] = true,
            },
        }, fixture.Services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpRoomChangeTeam"), ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<RoomTeamChangeRequested>(Assert.Single(fixture.Messages.Received));
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpPveUseRevival"), ReadOnlyMemory<byte>.Empty), default);
            Assert.IsType<RoundPveRevivalRequested>(fixture.Messages.Received[^1]);
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpPveUseRevival"), new byte[4]), default).AsTask());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoomTeamConverted>>());
            foreach (var team in new[] { 2, 15, 15 })
                await sender.SendAsync(session, new(1, team), default);
            Assert.Equal(3, session.Sent.Count);
            Assert.All(session.Sent, opcode => Assert.Equal(table.S2C("gspRoomChangeTeam"), opcode));
            Assert.Equal("0100000002000000", Convert.ToHexString(session.Bodies[0]));
            Assert.Equal("010000000F000000", Convert.ToHexString(session.Bodies[1]));
            Assert.Equal(session.Bodies[1], session.Bodies[2]);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCountedItemsPrepared>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPvePointsChanged>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomTeamConverted>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPveRevivalRequested>>());
    }
}
