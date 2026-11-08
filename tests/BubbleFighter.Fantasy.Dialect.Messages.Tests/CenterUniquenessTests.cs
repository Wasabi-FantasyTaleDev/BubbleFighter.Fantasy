using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Centers;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises auxiliary cleanup replies on builds with complete native center routes.</summary>
public sealed class CenterUniquenessTests
{
    /// <summary>The server sends the native success word and leaves auxiliary retirement to the client.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    public async Task AuxiliaryUniquenessPublishesSuccessWithoutClosingAuxiliaryConnection(string build)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id, IReadOnlyDictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow
            {
                Id = id, Plugin = id, Source = "center-test", Lineage = [build], Config = config,
            }, services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        var profile = new ClientProfile(build, build.Split('-')[0], int.Parse(build.Split('-')[1],
            System.Globalization.CultureInfo.InvariantCulture), 0, 0, build);
        var protocol = build + "/game";
        var auxiliary = new Session(profile, 2);
        try
        {
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            await new PacketRouterPlugin().ActivateAsync(Context("packets"), default);
            var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile)
                .ForConnection(OpcodeConnection.Game);
            var dialect = Context("dialect");
            var binding = new CenterDirectoryBinding(protocol, table, TestBuilds.All.Tables, build);
            binding.Bind(dialect, services.Get<IMessageRouter>());
            var domain = Context("centers", new Dictionary<string, object?>
            {
                ["protocol"] = protocol,
                ["entries"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Primary", ["listener_id"] = "game" },
                },
            });
            await new CentersPlugin().ActivateAsync(domain, default);
            await events.SerialAsync(new ConnectionAcceptedEvent(auxiliary.Connection, "game"), default);
            var router = services.Get<IPacketRouter>();
            await router.DispatchAsync(protocol, auxiliary, new(table.C2S("gcpReqCenterUserCnt"), ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(table.S2C("gspRepCenterUserCnt"), Assert.Single(auxiliary.Sent).Opcode);
            auxiliary.Sent.Clear();
            await router.DispatchAsync(protocol, auxiliary, new(table.C2S("gcpReqUniquenessCheck"), new byte[] { 42, 0, 0, 0 }), default);
            var reply = Assert.Single(auxiliary.Sent);
            Assert.Equal(table.S2C("gspRepUniquenessCheck"), reply.Opcode);
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, reply.Payload);
            Assert.False(auxiliary.Connection.IsClosed);
            await domain.UnwindAsync();
            Assert.False(await services.Get<IMessageRouter>().DispatchAsync(protocol, auxiliary,
                new BubbleFighter.Fantasy.Contracts.Lobby.CenterAvailabilityRequested(42), default));
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository was not found.");
    }

    private sealed class Session(ClientProfile profile, long id) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
