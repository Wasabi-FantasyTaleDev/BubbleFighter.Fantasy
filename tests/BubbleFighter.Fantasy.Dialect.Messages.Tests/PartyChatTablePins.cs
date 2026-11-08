using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the party recruiting and chat bodies added to <c>packets/party.yaml</c> from the kr-812 class
/// readers: the recruiting switch is one boolean word (kr-812 sub_80C160), the quick-start request one word
/// (kr-812 sub_80A910), the chat request one string (kr-812 sub_5C8FD0), the chat message a word, two strings,
/// a word and a boolean (kr-812 sub_92AB70; named from cn-717 sub_9CB6E0), and the join reply one word (kr-812 sub_80A910). A body that
/// changes on purpose updates these pins together with the client reader that decides it.
/// </summary>
public sealed class PartyChatTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The recruiting switch is one boolean word, four bytes.</summary>
    [Fact]
    public void PartyRecruitingRequestIsOneBoolean()
    {
        var body = Write("PartyRecruitingRequest", new Dictionary<string, object?> { ["unknown_4"] = true });

        Assert.Equal(4, body.Length);
        Assert.Equal("01000000", Convert.ToHexString(body));
    }

    /// <summary>The quick-start request is one kind word, four bytes; the senders set 1, 2 or 4
    /// (kr-812 sub_68E390, kr-812 sub_68E580, kr-812 sub_68EF80).</summary>
    [Fact]
    public void PartyQuickStartRequestIsOneWord()
    {
        var body = Write("PartyQuickStartRequest", new Dictionary<string, object?> { ["kind"] = 4 });

        Assert.Equal(4, body.Length);
        Assert.Equal("04000000", Convert.ToHexString(body));
    }

    /// <summary>The quick-start reply is one kind word, four bytes; 0 is the cancel form the stage-local
    /// consumer kr-812 sub_6875C0 exits on.</summary>
    [Fact]
    public void PartyQuickStartReplyIsOneWord()
    {
        var body = Write("PartyQuickStartReply", new Dictionary<string, object?> { ["kind"] = 1 });

        Assert.Equal(4, body.Length);
        Assert.Equal("01000000", Convert.ToHexString(body));
    }

    /// <summary>The chat request is one string: a 4-byte count and UTF-16 units, eight bytes for "ab".</summary>
    [Fact]
    public void PartyChatRequestCarriesOneString()
    {
        var named = Write("PartyChatRequest", new Dictionary<string, object?> { ["text"] = "ab" });

        Assert.Equal(8, named.Length);
        Assert.Equal("0200000061006200", Convert.ToHexString(named));

        var empty = Write("PartyChatRequest", new Dictionary<string, object?> { ["text"] = string.Empty });
        Assert.Equal(4, empty.Length);
    }

    /// <summary>The chat message: word, two strings, word and boolean; twenty bytes with empty strings.</summary>
    [Fact]
    public void PartyChatMessageKeepsTheFieldOrder()
    {
        var body = Write("PartyChatMessage", new Dictionary<string, object?>
        {
            ["user_number"] = 1,
            ["unknown_8"] = string.Empty,
            ["text"] = string.Empty,
            ["colour"] = 2,
            ["system"] = true,
        });

        Assert.Equal(20, body.Length);
        Assert.Equal("0100000000000000000000000200000001000000", Convert.ToHexString(body));
    }

    /// <summary>The join reply is one word, four bytes.</summary>
    [Fact]
    public void PartyJoinReplyIsOneWord()
    {
        var body = Write("PartyJoinReply", new Dictionary<string, object?> { ["unknown_4"] = 1 });

        Assert.Equal(4, body.Length);
        Assert.Equal("01000000", Convert.ToHexString(body));
    }

    /// <summary>The structures' field lists are the pinned ones.</summary>
    [Fact]
    public void TheFieldListsArePinned()
    {
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "PartyRecruitingRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["kind"],
            Tables.Resolve("kr-812", "PartyQuickStartRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["kind"],
            Tables.Resolve("kr-812", "PartyQuickStartReply").Select(field => field.Name).ToArray());
        Assert.Equal(["text"],
            Tables.Resolve("kr-812", "PartyChatRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["user_number", "unknown_8", "text", "colour", "system"],
            Tables.Resolve("kr-812", "PartyChatMessage").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "PartyJoinReply").Select(field => field.Name).ToArray());
    }

    private static byte[] Write(string structure, IReadOnlyDictionary<string, object?> values) =>
        PacketCodec.Write(Tables, "kr-812", structure, values);
}
