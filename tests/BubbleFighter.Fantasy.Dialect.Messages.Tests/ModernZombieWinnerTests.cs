using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the final masked scoreboard's winner byte for the zombie allegiances on every modern build.</summary>
public sealed class ModernZombieWinnerTests
{
    /// <summary>
    /// A zombie-mode winner reaches the lead unchanged with the banner gate set: kr-1256 sub_429620 keeps any
    /// winner up to 36 and cn-774 sub_A91FB0 compares it with the local team.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 14, "020E01")]
    [InlineData("kr-1256", 15, "020F01")]
    [InlineData("cn-774", 14, "020E01")]
    [InlineData("cn-774", 15, "020F01")]
    [InlineData("tw-454", 14, "020E01")]
    [InlineData("tw-454", 15, "020F01")]
    public async Task ZombieAllegianceWinsReachTheLead(string build, int winner, string lead)
    {
        var body = await SendFinal(build, winner);
        Assert.Equal(lead, Convert.ToHexString(body.AsSpan(0, 3)));
    }

    /// <summary>A winner that is neither a team, a zombie allegiance nor a draw still fails loudly.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task AnUnknownWinnerIsRefused(string build) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendFinal(build, 3));

    /// <summary>Team 34 reaches the ordinary, ghost and escape zombie banners while unrelated modes reject that allegiance.</summary>
    [Theory]
    [InlineData("kr-1256", 8)]
    [InlineData("kr-1256", 28)]
    [InlineData("kr-1256", 32)]
    [InlineData("cn-774", 8)]
    [InlineData("cn-774", 28)]
    [InlineData("cn-774", 32)]
    [InlineData("tw-454", 8)]
    [InlineData("tw-454", 32)]
    public async Task SmallZombieWinnerIsRestrictedToItsNativeMode(string build, int mode)
    {
        var body = await SendFinal(build, 34, mode);
        Assert.Equal("022201", Convert.ToHexString(body.AsSpan(0, 3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendFinal(build, 34, 1));
    }

    private static async Task<byte[]> SendFinal(string build, int winner, int mode = 1)
    {
        var table = TestBuilds.Opcodes(build);
        var session = new Session(table.Profile);
        var services = new ServiceRegistry();
        using var routes = services.Register<IPacketRouter>(new Routes());
        using var messages = services.Register<IMessageRouter>(new Messages());
        var context = new PluginContext(new PluginRow { Id = "zombie-winner", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            new RoundResultBinding(build + "/game", table, TestBuilds.Packets, build, [1, 8, 28, 32], NullLogger.Instance).Mount(context);
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundScoreboard>>());
            var row = new RoundScoreboardRow(0, "Zombie", winner, 12, new(3, 4, 5, 19, 2),
                new(13, 1000, 1234, 2000, 234, 567, true, []))
                { UserNumber = 101, Outcome = RoundOutcome.Won };
            await sender.SendAsync(session, new RoundScoreboard(7, winner, [0, 0], [row], WithRewards: true) { ModeId = mode }, default);
            return Assert.Single(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<byte[]> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add(payload.ToArray()); return ValueTask.CompletedTask; }
    }

    private sealed class Routes : IPacketRouter
    {
        public IDisposable Register(string protocol, ushort opcode, PacketHandler handler) => new Removal();
        public ValueTask DispatchAsync(string protocol, ISession session, InboundPacket packet, CancellationToken ct) =>
            throw new NotSupportedException();
        private sealed class Removal : IDisposable { public void Dispose() { } }
    }

    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage => ValueTask.FromResult(true);
    }
}
