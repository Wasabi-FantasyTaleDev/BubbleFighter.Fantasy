using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the play together bodies that are not already in <c>packets/party.yaml</c>: the kr-812 "go to friend"
/// request and the two kr-1256 visit failures, each with its exact byte length.
/// </summary>
public sealed class PlayTogetherTablePins
{
    private const string LegacyBuild = "kr-812";
    private const string ModernBuild = "kr-1256";

    /// <summary>The kr-812 request is one prefixed wide string, read by kr-812 sub_5C8FD0 into object +4.</summary>
    [Fact]
    public void MoveToFriendRequestIsOnePrefixedName()
    {
        var body = Text("Ab");
        Assert.Equal(8, body.Length);
        Assert.Equal(body, PacketCodec.Write(TestTables(), LegacyBuild, "MoveToFriendRequest", Values(("unknown_4", "Ab"))));
        var read = PacketCodec.Read(TestTables(), LegacyBuild, "MoveToFriendRequest", body);
        Assert.Equal("Ab", read["unknown_4"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestTables(), LegacyBuild, "MoveToFriendRequest", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestTables(), LegacyBuild, "MoveToFriendRequest", [.. body, 0]));
    }

    /// <summary>
    /// The kr-1256 visitation failure is a nickname and one word; handler kr-1256 sub_9AA690 reads them in that
    /// order. The word's meaning is unproven, so it keeps its position name.
    /// </summary>
    [Fact]
    public void VisitationFailedIsANicknameAndAWord()
    {
        var body = Combine(Text("Ab"), Word(7));
        Assert.Equal(12, body.Length);
        Assert.Equal(body, PacketCodec.Write(TestTables(), ModernBuild, "VisitationFailed",
            Values(("unknown_0", "Ab"), ("unknown_24", 7))));
        var read = PacketCodec.Read(TestTables(), ModernBuild, "VisitationFailed", body);
        Assert.Equal("Ab", read["unknown_0"]);
        Assert.Equal(7, Convert.ToInt32(read["unknown_24"]));
        Assert.Equal(body, PacketCodec.Write(TestTables(), ModernBuild, "VisitationFailed", read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestTables(), ModernBuild, "VisitationFailed", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestTables(), ModernBuild, "VisitationFailed", [.. body, 0]));
    }

    /// <summary>The kr-1256 move-to-inviter failure has the same two fields, read by kr-1256 sub_9A99F0.</summary>
    [Fact]
    public void MoveToInviterFailedIsANicknameAndAWord()
    {
        var body = Combine(Text("Cd"), Word(3));
        Assert.Equal(12, body.Length);
        Assert.Equal(body, PacketCodec.Write(TestTables(), ModernBuild, "MoveToInviterFailed",
            Values(("unknown_0", "Cd"), ("unknown_24", 3))));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestTables(), ModernBuild, "MoveToInviterFailed", body[..^1]));
    }

    /// <summary>The failure shapes follow the modern family; the friend request follows the legacy family.</summary>
    [Fact]
    public void TheVisitFailuresAreModernOnly()
    {
        Assert.Empty(TestTables().Resolve(LegacyBuild, "VisitationFailed"));
        Assert.Empty(TestTables().Resolve(LegacyBuild, "MoveToInviterFailed"));
        Assert.NotEmpty(TestTables().Resolve(ModernBuild, "VisitationFailed"));
        Assert.NotEmpty(TestTables().Resolve(ModernBuild, "MoveToInviterFailed"));
        Assert.NotEmpty(TestTables().Resolve(LegacyBuild, "MoveToFriendRequest"));
        Assert.NotEmpty(TestTables().Resolve("cn-717", "MoveToFriendRequest"));
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            Assert.Empty(TestTables().Resolve(build, "MoveToFriendRequest"));
            Assert.NotEmpty(TestTables().Resolve(build, "VisitationFailed"));
            Assert.NotEmpty(TestTables().Resolve(build, "MoveToInviterFailed"));
        }
    }

    private static PacketTables TestTables() => TestBuilds.Packets;

    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Name, field => field.Value);

    private static byte[] Word(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Text(string text) => Combine(Word(text.Length), Encoding.Unicode.GetBytes(text));

    private static byte[] Combine(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
