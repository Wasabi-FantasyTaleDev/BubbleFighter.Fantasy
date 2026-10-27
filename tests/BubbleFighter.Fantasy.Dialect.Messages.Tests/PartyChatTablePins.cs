using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the party recruiting and chat bodies added to <c>packets/party.yaml</c> from the kr-812 class
/// readers: the recruiting switch is one boolean word (kr-812 sub_80C160), the quick-start request one word
/// (kr-812 sub_80A910), the chat request one string (kr-812 sub_5C8FD0), the chat message a word, two strings,
/// a word and a boolean (kr-812 sub_92AB70), and the join reply one word (kr-812 sub_80A910). A body that
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

    /// <summary>The quick-start request is one word, four bytes.</summary>
    [Fact]
    public void PartyQuickStartRequestIsOneWord()
    {
        var body = Write("PartyQuickStartRequest", new Dictionary<string, object?> { ["unknown_4"] = 4 });

        Assert.Equal(4, body.Length);
        Assert.Equal("04000000", Convert.ToHexString(body));
    }

    /// <summary>The chat request is one string: a 4-byte count and UTF-16 units, eight bytes for "ab".</summary>
    [Fact]
    public void PartyChatRequestCarriesOneString()
    {
        var named = Write("PartyChatRequest", new Dictionary<string, object?> { ["unknown_4"] = "ab" });

        Assert.Equal(8, named.Length);
        Assert.Equal("0200000061006200", Convert.ToHexString(named));

        var empty = Write("PartyChatRequest", new Dictionary<string, object?> { ["unknown_4"] = string.Empty });
        Assert.Equal(4, empty.Length);
    }

    /// <summary>The chat message: word, two strings, word and boolean; twenty bytes with empty strings.</summary>
    [Fact]
    public void PartyChatMessageKeepsTheFieldOrder()
    {
        var body = Write("PartyChatMessage", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1,
            ["unknown_8"] = string.Empty,
            ["unknown_36"] = string.Empty,
            ["unknown_64"] = 2,
            ["unknown_68"] = true,
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
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "PartyQuickStartRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "PartyChatRequest").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4", "unknown_8", "unknown_36", "unknown_64", "unknown_68"],
            Tables.Resolve("kr-812", "PartyChatMessage").Select(field => field.Name).ToArray());
        Assert.Equal(["unknown_4"],
            Tables.Resolve("kr-812", "PartyJoinReply").Select(field => field.Name).ToArray());
    }

    private static byte[] Write(string structure, IReadOnlyDictionary<string, object?> values) =>
        PacketCodec.Write(Tables, "kr-812", structure, values);
}
