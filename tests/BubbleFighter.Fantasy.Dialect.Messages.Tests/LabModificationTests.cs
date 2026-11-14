using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Verifies native modification result references, configuration floats and exact-build admission.</summary>
public sealed class LabModificationTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";

    /// <summary>Apply and delete retain the full Article identity followed by selector and group in the third vector.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletionUsesNamedOpcodeAndNativeVector(bool deleted, bool remap)
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
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<LabModificationCompleted>>());
            await sender.SendAsync(session, new(deleted, PetArticleKeys.Last, 156, 2), default);
            Assert.Equal(table.S2C(deleted ? "gspRepModificationDelete" : "gspRepModification"), Assert.Single(session.Sent));
            var body = Assert.Single(session.Bodies);
            Assert.Equal(48, body.Length);
            var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
            Assert.Equal(0, fields["result"]);
            Assert.Empty((object?[])fields["unknown_4"]!);
            Assert.Empty((object?[])fields["unknown_16"]!);
            Assert.Equal(new object?[] { long.MaxValue, 156L, 2L }, (object?[])fields["unknown_28"]!);
            Assert.Equal(0L, fields["unknown_40"]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabModificationCompleted>>());
    }

    /// <summary>Invalid native identifiers never produce a successful result packet.</summary>
    [Theory]
    [InlineData(0, 101, 0)]
    [InlineData(100000000, 100, 0)]
    [InlineData(100000000, 157, 0)]
    [InlineData(100000000, 101, -1)]
    [InlineData(100000000, 101, 3)]
    public async Task InvalidCompletionDoesNotSend(long article, int selector, int group)
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new LabBinding(Protocol, table, TestBuilds.Packets, Build)
                .SendAsync(session, new LabModificationCompleted(false, (ulong)article, selector, group), default));
        Assert.Empty(session.Sent);
    }

    /// <summary>A descendant's inherited packet layout does not admit weapon modification.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task UnauditedBuildCannotPublishCompletion(string build)
    {
        var table = LabTestTables.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        try
        {
            new LabBinding(build + "/game", table, TestBuilds.Packets, build).Mount(context);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabModificationCompleted>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Preview percentages use IEEE float words while all seven signed pairs retain their selector association.</summary>
    [Fact]
    public async Task ConfigurationPinsPreviewFloatsAndWeights()
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var configuration = Configuration();
        var message = new ScienceLabConfigurationPublished([190111, 190106, 190107, 190108, 190109], 101745)
            { Modification = configuration };
        await new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build).SendAsync(session, message, default);
        Assert.Equal(table.S2C("gspRepSciendLabConfig"), Assert.Single(session.Sent));
        var body = Assert.Single(session.Bodies);
        Assert.Equal(276, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", body);
        Assert.Equal(true, fields["unknown_8"]);
        var part = Assert.IsType<Dictionary<string, object?>>(Assert.Single((object?[])fields["modification_configurations"]!));
        Assert.Equal(BitConverter.SingleToInt32Bits(1.6f), part["unknown_28"]);
        Assert.Equal(Enumerable.Range(101, 7).Cast<object?>(), (object?[])part["unknown_16"]!);
        var negative = Assert.IsType<Dictionary<string, object?>>(((object?[])part["unknown_32"]!)[0]);
        Assert.Equal(101, negative["key"]);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0.4f), negative["value"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfiguration", fields));
    }

    /// <summary>Malformed preview policies are refused before any configuration is published.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidCategoryWeightsFailBeforeEncoding(int weight)
    {
        var configuration = Configuration() with
        { TagWeights = new Dictionary<int, IReadOnlyList<int>> { [0] = new[] { weight, 1, 1 } } };
        Assert.Throws<ArgumentException>(() => ModificationConfigurationWire.Apply(new(), configuration));
    }

    private static ModificationConfiguration Configuration() => new(
        [new(190112, 0, 1, Enumerable.Range(101, 7).ToArray(), 160,
            Enumerable.Range(101, 7).ToDictionary(id => id, _ => -40))],
        new Dictionary<int, IReadOnlyList<int>> { [0] = new[] { 3, 2, 1 } },
        new Dictionary<int, IReadOnlyList<int>> { [1] = new[] { 1, 2, 3 } });
}
