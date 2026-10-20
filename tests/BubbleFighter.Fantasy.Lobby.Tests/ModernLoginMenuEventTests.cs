using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Pins the shared login adapter's <see cref="LoginMenuInstalled"/> boundary on the kr-1256 family's burst:
/// the event fires once, after the last frame, while the session is still authenticated, and a refused or
/// faulted login never reaches it.
/// </summary>
public sealed class ModernLoginMenuEventTests
{
    /// <summary>The launcher request reader delivers the identity without echoing request contents.</summary>
    [Fact]
    public async Task MalformedLauncherLoginDoesNotEchoItsContents()
    {
        var identity = "bff-ticket:" + new string('a', 64);
        await using var rig = await Rig.StartAsync();
        var payload = WireLengthSupport.Written(writer =>
        {
            writer.WriteWideStringPrefixed(identity);
            writer.WriteWideStringPrefixed("private-login-field");
            writer.WriteWideStringPrefixed("private-login-field");
            writer.WriteUInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteWideStringPrefixed("private-login-field");
            writer.WriteInt32(123);
        });
        var error = Assert.Throws<PacketFormatException>(() => LoginMessageCodec.ReadRequest(payload, rig.Values.Shape));
        Assert.DoesNotContain(identity, error.Message);
        Assert.DoesNotContain("private-login-field", error.Message);
        Assert.DoesNotContain(Convert.ToHexString(payload), error.Message);
    }

    /// <summary>The menu boundary fires once, after the burst's last frame, while the session is authenticated.</summary>
    [Fact]
    public async Task SuccessfulLoginPublishesOnceAfterTheLastFrameWhileStillAuthenticated()
    {
        await using var rig = await Rig.StartAsync();
        var login = Login();
        var session = new Session(rig.Profile);
        var seen = new List<LoginMenuInstalled>();
        using var subscription = rig.Events.OnAsync<LoginMenuInstalled>((installed, _) =>
        {
            Assert.Same(session, installed.Session);
            Assert.Same(login, installed.Login);
            Assert.Equal("kr-1256/game", installed.Protocol);
            Assert.Equal(rig.Opcodes.S2C("gspNotifyFirstMoveChannel"), session.Sent[^1]);
            seen.Add(installed);
            return session.SendAsync(41000, ReadOnlyMemory<byte>.Empty, default);
        });

        await rig.Binding.SendAsync(session, login, default);

        Assert.Single(seen);
        Assert.Equal((ushort)41000, session.Sent[^1]);
        Assert.Equal(1, session.Sent.Count(opcode => opcode == rig.Opcodes.S2C("gspNotifyFirstMoveChannel")));
    }

    /// <summary>A rejected login answers the refusal only and raises no menu boundary.</summary>
    [Fact]
    public async Task RejectedLoginDoesNotPublishAMenuBoundary()
    {
        await using var rig = await Rig.StartAsync();
        var raised = 0;
        using var subscription = rig.Events.OnAsync<LoginMenuInstalled>((_, _) =>
        {
            raised++;
            return ValueTask.CompletedTask;
        });
        await rig.Binding.SendAsync(new Session(rig.Profile), new LoginRejected(LoginRejection.AccountNotFound), default);
        Assert.Equal(0, raised);
    }

    /// <summary>A faulted frame stops the burst and still raises no menu boundary.</summary>
    [Fact]
    public async Task AFaultedLoginSendDoesNotPublishAMenuBoundary()
    {
        await using var rig = await Rig.StartAsync();
        var session = new Session(rig.Profile) { FailOpcode = rig.Opcodes.S2C("gspGotoRoom") };
        var raised = 0;
        using var subscription = rig.Events.OnAsync<LoginMenuInstalled>((_, _) =>
        {
            raised++;
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => rig.Binding.SendAsync(session, Login(), default).AsTask());
        Assert.Equal(0, raised);
        Assert.DoesNotContain(rig.Opcodes.S2C("gspNotifyFirstMoveChannel"), session.Sent);
    }

    /// <summary>The last boundary awaits every restoration and propagates its failure.</summary>
    [Fact]
    public async Task LoginAwaitsRestorationAndPropagatesItsFailure()
    {
        await using var rig = await Rig.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = rig.Events.OnAsync<LoginMenuInstalled>(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            throw new IOException("Restoration failed.");
        });
        var sending = rig.Binding.SendAsync(new Session(rig.Profile), Login(), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(sending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.ThrowsAsync<IOException>(() => sending);
    }

    private static LoginSucceeded Login() => new(42, "account", "player", 17,
        new DateTime(2026, 10, 16), 300001, [300001], null, [], []);

    private sealed class Rig : IAsyncDisposable
    {
        internal ClientProfile Profile { get; private init; } = null!;
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal LoginBinding Binding { get; private set; } = null!;
        internal LoginValues Values { get; private set; } = null!;
        internal IOpcodeTable Opcodes { get; private set; } = null!;
        private readonly ServiceRegistry _services = new();
        private IDisposable _table = null!;

        internal static async Task<Rig> StartAsync()
        {
            const string build = "kr-1256";
            var rig = new Rig { Profile = new ClientProfile(build, "kr", 1256, 0, 0, build) };
            var context = new PluginContext(
                new PluginRow { Id = "core.opcode-tables", Plugin = "core.opcode-tables", Source = "login-test" },
                rig._services, rig.Events, NullLogger.Instance);
            await new OpcodeTableRegistryPlugin().ActivateAsync(context, default);
            var registry = rig._services.Get<IOpcodeTableRegistry>();
            rig._table = registry.Register(OpcodeTableLoader.Load(
                Path.Combine(Root(), "profiles", build, "opcodes.yaml"), rig.Profile));
            var opcodes = registry.ForProtocol(build + "/game");
            var tables = PacketTables.Load(Path.Combine(Root(), "packets"));
            var values = LoginValues.From(build, tables.ValuesOf(build));
            rig.Opcodes = opcodes;
            rig.Values = values;
            rig.Binding = new LoginBinding(build + "/game", values, opcodes.S2C("gspRepLogin"), opcodes, tables,
                build, rig._services, rig.Events, new InventoryBinding(build + "/game", opcodes, tables, build),
                null, NullLogger.Instance);
            return rig;
        }

        public ValueTask DisposeAsync()
        {
            _table.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session : ISessionWithFeatures
    {
        internal Session(ClientProfile profile)
        {
            Profile = profile;
            this.SetLobbyPlayer(42, pastLoginBurst: false);
        }

        public long Id => 1;
        public IConnection Connection { get; } = new Connection();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile { get; }
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<ushort> Sent { get; } = [];
        internal ushort? FailOpcode { get; init; }

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailOpcode == opcode) throw new IOException("Login send failed.");
            Sent.Add(opcode);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }
}
