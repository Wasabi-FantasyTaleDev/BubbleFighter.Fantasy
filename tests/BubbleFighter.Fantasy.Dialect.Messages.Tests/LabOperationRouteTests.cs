using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises typed laboratory admission, the closed fallback and registration lifetime.</summary>
public sealed class LabOperationRouteTests
{
    private static readonly string[] Requests =
    [
        "gcpReqScienceLabCompound_", "gcpReqScienceLabCompoundUnlimited_", "gcpReqEnchant",
        "gcpReqScienceLabPetCompound_", "gcpReqScienceLabMakeItemAdvanced_", "gcpReqChangeUnlimited",
        "gcpReqScienceLabModification_", "gcpReqScienceLabModificationDelete_",
    ];
    private static readonly string[] Replies =
    [
        "gspRepCompound", "gspRepCompoundUnlimited", "gspRepEnchant", "gspRepPetCompound",
        "gspRepMakeItemAdvanced", "gspRepChangeUnlimited", "gspRepModification", "gspRepModificationDelete",
    ];
    private const string RequestBody = "020000000001000000000000FFFFFFFFFFFFFFFF0500000000000000";

    /// <summary>The service registered by the binding is admitted by the plugin declaration.</summary>
    [Fact]
    public void LabSenderIsDeclaredByThePlugin()
    {
        var declared = typeof(MessagesPlugin).GetCustomAttribute<PluginAttribute>()!.ConditionalProvidesMany;
        Assert.Contains(typeof(IInboundMessageBinding<LabOperationRequested>), declared);
        Assert.Contains(typeof(IMessageSender<LabOperationAnswered>), declared);
        Assert.Contains(typeof(IMessageSender<LabOperationRefused>), declared);
        Assert.Contains(typeof(IMessageSender<LabEnchantCompleted>), declared);
    }

    /// <summary>Every operation uses its build's named request and reply with ordinary login enabled.</summary>
    [Theory]
    [InlineData("kr-1256", false, 5)]
    [InlineData("kr-1256", true, 5)]
    [InlineData("cn-774", false, 4)]
    [InlineData("cn-774", true, 4)]
    [InlineData("tw-454", false, 4)]
    [InlineData("tw-454", true, 4)]
    public async Task OperationsAnswerClosedAlongsideLoginAndUnload(string build, bool remap, int closedResult)
    {
        var full = Table(build);
        var table = remap ? new OpcodeTable(full.Profile, full.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        { ["science_lab"] = true, ["inventory"] = true });
        context.BindServices(typeof(MessagesPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.Single(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
            Assert.Empty(session.Sent);
            for (var i = 0; i < Requests.Length; ++i)
            {
                await fixture.Packets.DispatchAsync(protocol, session,
                    new(table.C2S(Requests[i]), Convert.FromHexString(RequestBody)), default);
                Assert.Equal(table.S2C(Replies[i]), session.Sent[i]);
                Assert.Equal($"{closedResult:X2}0000000000000000000000000000000000000000000000", Convert.ToHexString(session.Bodies[i]));
            }
            Assert.Equal(8, session.Sent.Count);
            Assert.Equal(8, fixture.Messages.Received.Count);
            for (var i = 0; i < Requests.Length; ++i)
            {
                var decoded = Assert.IsType<LabOperationRequested>(fixture.Messages.Received[i]);
                Assert.Equal((LabOperation)i, decoded.Operation);
                Assert.Equal(new long[] { 256, -1 }, decoded.Keys);
                Assert.Equal(5, decoded.Argument);
            }
            if (table.Find(PacketDirection.ClientToServer, "gcpReqUserLaboratoryState") is { } inferred)
            {
                await fixture.Packets.DispatchAsync(protocol, session, new(inferred, ReadOnlyMemory<byte>.Empty), default);
                Assert.Equal(8, session.Sent.Count);
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
    }

    /// <summary>A registered feature receives the complete operation without a competing closed reply.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task TypedHandlerOwnsTheReply(string build)
    {
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            Assert.Equal(protocol, Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>()).Protocol);
            await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S(Requests[2]), Convert.FromHexString(RequestBody)), default);
            var decoded = Assert.IsType<LabOperationRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(LabOperation.Enchant, decoded.Operation);
            Assert.Equal(new long[] { 256, -1 }, decoded.Keys);
            Assert.Equal(5, decoded.Argument);
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>());
    }

