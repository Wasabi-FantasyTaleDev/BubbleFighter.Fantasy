using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins center metadata differences and the native four-byte availability result.</summary>
public sealed class CenterDirectoryWireTests
{
    [Theory]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void NamedCentersIncludeTheNameBetweenIdentityAndOccupancy(string build)
    {
        var bytes = CenterDirectoryBinding.WriteStatus(TestBuilds.All.Tables, build, new(2, "Test", 3, 512));
        Assert.Equal(24, bytes.Length);
        Assert.Equal("020000000400000054006500730074000300000000020000", Convert.ToHexString(bytes));
    }
    [Fact]
    public void Cn2011HasThreeWordsAndCannotReceiveANameString()
    {
        var bytes = CenterDirectoryBinding.WriteStatus(TestBuilds.All.Tables, "cn-279", new(2, "Test", 3, 512));
        Assert.Equal(12, bytes.Length);
        Assert.Equal("020000000300000000020000", Convert.ToHexString(bytes));
    }
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void AvailabilityUsesTheNativeBool32(string build)
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, CenterDirectoryBinding.WriteAvailability(TestBuilds.All.Tables, build, new(false)));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, CenterDirectoryBinding.WriteAvailability(TestBuilds.All.Tables, build, new(true)));
    }

    /// <summary>The shared reader preserves user-number bits and rejects partial or extended requests.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void AvailabilityRequestRequiresExactlyOneUserNumber(string build)
    {
        var tables = TestBuilds.All.Tables;
        Assert.Equal(-1, CenterDirectoryBinding.ReadAvailabilityRequest(tables, build, [255, 255, 255, 255]).UserNum);
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() =>
            CenterDirectoryBinding.ReadAvailabilityRequest(tables, build, [1, 0, 0]));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() =>
            CenterDirectoryBinding.ReadAvailabilityRequest(tables, build, [1, 0, 0, 0, 0]));
    }

    /// <summary>A table that retypes a consumed center field is refused when the row binds.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheRowBinds()
    {
        var directory = PacketTableEdits.WithFieldType("CenterAvailabilityRequest", "user_num", "u32", "wstr");
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", "kr-812", "opcodes.yaml"), profile);
        var binding = new CenterDirectoryBinding("kr-812/game", table, PacketTables.Load(directory.FullName), "kr-812");
        var services = new ServiceRegistry();
        var routes = new Routes();
        using var router = services.Register<IPacketRouter>(routes);
        var context = new PluginContext(new PluginRow { Id = "center-directory", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var error = Assert.Throws<ConfigException>(() => binding.Bind(context, new Messages()));
            Assert.Contains("user_num", error.Message, StringComparison.Ordinal);
            Assert.Empty(routes.Handlers);
            Assert.Empty(services.GetAll<IInboundMessageBinding<CenterStatusRequested>>());
            Assert.Empty(services.GetAll<IMessageSender<CenterStatusReplied>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            throw new NotSupportedException();
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage =>
            ValueTask.FromResult(true);
    }
}
