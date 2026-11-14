using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited enchant result vector and rejects incomplete admission before any route is published.</summary>
public sealed class LabEnchantCompletionTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";

    /// <summary>Success and failure use their native result words and preserve ordered Article references in the third vector.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CompletionPinsTheThirdWideVectorAndNamedReply(bool succeeded, bool remap)
    {
        var original = LabTestTables.Opcodes(Build);
        var table = remap ? new OpcodeTable(original.Profile, original.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : original;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(Protocol, table, TestBuilds.Packets, Build).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
            await sender.SendAsync(session, new(succeeded, [PetArticleKeys.First, 0x1_0000_0001, PetArticleKeys.Last]), default);
            Assert.Equal(table.S2C("gspRepEnchant"), Assert.Single(session.Sent));
            var body = Assert.Single(session.Bodies);
            Assert.Equal(48, body.Length);
            Assert.Equal((succeeded ? "00000000" : "10000000")
                + "00000000000000000300000000E1F505000000000100000001000000FFFFFFFFFFFFFF7F0000000000000000",
                Convert.ToHexString(body));
            var read = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
            Assert.Empty((object?[])read["unknown_4"]!);
            Assert.Empty((object?[])read["unknown_16"]!);
            Assert.Equal(new object?[] { (long)PetArticleKeys.First, 0x1_0000_0001L, long.MaxValue }, (object?[])read["unknown_28"]!);
            Assert.Equal(0L, read["unknown_40"]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
    }

    /// <summary>Sharing an audited reply layout cannot advertise successful mutation for an unlisted client.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task ClosedOnlyBuildsNeverAdvertiseCompletion(string build)
    {
        var table = LabTestTables.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new LabBinding(protocol, table, TestBuilds.Packets, build);
        try
        {
            binding.Mount(context);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await binding.SendAsync(session, new LabEnchantCompleted(true, [PetArticleKeys.First]), default));
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Invalid references never reach the client; the inclusive maximum batch still fits the bounded result body.</summary>
    [Fact]
    public async Task CompletionRejectsInvalidReferencesAndAcceptsTheBoundedLimit()
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new LabBinding(Protocol, table, TestBuilds.Packets, Build);
        ulong[][] invalid =
        [
            [], [0], [PetArticleKeys.First - 1], [PetArticleKeys.Last + 1], [ulong.MaxValue],
            [PetArticleKeys.First, PetArticleKeys.First],
            Enumerable.Range(0, 129).Select(index => PetArticleKeys.First + (ulong)index).ToArray(),
        ];
        foreach (var targets in invalid)
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await binding.SendAsync(session, new LabEnchantCompleted(true, targets), default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await binding.SendAsync(session, (LabEnchantCompleted)null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await binding.SendAsync(session, new LabEnchantCompleted(true, null!), default));
        Assert.Empty(session.Sent);
        var maximum = Enumerable.Range(0, 128).Select(index => PetArticleKeys.First + (ulong)index).ToArray();
        await binding.SendAsync(session, new LabEnchantCompleted(false, maximum), default);
        var body = Assert.Single(session.Bodies);
        Assert.Equal(1048, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
        Assert.Equal(maximum.Select(key => (object?)(long)key), (object?[])read["unknown_28"]!);
    }

    /// <summary>Cancellation and transport errors propagate from the completion sender without an alternate fallback reply.</summary>
    [Fact]
    public async Task CompletionPreservesCancellationAndTransportFailure()
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new LabBinding(Protocol, table, TestBuilds.Packets, Build);
        var message = new LabEnchantCompleted(true, [PetArticleKeys.First]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await binding.SendAsync(session, message, new CancellationToken(true)));
        session.FailSend = true;
        await Assert.ThrowsAsync<IOException>(async () => await binding.SendAsync(session, message, default));
        Assert.Empty(session.Sent);
    }

    /// <summary>Malformed admission, missing result data and incompatible result fields fail before any laboratory effect exists.</summary>
    [Theory]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: kr-1256")]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: [kr-1256, kr-1256]")]
    [InlineData("builds.yaml", "laboratory_enchant_builds: [kr-1256]", "laboratory_enchant_builds: [kr-1256, missing-build]")]
    [InlineData("builds.yaml", "laboratory_enchant_success_result:", "missing_enchant_success_result:")]
    [InlineData("builds.yaml", "laboratory_enchant_failure_result:", "missing_enchant_failure_result:")]
    [InlineData("builds.yaml", "laboratory_enchant_success_result: 0", "laboratory_enchant_success_result: 5")]
    [InlineData("builds.yaml", "laboratory_enchant_failure_result: 16", "laboratory_enchant_failure_result: 0")]
    [InlineData("builds.yaml", "laboratory_enchant_failure_result: 16", "laboratory_enchant_failure_result: -1")]
    [InlineData("builds.yaml", "  kr-1256:\n", "  kr-1256:\n    laboratory_operations: [Compound]\n")]
    [InlineData("lab.yaml", "unknown_28: list<u64>", "unknown_29: list<u64>")]
    [InlineData("lab.yaml", "unknown_28: list<u64>", "unknown_28: list<u32>")]
    public async Task CompletionConfigurationFailsBeforeRegistration(string file, string before, string after)
    {
        var packets = LabTestTables.Modified(file, before, after);
        var table = LabTestTables.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        try
        {
            Assert.Throws<ConfigException>(() => new LabBinding(Protocol, table, packets, Build).Mount(context));
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationAnswered>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabOperationRefused>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabEnchantCompleted>>());
        }
        finally { await context.UnwindAsync(); }
    }
}
