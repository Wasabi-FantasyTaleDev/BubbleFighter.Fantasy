using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the portal move bodies of <c>packets/portal.yaml</c>: the kr-812 writer and reader orders and the
/// kr-1256 builder and handler orders, with a byte-length pin per structure. Nothing here binds or sends a
/// frame.
/// </summary>
public sealed class PortalMoveTablePins
{
    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    /// <summary>The game request: three four-byte words then a two-byte word, 14 bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void PortalMoveRequestIsThreeWordsAndAShortWord(string build)
    {
        var body = PacketCodec.Write(Tables.Value, build, "PortalMoveRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11, ["unknown_8"] = 0x22, ["unknown_12"] = 0x33, ["unknown_16"] = 0x44,
        });
        Assert.Equal(14, body.Length);
        Assert.Equal(new byte[]
        {
            0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x33, 0x00, 0x00, 0x00, 0x44, 0x00,
        }, body);
    }

    /// <summary>The game reply: a four-byte word, a two-byte word and a four-byte word, 10 bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void PortalMoveReplyIsWordShortWordWord(string build)
    {
        var body = PacketCodec.Write(Tables.Value, build, "PortalMoveReply", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11, ["unknown_8"] = 0x22, ["unknown_12"] = 0x33,
        });
        Assert.Equal(10, body.Length);
        Assert.Equal(new byte[]
        {
            0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x33, 0x00, 0x00, 0x00,
        }, body);
    }

    /// <summary>The relay companion: a four-byte word then a two-byte word, 6 bytes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void PortalMoveRelayIsWordShortWord(string build)
    {
        var body = PacketCodec.Write(Tables.Value, build, "PortalMoveRelay", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x55, ["unknown_8"] = 0x66,
        });
        Assert.Equal(6, body.Length);
        Assert.Equal(new byte[] { 0x55, 0x00, 0x00, 0x00, 0x66, 0x00 }, body);
    }

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
