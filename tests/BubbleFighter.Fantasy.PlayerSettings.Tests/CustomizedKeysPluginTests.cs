using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Dialect.CustomizedKeys;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.PlayerSettings;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.PlayerSettings.Tests;

/// <summary>Exercises the mounted typed logic and table binding through real routers and login events.</summary>
public sealed class CustomizedKeysPluginTests
{
    [Theory]
    [InlineData("kr-812", 2013, 975)]
    [InlineData("kr-1256", 2019, 974)]
    [InlineData("cn-774", 2017, 974)]
    [InlineData("tw-454", 2016, 973)]
    public async Task UpdatesHaveNoImmediateReplyAndLoginRestoresTheSameKeyBits(string build, int c2s, int s2c)
    {
        await using var rig = await Rig.StartAsync(build, (ushort)c2s, (ushort)s2c);
        var original = rig.Session(1);
        CustomizedKey[] mappings = [new("Fire", int.MinValue), new("Jump", -1)];
        await rig.UpdateAsync(original, false, mappings);
        Assert.Empty(original.Sent);

        var reconnected = rig.Session(1);
        Assert.Equal(SessionPhase.Authenticated, reconnected.Feature<IPlayerSession>().Phase);
        await rig.LoginAsync(reconnected);
        var frame = Assert.Single(reconnected.Sent);
        Assert.Equal((ushort)s2c, frame.Opcode);
        var expected = Body(true, mappings);
        Assert.Equal(8 + 8 * 2 + 2 * (4 + 4), frame.Payload.Length);
        Assert.Equal(expected, frame.Payload);
        Assert.Equal(mappings, await rig.Store.ReadKeysAsync(1, rig.Build, default));
    }