    /// <summary>The native request and reply numbers remain independent of their symbolic routing names.</summary>
    [Theory]
    [InlineData("kr-1256", new ushort[] { 131, 132, 134, 135, 136, 137, 138, 139 }, new ushort[] { 151, 152, 154, 155, 156, 157, 158, 159 })]
    [InlineData("cn-774", new ushort[] { 130, 131, 133, 134, 135, 136, 137, 138 }, new ushort[] { 151, 152, 154, 155, 156, 157, 158, 159 })]
    [InlineData("tw-454", new ushort[] { 130, 131, 133, 134, 135, 136, 137, 138 }, new ushort[] { 149, 150, 152, 153, 154, 155, 156, 157 })]
    public void OperationNumbersMatchTheNativeTables(string build, ushort[] requests, ushort[] replies)
    {
        var table = Table(build);
        Assert.Equal(requests, Requests.Select(table.C2S));
        Assert.Equal(replies, Replies.Select(table.S2C));
    }

    /// <summary>The restored operation labels resolve in their own directions on the game connection only.</summary>
    [Theory]
    [InlineData("kr-1256", 137, 157)]
    [InlineData("cn-774", 136, 157)]
    [InlineData("tw-454", 136, 155)]
    [InlineData("cn-738", 134, 146)]
    public void ChangeUnlimitedNamesKeepTheirNativeNamespaces(string build, int request, int reply)
    {
        var game = Table(build);
        Assert.Equal((ushort)request, game.C2S("gcpReqChangeUnlimited"));
        Assert.Equal((ushort)reply, game.S2C("gspRepChangeUnlimited"));
        var requestRow = Assert.Single(game.All, row => row.Direction == PacketDirection.ClientToServer
            && row.Name == "gcpReqChangeUnlimited");
        var replyRow = Assert.Single(game.All, row => row.Direction == PacketDirection.ServerToClient
            && row.Name == "gspRepChangeUnlimited");
        Assert.Equal("label@cn-738", requestRow.NameSource);
        Assert.Equal("label@" + build, replyRow.NameSource);
        Assert.Equal(Evidence.Verified, requestRow.Evidence);
        Assert.Equal(Evidence.Verified, replyRow.Evidence);
        Assert.Equal(OpcodeConnection.Game, requestRow.Connection);
        Assert.Equal(OpcodeConnection.Game, replyRow.Connection);
        Assert.Null(game.Find(PacketDirection.ServerToClient, "gcpReqChangeUnlimited"));
        Assert.Null(game.Find(PacketDirection.ClientToServer, "gspRepChangeUnlimited"));
        Assert.Null(game.Find(PacketDirection.ClientToServer, "gcpReqScienceLabChangeUnlimited_"));
        var relay = game.ForConnection(OpcodeConnection.Relay);
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "gcpReqChangeUnlimited"));
        Assert.Null(relay.Find(PacketDirection.ServerToClient, "gspRepChangeUnlimited"));
    }

    /// <summary>The restored modern request routes a single base item identity to the existing closed fallback.</summary>
    [Theory]
    [InlineData("kr-1256", 5)]
    [InlineData("cn-774", 4)]
    [InlineData("tw-454", 4)]
    public async Task ChangeUnlimitedNativeRequestKeepsTheClosedRoute(string build, int closedResult)
    {
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            var body = Convert.FromHexString("01000000A4870100000000000000000000000000");
            Assert.Equal(20, body.Length);
            await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpReqChangeUnlimited"), body), default);
            var decoded = Assert.IsType<LabOperationRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(LabOperation.ChangeUnlimited, decoded.Operation);
            Assert.Equal(new long[] { 100260 }, decoded.Keys);
            Assert.Equal(0, decoded.Argument);
            Assert.Equal(table.S2C("gspRepChangeUnlimited"), Assert.Single(session.Sent));
            Assert.Equal($"{closedResult:X2}0000000000000000000000000000000000000000000000",
                Convert.ToHexString(Assert.Single(session.Bodies)));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Restoring the older native labels does not admit another operation through its inherited Enchant-only policy.</summary>
    [Fact]
    public async Task Cn738ChangeUnlimitedLabelsDoNotEnableConversion()
    {
        const string build = "cn-738";
        const string protocol = build + "/game";
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            var binding = new LabBinding(protocol, table, TestBuilds.Packets, build);
            binding.Mount(context);
            Assert.Equal((protocol, table.C2S("gcpReqEnchant")), Assert.Single(fixture.Packets.Handlers).Key);
            Assert.False(fixture.Packets.Handlers.ContainsKey((protocol, table.C2S("gcpReqChangeUnlimited"))));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await binding.SendAsync(session, new LabOperationRefused(LabOperation.ChangeUnlimited), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Leaving the option at its default publishes neither operation routes nor a sender.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task DefaultOptionKeepsOperationsUnbound(string build)
    {
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new() { ["login"] = false });
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.All(Requests, name => Assert.False(fixture.Packets.Handlers.ContainsKey((protocol, table.C2S(name)))));
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A missing or unverified operation refuses activation before the lab publishes any effect.</summary>
    [Theory]
    [InlineData("missing-request")]
    [InlineData("missing-reply")]
    [InlineData("inferred-request")]
    [InlineData("inferred-reply")]
    [InlineData("legacy")]
    public async Task IncompleteEvidenceFailsBeforeRegistration(string fault)
    {
        var build = fault == "legacy" ? "kr-812" : "kr-1256";
        var full = Table(build);
        var target = fault.EndsWith("request", StringComparison.Ordinal) ? Requests[^1] : Replies[^1];
        var rows = full.All.Where(row => !fault.StartsWith("missing", StringComparison.Ordinal) || row.Name != target)
            .Select(row => fault.StartsWith("inferred", StringComparison.Ordinal) && row.Name == target
                ? row with { Evidence = Evidence.Inferred } : row);
        var table = new OpcodeTable(full.Profile, rows);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        try
        {
            var binding = new LabBinding(build + "/game", table, TestBuilds.Packets, build);
            Assert.Throws<ConfigException>(() => binding.Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A missing closed result cannot silently fall back to another client's result code.</summary>
    [Fact]
    public async Task MissingClosedResultFailsBeforeRegistration()
    {
        var directory = TestTempDirectories.ForTest("bff-lab-closed-result");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var builds = Path.Combine(directory, "builds.yaml");
        File.WriteAllText(builds, File.ReadAllText(builds).Replace("laboratory_closed_result:", "unavailable_laboratory_result:", StringComparison.Ordinal));
        var table = Table("kr-1256");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.messages");
        try
        {
            var binding = new LabBinding("kr-1256/game", table, PacketTables.Load(directory), "kr-1256");
            Assert.Throws<ConfigException>(() => binding.Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Operation restrictions must name a nonempty distinct set of known operations, without numeric enum aliases.</summary>
    [Theory]
    [InlineData("Enchant")]
    [InlineData("[]")]
    [InlineData("[Enchant, enchant]")]
    [InlineData("[MissingOperation]")]
    [InlineData("[2]")]
    public async Task InvalidOperationAdmissionFailsBeforeRegistration(string operations)
    {
        var packets = LabTestTables.Modified("builds.yaml", "  kr-1256:\n",
            "  kr-1256:\n    laboratory_operations: " + operations + "\n");
        var table = Table("kr-1256");
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-1256", "kr-1256/game", "dialect.messages");
        try
        {
            Assert.Throws<ConfigException>(() => new LabBinding("kr-1256/game", table, packets, "kr-1256").Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationRefused>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Truncated, trailing and oversized request bodies fail before a reply is sent.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task MalformedBodiesDoNotSend(string build)
    {
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            var valid = Convert.FromHexString(RequestBody);
            byte[][] invalid = [[], valid[..^1], [.. valid, 0], Convert.FromHexString("011000000000000000000000")];
            foreach (var body in invalid)
                await Assert.ThrowsAsync<PacketFormatException>(async () => await fixture.Packets.DispatchAsync(protocol,
                    session, new(table.C2S(Requests[0]), body), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Cancellation and transport failure are propagated, and unknown operations cannot send.</summary>
    [Fact]
    public async Task CancellationSendFailureAndInvalidOperationAreNotSwallowed()
    {
        const string build = "kr-1256";
        const string protocol = build + "/game";
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        fixture.Messages.HasHandler = false;
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            var binding = new LabBinding(protocol, table, TestBuilds.Packets, build);
            binding.Mount(context);
            var request = new InboundPacket(table.C2S(Requests[0]), Convert.FromHexString(RequestBody));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, request, new CancellationToken(true)));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await binding.SendAsync(session, new LabOperationAnswered(LabOperation.Compound, 5), new CancellationToken(true)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await binding.SendAsync(session, new LabOperationAnswered((LabOperation)99, 5), default));
            Assert.Empty(session.Sent);
            session.FailSend = true;
            await Assert.ThrowsAsync<IOException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, request, default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The closed sender rejects unsupported success or result codes before sending any frame.</summary>
    [Theory]
    [InlineData("kr-1256", 5)]
    [InlineData("cn-774", 4)]
    [InlineData("tw-454", 4)]
    public async Task ReplyOnlySupportsTheAuditedClosedResult(string build, int closedResult)
    {
        var table = Table(build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new LabBinding(build + "/game", table, TestBuilds.Packets, build);
        foreach (var invalid in new[] { 0, -1, closedResult + 1 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await binding.SendAsync(session, new LabOperationAnswered(LabOperation.Enchant, invalid), default));
        Assert.Empty(session.Sent);
        await binding.SendAsync(session, new LabOperationAnswered(LabOperation.Enchant, closedResult), default);
        Assert.Equal(table.S2C("gspRepEnchant"), Assert.Single(session.Sent));
        Assert.Equal($"{closedResult:X2}0000000000000000000000000000000000000000000000",
            Convert.ToHexString(Assert.Single(session.Bodies)));
    }

    private static IOpcodeTable Table(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        return OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
    }
}
