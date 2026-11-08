using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class RoundPingTests
{
    [Fact]
    public void NativeProbeIsExactlyTwoWordsAndPreservesAllBits()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "RoundPing", new Dictionary<string, object?>
        {
            ["unknown_4"] = unchecked((int)0xFEDCBA98), ["unknown_8"] = unchecked((int)0x89ABCDEF),
        });
        Assert.Equal(8, body.Length);
        Assert.Equal("98BADCFEEFCDAB89", Convert.ToHexString(body));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, "kr-812", "RoundPing", new Dictionary<string, object?>
        {
            ["unknown_4"] = unchecked((int)0xFEDCBA98), ["unknown_8"] = unchecked((int)0x89ABCDEF),
        }));
    }

    [Theory]
    [InlineData("game", false)]
    [InlineData("relay", false)]
    [InlineData("game", true)]
    [InlineData("relay", true)]
    public async Task MountedRoomAnswersOnlyTheOriginatingConnectionAndUnloads(string connection, bool remap)
    {
        const string build = "kr-1256";
        var native = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry.Name == "gppPing"
            ? entry with { Value = checked((ushort)(entry.Value + (entry.Direction == PacketDirection.ClientToServer ? 111 : 222))) }
            : entry)) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new() { ["modern_rooms"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var sender = new MatchMessagesMountTests.Session(table.Profile);
        var body = Convert.FromHexString("98BADCFEEFCDAB89");
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            await fixture.Packets.DispatchAsync(build + "/" + connection, sender, new(table.C2S("gppPing"), body), default);
            Assert.Equal(table.S2C("gppPing"), Assert.Single(sender.Sent));
            Assert.Equal(body, Assert.Single(sender.Bodies));
            Assert.Empty(fixture.Messages.Received);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    public async Task MalformedProbeIsNotEchoedOrForwarded(int length)
    {
        var table = TestBuilds.Opcodes("kr-1256");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            Mount(context, table);
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gppPing"), new byte[length]), default).AsTask());
            Assert.Empty(session.Sent);
            Assert.Empty(fixture.Messages.Received);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task CancellationAndTransportFailureRemainVisible()
    {
        var table = TestBuilds.Opcodes("kr-1256");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            Mount(context, table);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Packets.DispatchAsync("kr-1256/relay", session,
                new(table.C2S("gppPing"), new byte[8]), new CancellationToken(true)).AsTask());
            Assert.Empty(session.Sent);
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.Packets.DispatchAsync("kr-1256/relay", session,
                new(table.C2S("gppPing"), new byte[8]), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task MissingReplyFailsBeforeRegisteringEitherConnection()
    {
        var native = TestBuilds.Opcodes("kr-1256");
        var table = new OpcodeTable(native.Profile, native.All.Where(entry => entry.Name != "gppPing"
            || entry.Direction != PacketDirection.ServerToClient));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game");
        try
        {
            Assert.Throws<ConfigException>(() => Mount(context, table));
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task OtherBuildsDoNotAcquireAnUnauditedEcho(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game");
        try
        {
            RoundPingBinding.Mount(context, build + "/game", build + "/relay", table.ForConnection(OpcodeConnection.Game),
                table.ForConnection(OpcodeConnection.Relay), TestBuilds.Packets, build);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    private static void Mount(IPluginContext context, IOpcodeTable table) =>
        RoundPingBinding.Mount(context, "kr-1256/game", "kr-1256/relay", table.ForConnection(OpcodeConnection.Game),
            table.ForConnection(OpcodeConnection.Relay), TestBuilds.Packets, "kr-1256");
}
