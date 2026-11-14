using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the permanent-conversion result, its configuration fields and exact-build admission.</summary>
public sealed class LabConversionTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";

    /// <summary>An individual conversion answers result 0 with its one Article key in the third vector: 32 bytes.</summary>
    [Fact]
    public async Task CompletionWritesTheNativeIndividualReply()
    {
        var table = LabTestTables.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(Protocol, table, TestBuilds.Packets, Build).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<LabConversionCompleted>>());
            await sender.SendAsync(session, new(PetArticleKeys.First), default);
            Assert.Equal(table.S2C("gspRepChangeUnlimited"), Assert.Single(session.Sent));
            var body = Assert.Single(session.Bodies);
            Assert.Equal(32, body.Length);
            var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
            Assert.Equal(0, fields["result"]);
            Assert.Empty((object?[])fields["unknown_4"]!);
            Assert.Empty((object?[])fields["unknown_16"]!);
            Assert.Equal(new object?[] { checked((long)PetArticleKeys.First) }, (object?[])fields["unknown_28"]!);
            Assert.Equal(0L, fields["unknown_40"]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabConversionCompleted>>());
    }

    /// <summary>A set conversion answers result 0 with its set identity and one member Article key: 40 bytes.</summary>
    [Fact]
    public async Task CompletionWritesTheNativeSetReply()
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await new LabBinding(Protocol, table, TestBuilds.Packets, Build)
            .SendAsync(session, LabConversionCompleted.ForSet(2000508, PetArticleKeys.First + 7), default);
        Assert.Equal(table.S2C("gspRepChangeUnlimited"), Assert.Single(session.Sent));
        var body = Assert.Single(session.Bodies);
        Assert.Equal(40, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
        Assert.Equal(0, fields["result"]);
        Assert.Empty((object?[])fields["unknown_4"]!);
        Assert.Empty((object?[])fields["unknown_16"]!);
        Assert.Equal(new object?[] { 2000508L, checked((long)PetArticleKeys.First + 7) }, (object?[])fields["unknown_28"]!);
        Assert.Equal(0L, fields["unknown_40"]);
    }

    /// <summary>Only the two native shapes are written: an Article, or a set identity followed by an Article.</summary>
    [Theory]
    [InlineData(new ulong[0])]
    [InlineData(new ulong[] { 2000508 })]
    [InlineData(new ulong[] { 100000000, 100000001 })]
    [InlineData(new ulong[] { 2000508, 190035 })]
    [InlineData(new ulong[] { 0, 100000001 })]
    [InlineData(new ulong[] { 2000508, 100000001, 100000002 })]
    public async Task OnlyTheNativeReplyShapesAreWritten(ulong[] keys)
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new LabBinding(Protocol, table, TestBuilds.Packets, Build).SendAsync(session, new LabConversionCompleted(keys), default));
        Assert.Empty(session.Sent);
    }

    /// <summary>A key outside the native Article range never produces a success packet.</summary>
    [Fact]
    public async Task ANonArticleKeyDoesNotSend()
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new LabBinding(Protocol, table, TestBuilds.Packets, Build).SendAsync(session, new LabConversionCompleted(190035), default));
        Assert.Empty(session.Sent);
    }

    /// <summary>Descendants inherit the layout but not the audited success result.</summary>
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
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabConversionCompleted>>());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The configuration shows tab 4 and carries the card and minimum days only when conversion is configured.</summary>
    [Fact]
    public async Task ConfigurationCarriesTheTabCardAndThreshold()
    {
        var table = LabTestTables.Opcodes(Build);
        var binding = new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build);
        var closed = new MatchMessagesMountTests.Session(table.Profile);
        var open = new MatchMessagesMountTests.Session(table.Profile);
        var message = new ScienceLabConfigurationPublished([190111, 190106, 190107, 190108, 190109], 101745);
        await binding.SendAsync(closed, message, default);
        await binding.SendAsync(open, message with { Conversion = new(190151, 30) }, default);

        var without = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", Assert.Single(closed.Bodies));
        var with = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", Assert.Single(open.Bodies));
        Assert.Equal(false, without["unknown_5"]);
        Assert.Equal(0, without["unknown_56"]);
        Assert.Equal(0, without["unknown_60"]);
        Assert.Equal(true, with["unknown_5"]);
        Assert.Equal(190151, with["unknown_56"]);
        Assert.Equal(30, with["unknown_60"]);
        Assert.Equal(Assert.Single(closed.Bodies).Length, Assert.Single(open.Bodies).Length);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await binding.SendAsync(open, message with { Conversion = new(190151, 0) }, default));
    }
}