    [Fact]
    public async Task ReplacementAndResetAreIsolatedFromOtherAccountsAndScopes()
    {
        await using var rig = await Rig.StartAsync();
        var first = rig.Session(1);
        var second = rig.Session(2);
        await rig.Store.ReplaceKeysAsync(1, "another-layout", [new("Fire", 99)], default);
        await rig.UpdateAsync(first, false, [new("Fire", 1), new("Jump", 2)]);
        await rig.UpdateAsync(second, false, [new("Fire", 3)]);
        await rig.UpdateAsync(first, false, [new("Jump", 4)]);
        Assert.Equal(new[] { new CustomizedKey("Jump", 4) }, await rig.Store.ReadKeysAsync(1, rig.Build, default));
        await rig.UpdateAsync(first, true, []);
        await rig.LoginAsync(first);
        Assert.Equal(Body(false, []), Assert.Single(first.Sent).Payload);
        Assert.Equal(new[] { new CustomizedKey("Fire", 3) }, await rig.Store.ReadKeysAsync(2, rig.Build, default));
        Assert.Equal(new[] { new CustomizedKey("Fire", 99) }, await rig.Store.ReadKeysAsync(1, "another-layout", default));
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("count")]
    [InlineData("string-length")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("flag-list")]
    [InlineData("duplicate")]
    [InlineData("nul")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    public async Task MalformedBodiesNeverReplaceAnExistingMap(string fault)
    {
        await using var rig = await Rig.StartAsync();
        var session = rig.Session(1);
        CustomizedKey[] saved = [new("Fire", 7)];
        await rig.UpdateAsync(session, false, saved);
        var body = fault switch
        {
            "boolean" => new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 },
            "count" => new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 },
            "string-length" => new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 255, 255, 255, 255 },
            "truncated" => Body(false, saved)[..^1],
            "trailing" => Body(false, saved).Concat(new byte[] { 0 }).ToArray(),
            "flag-list" => Body(true, saved),
            "duplicate" => Body(false, [new("Fire", 1), new("Fire", 2)]),
            "nul" => Body(false, [new("Fi\0re", 1)]),
            "high-surrogate" => InvalidUtf16Name(0xD800),
            "low-surrogate" => InvalidUtf16Name(0xDC00),
            _ => throw new InvalidOperationException(fault),
        };
        var error = await Record.ExceptionAsync(() => rig.RawAsync(session, body));
        Assert.True(error is PacketFormatException or ArgumentException, error?.ToString() ?? "The malformed packet was accepted.");
        Assert.Equal(saved, await rig.Store.ReadKeysAsync(1, rig.Build, default));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task TypedIngressEnforcesIdentityAndFlagsAndCancellation()
    {
        await using var rig = await Rig.StartAsync();
        var session = rig.Session(1);
        CustomizedKey[] saved = [new("Jump", 4)];
        await rig.UpdateAsync(session, false, saved);
        var router = rig.Services.Get<IMessageRouter>();
        await Assert.ThrowsAsync<InvalidDataException>(() => router.DispatchAsync(rig.Protocol, session,
            new CustomizedKeysUpdated(true, saved), default).AsTask());
        var anonymous = rig.Session(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.DispatchAsync(rig.Protocol, anonymous,
            new CustomizedKeysUpdated(false, saved), default).AsTask());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.DispatchAsync(rig.Protocol, session,
            new CustomizedKeysUpdated(true, []), cancelled.Token).AsTask());
        Assert.Equal(saved, await rig.Store.ReadKeysAsync(1, rig.Build, default));
    }

    [Fact]
    public async Task LoginIgnoresOtherProtocolsAndRejectsAnotherPlayersIdentity()
    {
        await using var rig = await Rig.StartAsync();
        var session = rig.Session(1);
        await rig.Events.SerialAsync(new LoginMenuInstalled("other/game", session, Login(2)), default);
        Assert.Empty(session.Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Events.SerialAsync(
            new LoginMenuInstalled(rig.Protocol, session, Login(2)), default).AsTask());
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task OpcodesAreResolvedByNameAndUnloadRemovesEveryRegistration()
    {
        await using var rig = await Rig.StartAsync("kr-1256", 41001, 41002);
        var session = rig.Session(1);
        await rig.UpdateAsync(session, false, [new("Fire", 2)]);
        await rig.LoginAsync(session);
        Assert.Equal((ushort)41002, Assert.Single(session.Sent).Opcode);
        session.Sent.Clear();
        await rig.Domain.UnwindAsync();
        await rig.Dialect.UnwindAsync();
        Assert.Empty(rig.Services.GetAll<IMessageSender<CustomizedKeysRestored>>());
        Assert.Empty(rig.Services.GetAll<IInboundMessageBinding<CustomizedKeysUpdated>>());
        Assert.False(await rig.Services.Get<IMessageRouter>().DispatchAsync(rig.Protocol, session,
            new CustomizedKeysUpdated(true, []), default));
        await rig.RawAsync(session, Body(true, []));
        await rig.LoginAsync(session);
        Assert.Empty(session.Sent);
        Assert.Equal(new[] { new CustomizedKey("Fire", 2) }, await rig.Store.ReadKeysAsync(1, rig.Build, default));
        await rig.RemountAsync();
        await rig.LoginAsync(session);
        Assert.Single(session.Sent);
    }

    private static LoginSucceeded Login(int user) => new(user, "account", "name", 1,
        new DateTime(2026, 10, 16), 300001, [300001], null, [], []);

    [Fact]
    public async Task ValidReplacementAndSupplementaryCharactersAreNotRejectedAsMalformedEncoding()
    {
        await using var rig = await Rig.StartAsync();
        var session = rig.Session(1);
        CustomizedKey[] mappings = [new("\uFFFD", 7), new("\U00010400", -1)];
        await rig.UpdateAsync(session, false, mappings);
        await rig.LoginAsync(session);
        Assert.Equal(Body(true, mappings), Assert.Single(session.Sent).Payload);
        Assert.Equal(mappings, await rig.Store.ReadKeysAsync(1, rig.Build, default));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task MissingOrAmbiguousBindingsFailBeforeInstallingHandlers(int inboundCount, int senderCount)
    {
        await using var rig = await Rig.StartAsync(mount: false);
        var matching = new ProbeBinding(rig.Protocol);
        var other = new ProbeBinding("other/game");
        var registrations = new List<IDisposable>
        {
            rig.Services.RegisterMany<IInboundMessageBinding<CustomizedKeysUpdated>>(other),
            rig.Services.RegisterMany<IMessageSender<CustomizedKeysRestored>>(other),
        };
        var context = new PluginContext(new PluginRow
        {
            Id = "settings-candidate", Plugin = "player.settings", Source = "settings-test",
            Config = new Dictionary<string, object?> { ["protocol"] = rig.Protocol, ["scope"] = rig.Build },
        }, rig.Services, rig.Events, NullLogger.Instance);
        try
        {
            for (var i = 0; i < inboundCount; i++)
                registrations.Add(rig.Services.RegisterMany<IInboundMessageBinding<CustomizedKeysUpdated>>(matching));
            for (var i = 0; i < senderCount; i++)
                registrations.Add(rig.Services.RegisterMany<IMessageSender<CustomizedKeysRestored>>(matching));
            await Assert.ThrowsAsync<ConfigException>(() => new PlayerSettingsPlugin().ActivateAsync(context, default).AsTask());
            var session = rig.Session(1);
            Assert.False(await rig.Services.Get<IMessageRouter>().DispatchAsync(rig.Protocol, session,
                new CustomizedKeysUpdated(false, [new("Fire", 1)]), default));
            await rig.LoginAsync(session);
            Assert.Empty(await rig.Store.ReadKeysAsync(1, rig.Build, default));
            Assert.Equal(0, matching.Sends);
            Assert.Equal(0, other.Sends);
        }
        finally
        {
            await context.UnwindAsync();
            foreach (var registration in registrations.AsEnumerable().Reverse()) registration.Dispose();
        }
    }

    private static byte[] InvalidUtf16Name(ushort codeUnit)
    {
        var body = Body(false, [new("A", 1)]);
        body[12] = (byte)codeUnit;
        body[13] = (byte)(codeUnit >> 8);
        return body;
    }

    // The four audited native builders write bool32, count, then counted UTF-16 names and key words.
    private static byte[] Body(bool flag, IReadOnlyList<CustomizedKey> mappings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(flag ? 1 : 0);
        writer.Write(mappings.Count);
        foreach (var row in mappings)
        {
            writer.Write(row.Name.Length);
            writer.Write(Encoding.Unicode.GetBytes(row.Name));
            writer.Write(row.Key);
        }
        return stream.ToArray();
    }

    private sealed class Rig(string build, ushort c2s) : IAsyncDisposable
    {
        internal string Build => build;
        internal string Protocol => build + "/game";
        internal ServiceRegistry Services { get; } = new();
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal MemoryPlayerSettings Store { get; } = new(new(4), new());
        internal PluginContext Domain { get; private set; } = null!;
        internal PluginContext Dialect { get; private set; } = null!;
        private readonly List<PluginContext> _core = [];
        private readonly List<IDisposable> _registrations = [];
        internal static async Task<Rig> StartAsync(string build = "kr-1256", ushort c2s = 2019, ushort s2c = 974,
            bool mount = true)
        {
            var rig = new Rig(build, c2s);
            foreach (var (name, plugin) in new (string, IPlugin)[]
            {
                ("core.message-router", new MessageRouterPlugin()),
                ("core.packet-router", new PacketRouterPlugin()),
                ("core.opcode-tables", new OpcodeTableRegistryPlugin()),
            })
            {
                var context = rig.Context(name, null);
                rig._core.Add(context);
                await plugin.ActivateAsync(context, default);
            }
            rig._registrations.Add(rig.Services.Register<IPlayerSettingsRepository>(rig.Store));
            var table = new OpcodeTable(Profile(build),
            [
                new(c2s, PacketDirection.ClientToServer, "gcpUserUpdateCustomizedKeyList", Evidence.Verified, null, null),
                new(s2c, PacketDirection.ServerToClient, "gspRepCustomizedKeyList", Evidence.Verified, null, null),
            ]);
            rig._registrations.Add(rig.Services.Get<IOpcodeTableRegistry>().Register(table));
            if (mount) await rig.RemountAsync();
            return rig;
        }

        internal async Task RemountAsync()
        {
            Dialect = Context("dialect.customized-keys", new Dictionary<string, object?>
                { ["protocol"] = Protocol, ["packets_directory"] = Path.Combine(Root(), "packets") });
            Domain = Context("player.settings", new Dictionary<string, object?> { ["protocol"] = Protocol, ["scope"] = build });
            await new CustomizedKeysDialectPlugin().ActivateAsync(Dialect, default);
            await new PlayerSettingsPlugin().ActivateAsync(Domain, default);
        }

        private PluginContext Context(string name, IReadOnlyDictionary<string, object?>? config) => new(new PluginRow
        {
            Id = name, Plugin = name, Source = "settings-test", Lineage = [build], Config = config,
        }, Services, Events, NullLogger.Instance);

        internal Session Session(int user) => new(build, user);
        internal Task RawAsync(Session session, byte[] body) => Services.Get<IPacketRouter>()
            .DispatchAsync(Protocol, session, new(c2s, body), default).AsTask();
        internal Task UpdateAsync(Session session, bool defaults, CustomizedKey[] rows) => RawAsync(session, Body(defaults, rows));
        internal Task LoginAsync(Session session) => Events.SerialAsync(new LoginMenuInstalled(Protocol, session,
            Login(session.Feature<IPlayerSession>().Identity.UserNum)), default).AsTask();

        public async ValueTask DisposeAsync()
        {
            if (Domain is not null) await Domain.UnwindAsync();
            if (Dialect is not null) await Dialect.UnwindAsync();
            foreach (var registration in _registrations.AsEnumerable().Reverse()) registration.Dispose();
            foreach (var context in _core.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
    }

    /// <summary>A table that retypes a consumed customized-key field is refused when the dialect row activates.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheDialectActivates()
    {
        var directory = PacketTableEdits.WithFieldType("CustomizedKeysUpdate", "use_defaults", "bool", "u32");
        await using var rig = await Rig.StartAsync(mount: false);
        var context = new PluginContext(new PluginRow
        {
            Id = "dialect.customized-keys", Plugin = "dialect.customized-keys", Source = "test", Lineage = ["kr-1256"],
            Config = new Dictionary<string, object?> { ["protocol"] = "kr-1256/game", ["packets_directory"] = directory.FullName },
        }, rig.Services, rig.Events, NullLogger.Instance);
        try
        {
            var error = Assert.Throws<ConfigException>(() =>
            {
                _ = new CustomizedKeysDialectPlugin().ActivateAsync(context, default);
            });
            Assert.Contains("use_defaults", error.Message, StringComparison.Ordinal);
            Assert.Contains("Customized keys", error.Message, StringComparison.Ordinal);
            Assert.Empty(rig.Services.GetAll<IInboundMessageBinding<CustomizedKeysUpdated>>());
            Assert.Empty(rig.Services.GetAll<IMessageSender<CustomizedKeysRestored>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }

    private static ClientProfile Profile(string build) => new(build, build.Split('-')[0], int.Parse(build.Split('-')[1],
        System.Globalization.CultureInfo.InvariantCulture), 0, 0, build);

    private sealed class ProbeBinding(string protocol)
        : IInboundMessageBinding<CustomizedKeysUpdated>, IMessageSender<CustomizedKeysRestored>
    {
        public string Protocol => protocol;
        internal int Sends { get; private set; }
        public ValueTask SendAsync(ISession session, CustomizedKeysRestored message, CancellationToken ct)
        {
            Sends++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session : ISessionWithFeatures
    {
        internal Session(string build, int user)
        {
            Profile = CustomizedKeysPluginTests.Profile(build);
            if (user > 0) this.SetLobbyPlayer(user, pastLoginBurst: false);
        }
        public long Id => 1;
        public IConnection Connection { get; } = new Connection();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile { get; }
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((opcode, payload.ToArray()));
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
}
