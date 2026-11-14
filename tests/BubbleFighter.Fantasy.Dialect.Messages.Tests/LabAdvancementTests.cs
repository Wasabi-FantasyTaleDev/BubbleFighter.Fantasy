using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the grade-up result, the rule groups of the laboratory configuration and exact-build admission.</summary>
public sealed class LabAdvancementTests
{
    private const string Build = "kr-1256";
    private const string Protocol = Build + "/game";

    /// <summary>The grade-up answers result 0 with the set identity and one member's Article key in the third vector: 40 bytes.</summary>
    [Fact]
    public async Task CompletionWritesTheNativeSetReply()
    {
        var table = LabTestTables.Opcodes(Build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(Build, Protocol, "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        try
        {
            new LabBinding(Protocol, table, TestBuilds.Packets, Build).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<LabAdvancementCompleted>>());
            await sender.SendAsync(session, new(2000508, PetArticleKeys.First + 7), default);
            Assert.Equal(table.S2C("gspRepMakeItemAdvanced"), Assert.Single(session.Sent));
            var body = Assert.Single(session.Bodies);
            Assert.Equal(40, body.Length);
            var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabReply", body);
            Assert.Equal(0, fields["result"]);
            Assert.Empty((object?[])fields["unknown_4"]!);
            Assert.Empty((object?[])fields["unknown_16"]!);
            Assert.Equal(new object?[] { 2000508L, checked((long)PetArticleKeys.First + 7) }, (object?[])fields["unknown_28"]!);
            Assert.Equal(0L, fields["unknown_40"]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<LabAdvancementCompleted>>());
    }

    /// <summary>A reply without a set identity or an Article member never produces a success packet.</summary>
    [Theory]
    [InlineData(0, 100000001UL)]
    [InlineData(2000508, 190035UL)]
    public async Task OnlyASetAndAnArticleAreWritten(int set, ulong member)
    {
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new LabBinding(Protocol, table, TestBuilds.Packets, Build).SendAsync(session, new LabAdvancementCompleted(set, member), default));
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
            Assert.Empty(fixture.Services.GetAll<IMessageSender<LabAdvancementCompleted>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static ScienceLabAdvancementConfiguration Rules() => new(190247,
    [
        new(1,
        [
            new(190247, [100, 157], [], [], 4),
            new(190558, [157], [100], [2000510, 2000511], 6),
        ]),
        new(3, [new(190570, [158], [], [], 6)]),
    ]);

    /// <summary>The configuration shows tab 6 and carries its card and the rule groups exactly as the client's readers consume them.</summary>
    [Fact]
    public async Task ConfigurationCarriesTheTabCardAndRuleGroups()
    {
        var table = LabTestTables.Opcodes(Build);
        var binding = new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build);
        var closed = new MatchMessagesMountTests.Session(table.Profile);
        var open = new MatchMessagesMountTests.Session(table.Profile);
        var message = new ScienceLabConfigurationPublished([190111, 190106, 190107, 190108, 190109], 101745);
        await binding.SendAsync(closed, message, default);
        await binding.SendAsync(open, message with { Advancement = Rules() }, default);

        var without = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", Assert.Single(closed.Bodies));
        var body = Assert.Single(open.Bodies);
        var with = PacketCodec.Read(TestBuilds.Packets, Build, "ScienceLabConfiguration", body);
        Assert.Equal(false, without["unknown_7"]);
        Assert.Equal(0, without["unknown_64"]);
        Assert.Empty((object?[])without["unknown_124"]!);
        Assert.Equal(true, with["unknown_7"]);
        Assert.Equal(190247, with["unknown_64"]);
        var groups = (object?[])with["unknown_124"]!;
        Assert.Equal([1, 3], groups.Cast<IReadOnlyDictionary<string, object?>>().Select(group => group["key"]));
        var second = ((object?[])((IReadOnlyDictionary<string, object?>)groups[0]!)["values"]!)[1] as IReadOnlyDictionary<string, object?>;
        Assert.Equal(190558, second!["unknown_0"]);
        Assert.Equal(new object?[] { 2000510, 2000511 }, (object?[])second["unknown_16"]!);
        Assert.Empty((object?[])second["unknown_24"]!);
        Assert.Equal((byte)6, second["unknown_48"]);
        var blocking = (IReadOnlyDictionary<string, object?>)((object?[])second["unknown_4"]!)[0]!;
        var required = (IReadOnlyDictionary<string, object?>)((object?[])second["unknown_36"]!)[0]!;
        Assert.Equal((157, 100), (blocking["unknown_4"], required["unknown_4"]));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "ScienceLabConfiguration", with));
        // The groups add, beyond the closed body: per group a key and a count (8); per rule 25 bytes (card, two content
        // counts, two set counts, the +32 word and the marker) plus 20 per content and 4 per set identity.
        // Rules(): group 1 holds 65 + 73, group 3 holds 45.
        Assert.Equal(2 * 8 + 65 + 73 + 45, body.Length - Assert.Single(closed.Bodies).Length);
    }

    /// <summary>Rules the client cannot evaluate are refused before any packet is written.</summary>
    [Theory]
    [InlineData("marker")]
    [InlineData("card")]
    [InlineData("empty_group")]
    [InlineData("repeated_group")]
    [InlineData("tab_card")]
    [InlineData("repeated_effect")]
    public async Task InvalidRulesDoNotSend(string reason)
    {
        var rules = Rules();
        var groups = rules.Groups.ToList();
        var first = groups[0].Rules.ToList();
        ScienceLabAdvancementConfiguration invalid = reason switch
        {
            "marker" => rules with { Groups = [new(1, [first[0] with { TargetMarker = 5 }])] },
            "card" => rules with { Groups = [new(1, [first[0] with { CardItemId = 0 }])] },
            "empty_group" => rules with { Groups = [new(1, [])] },
            "repeated_group" => rules with { Groups = [groups[0], groups[0]] },
            "tab_card" => rules with { TabCardItemId = 0 },
            _ => rules with { Groups = [new(1, [first[0] with { BlockingEffects = [157, 157] }])] },
        };
        var table = LabTestTables.Opcodes(Build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await Assert.ThrowsAsync<ArgumentException>(async () => await new ScienceLabConfigurationBinding(Protocol, table, TestBuilds.Packets, Build)
            .SendAsync(session, new ScienceLabConfigurationPublished([190111, 190106, 190107, 190108, 190109], 101745) { Advancement = invalid }, default));
        Assert.Empty(session.Sent);
    }
}
