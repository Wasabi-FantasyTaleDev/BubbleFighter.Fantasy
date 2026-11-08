using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins shared native map-script bodies and their authenticated, disposable input surface.</summary>
public sealed class MapScriptTests
{
    /// <summary>The object name is counted bytes, including native non-ASCII bytes; health remains a float.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ObjectBodiesPreserveNativeBytesAndPinEveryWord(string build)
    {
        var visibility = Convert.FromHexString("030000006781FE01000000FFFFFFFF");
        Assert.Equal(15, visibility.Length);
        var shown = MapScriptObjectBinding.ReadVisibility(TestBuilds.Packets, build, visibility);
        Assert.Equal(new byte[] { 0x67, 0x81, 0xFE }, shown.Name.ToArray());
        Assert.True(shown.Visible);
        Assert.Equal(-1, shown.Sequence);
        Assert.Equal(visibility, MapScriptObjectBinding.WriteVisibility(TestBuilds.Packets, build, shown));

        var breakable = Convert.FromHexString("030000006781FE0000484302000000");
        Assert.Equal(15, breakable.Length);
        var created = MapScriptObjectBinding.ReadBreakable(TestBuilds.Packets, build, breakable);
        Assert.Equal(shown.Name.ToArray(), created.Name.ToArray());
        Assert.Equal(200f, created.HitPoints);
        Assert.Equal(2, created.Sequence);
        Assert.Equal(breakable, MapScriptObjectBinding.WriteBreakable(TestBuilds.Packets, build, created));

        foreach (var malformed in new[] { visibility[..^1], visibility.Concat(new byte[] { 0 }).ToArray(),
            BitConverter.GetBytes(1025), new byte[12], Convert.FromHexString("01000000000100000002000000") })
            Assert.Throws<PacketFormatException>(() => MapScriptObjectBinding.ReadVisibility(TestBuilds.Packets, build, malformed));
        foreach (var malformed in new[] { breakable[..^1], breakable.Concat(new byte[] { 0 }).ToArray(), BitConverter.GetBytes(1025) })
            Assert.Throws<PacketFormatException>(() => MapScriptObjectBinding.ReadBreakable(TestBuilds.Packets, build, malformed));
        foreach (var health in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            var malformed = breakable.ToArray();
            BitConverter.GetBytes(health).CopyTo(malformed, 7);
            Assert.Throws<PacketFormatException>(() => MapScriptObjectBinding.ReadBreakable(TestBuilds.Packets, build, malformed));
        }
    }

    /// <summary>Both input links resolve names independently; every published sender and input unloads with the row.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public async Task ObjectRoutesResolveNamesEchoAndUnload(string build, bool remap)
    {
        var native = TestBuilds.Opcodes(build);
        var game = native.ForConnection(OpcodeConnection.Game);
        var relay = native.ForConnection(OpcodeConnection.Relay);
        if (remap)
        {
            game = new OpcodeTable(native.Profile, game.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) }))
                .ForConnection(OpcodeConnection.Game);
            relay = new OpcodeTable(native.Profile, relay.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 2000)) }))
                .ForConnection(OpcodeConnection.Relay);
        }
        using var fixture = new MatchMessagesMountTests.Fixture(native);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(native.Profile);
        var binding = new MapScriptObjectBinding(build + "/game", build + "/relay", game, relay, TestBuilds.Packets, build);
        try
        {
            binding.Mount(context);
            var visibility = Convert.FromHexString("01000000470100000005000000");
            var breakable = Convert.FromHexString("01000000420000484306000000");
            await fixture.Packets.DispatchAsync(build + "/game", session,
                new(game.C2S("gppSetAddableObjectVisible_"), visibility), default);
            await fixture.Packets.DispatchAsync(build + "/relay", session,
                new(relay.C2S("gppCreateBreakableObject_"), breakable), default);
            var shown = Assert.IsType<RoundMapObjectVisibilityChanged>(fixture.Messages.Received[0]);
            var created = Assert.IsType<RoundBreakableMapObjectCreated>(fixture.Messages.Received[1]);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundMapObjectVisibilityChanged>>()).SendAsync(session, shown, default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBreakableMapObjectCreated>>()).SendAsync(session, created, default);
            Assert.Equal(new[] { game.S2C("gppSetAddableObjectVisible_"), game.S2C("gppCreateBreakableObject_") }, session.Sent);
            Assert.Equal(visibility, session.Bodies[0]);
            Assert.Equal(breakable, session.Bodies[1]);
            Assert.Equal(2, fixture.Services.GetAll<IInboundMessageBinding<RoundMapObjectVisibilityChanged>>().Count());
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Packets.DispatchAsync(build + "/game", session,
                new(game.C2S("gppSetAddableObjectVisible_"), visibility), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundMapObjectVisibilityChanged>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBreakableMapObjectCreated>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundMapObjectVisibilityChanged>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBreakableMapObjectCreated>>());
    }

    /// <summary>Default activation does not acquire the newly audited script routes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task MapScriptsRemainAbsentWithoutModeOptions(string build)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new() { ["modern_rooms"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundMapObjectVisibilityChanged>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBreakableMapObjectCreated>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundFinalZoneMapEffectChanged>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>FinalZone and Zombie Escape share one effect binding when both mode options are enabled.</summary>
    [Fact]
    public async Task BothModesMountExactlyOneEffectSenderAndTwoInputLinks()
    {
        const string build = "kr-1256";
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = true, ["final_zone"] = true, ["zombie_escape"] = true,
            ["result_scoreboard_modes"] = new[] { 1, 30, 32 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Single(fixture.Services.GetAll<IMessageSender<RoundFinalZoneMapEffectChanged>>());
            Assert.Equal(2, fixture.Services.GetAll<IInboundMessageBinding<RoundFinalZoneMapEffectChanged>>().Count());
            Assert.Single(fixture.Services.GetAll<IMessageSender<RoundMapObjectVisibilityChanged>>());
            Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBreakableMapObjectCreated>>());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }
}
