using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native move replies and the distinct promotion acknowledgement sequences.</summary>
public sealed class MigrationBindingTests
{
    /// <summary>Pins destination address bytes, little-endian numeric port and the modern unknown tail.</summary>
    [Theory]
    [InlineData("kr-812", 812, 10, 9)]
    [InlineData("kr-1256", 1256, 14, 8)]
    [InlineData("cn-279", 279, 10, 8)]
    [InlineData("cn-324", 324, 10, 8)]
    [InlineData("cn-406", 406, 10, 8)]
    [InlineData("cn-404", 404, 10, 9)]
    [InlineData("cn-408", 408, 10, 9)]
    [InlineData("cn-705", 705, 10, 9)]
    [InlineData("cn-706", 706, 10, 9)]
    [InlineData("cn-717", 717, 10, 9)]
    [InlineData("id-400", 400, 10, 9)]
    [InlineData("id-1809", 1809, 10, 9)]
    [InlineData("cn-774", 774, 14, 10)]
    [InlineData("tw-454", 454, 14, 10)]
    public async Task OfferUsesTheNativePendingConnectionLayout(string build, int version, int length, ushort opcode)
    {
        var (context, _, _, _, session, binding) = Fixture(build, version);
        try
        {
            await binding.SendAsync(session, new ChannelMoveOffered(System.Net.IPAddress.Parse("192.0.2.7"), 1304), default);
            Assert.Equal(opcode, Assert.Single(session.Sent));
            var expected = new byte[length];
            new byte[] { 0, 0, 0, 0, 192, 0, 2, 7, 24, 5 }.CopyTo(expected, 0);
            Assert.Equal(expected, Assert.Single(session.Bodies));
            await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session,
                new ChannelMoveOffered(System.Net.IPAddress.IPv6Loopback, 1304), default).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => binding.SendAsync(session,
                new ChannelMoveOffered(System.Net.IPAddress.Loopback, 0), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(context.Services.GetAll<IMessageSender<ChannelMoveOffered>>());
    }

    /// <summary>Checks the complete frame bodies and that unloading removes all routes and services.</summary>
    [Theory]
    [InlineData("kr-812", 812, 10, 11, 10)]
    [InlineData("kr-1256", 1256, 9, 11, 10)]
    [InlineData("cn-279", 279, 9, 10, 0)]
    [InlineData("cn-324", 324, 9, 10, 0)]
    [InlineData("cn-406", 406, 9, 10, 0)]
    [InlineData("cn-404", 404, 10, 11, 0)]
    [InlineData("cn-408", 408, 10, 11, 0)]
    [InlineData("cn-705", 705, 10, 11, 0)]
    [InlineData("cn-706", 706, 10, 11, 0)]
    [InlineData("cn-717", 717, 10, 11, 0)]
    [InlineData("id-400", 400, 10, 11, 0)]
    [InlineData("id-1809", 1809, 10, 11, 0)]
    [InlineData("cn-774", 774, 11, 13, 12)]
    [InlineData("tw-454", 454, 11, 13, 12)]
    public async Task MoveReplyAndPromotionUseEachBuildsNativePacketSequence(string build, int version, ushort promotion, ushort reply, ushort completion)
    {
        var (context, packets, messages, table, session, binding) = Fixture(build, version);
        try
        {
            await binding.SendAsync(session, new MoveIntoReplied(true, 60), default);
            await binding.SendAsync(session, new PendingConnectionPromoted(), default);
            Assert.Equal(new ushort[] { reply, promotion }, session.Sent);
            Assert.Equal(new byte[] { 1, 0, 0, 0, 60, 0, 0, 0, 0, 0, 0, 0 }, session.Bodies[0]);
            Assert.Empty(session.Bodies[1]);
            await binding.SendAsync(session, new MoveIntoReplied(false, 0), default);
            Assert.Equal(new byte[12], session.Bodies[2]);
            if (table.Find(PacketDirection.ClientToServer, "gcpAcknowledgePendingPromotion_") is { } ack)
            {
                Assert.Equal((ushort)55, ack);
                await packets.DispatchAsync(build + "/game", session, new(ack, ReadOnlyMemory<byte>.Empty), default);
                Assert.IsType<PendingPromotionAcknowledged>(Assert.Single(messages.Received));
                await binding.SendAsync(session, new PendingPromotionCompleted(), default);
                Assert.Equal(completion, session.Sent[^1]);
                Assert.Empty(session.Bodies[^1]);
                await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync(build + "/game", session,
                    new(ack, new byte[1]), default).AsTask());
                Assert.Single(messages.Received);
                messages.HasHandler = false;
                await Assert.ThrowsAsync<InvalidOperationException>(() => packets.DispatchAsync(build + "/game", session,
                    new(ack, ReadOnlyMemory<byte>.Empty), default).AsTask());
            }
            else
            {
                Assert.Empty(packets.Handlers);
                await Assert.ThrowsAsync<InvalidOperationException>(() => binding.SendAsync(session, new PendingPromotionCompleted(), default).AsTask());
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Handlers);
        Assert.Empty(context.Services.GetAll<IMessageSender<MoveIntoReplied>>());
        Assert.Empty(context.Services.GetAll<IMessageSender<PendingConnectionPromoted>>());
        Assert.Empty(context.Services.GetAll<IMessageSender<PendingPromotionCompleted>>());
        Assert.Empty(context.Services.GetAll<IInboundMessageBinding<PendingPromotionAcknowledged>>());
    }

    private static (PluginContext, MatchMessagesMountTests.Packets, MatchMessagesMountTests.Messages,
        IOpcodeTable, MatchMessagesMountTests.Session, MigrationBinding) Fixture(string build, int version)
    {
        var profile = new ClientProfile(build, "kr", version, 0, 1304, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var packets = new MatchMessagesMountTests.Packets();
        var messages = new MatchMessagesMountTests.Messages();
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "migration", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.Register<IPacketRouter>(packets));
        var binding = new MigrationBinding(build + "/game", table, TestBuilds.All.Tables, build);
        binding.Bind(context, messages);
        return (context, packets, messages, table, new(profile), binding);
    }
}
