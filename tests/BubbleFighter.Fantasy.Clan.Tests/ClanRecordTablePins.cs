using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Pins the game-port clan record bodies added to <c>packets/clan.yaml</c>: the kr-812 reader order and the
/// kr-1256 handler order, with a byte-length pin per structure. Nothing here mounts or writes a frame.
/// </summary>
public sealed class ClanRecordTablePins
{
    /// <summary>Both inspected clients read three words followed by a four-byte boolean.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ClanPointAddIsThreeWordsAndABooleanWord(string build)
    {
        var body = PacketCodec.Write(Tables(), build, "ClanPointAdd", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1, ["unknown_8"] = 2, ["unknown_12"] = 3, ["unknown_16"] = true,
        });
        Assert.Equal(16, body.Length);
        Assert.Equal(new byte[]
        {
            0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        }, body);
    }

    /// <summary>The modern update adds a third word to the legacy two-word body.</summary>
    [Fact]
    public void ClanUpdateInfoIsTwoWordsOnKr812AndThreeOnKr1256()
    {
        var legacy = PacketCodec.Write(Tables(), "kr-812", "ClanUpdateInfo", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11, ["unknown_8"] = 0x22,
        });
        Assert.Equal(8, legacy.Length);
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00 }, legacy);

        var modern = PacketCodec.Write(Tables(), "kr-1256", "ClanUpdateInfo", new Dictionary<string, object?>
        {
            ["unknown_0"] = 0x11, ["unknown_4"] = 0x22, ["unknown_8"] = 0x33,
        });
        Assert.Equal(12, modern.Length);
        Assert.Equal(new byte[]
        {
            0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x33, 0x00, 0x00, 0x00,
        }, modern);
    }

    /// <summary>Both inspected mark notifications preserve their two-word wire order.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ClanMarkChangedNotyIsTwoWords(string build)
    {
        var body = PacketCodec.Write(Tables(), build, "ClanMarkChangedNoty", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11, ["unknown_8"] = 0x22,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00 }, body);
    }

    /// <summary>The legacy mark request contains exactly three words.</summary>
    [Fact]
    public void ClanMarkChangeRequestIsThreeWords()
    {
        var body = PacketCodec.Write(Tables(), "kr-812", "ClanMarkChangeRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 0x11, ["unknown_8"] = 0x22, ["unknown_12"] = 0x33,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal(new byte[]
        {
            0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x33, 0x00, 0x00, 0x00,
        }, body);
    }

    /// <summary>The clan-info request preserves its word, counted string and trailing word.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void ClanInfoRequestIsWordStringWord(string build)
    {
        var empty = PacketCodec.Write(Tables(), build, "ClanInfoRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 7, ["unknown_8"] = "", ["unknown_36"] = 9,
        });
        Assert.Equal(12, empty.Length);
        Assert.Equal(new byte[]
        {
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00,
        }, empty);

        var named = PacketCodec.Write(Tables(), build, "ClanInfoRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 7, ["unknown_8"] = "ab", ["unknown_36"] = 9,
        });
        Assert.Equal(16, named.Length);
        Assert.Equal(new byte[]
        {
            0x07, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x61, 0x00, 0x62, 0x00, 0x09, 0x00, 0x00, 0x00,
        }, named);
        var values = PacketCodec.ReadScalars(Tables(), build, "ClanInfoRequest", named);
        Assert.Equal(7, Assert.IsType<int>(values["unknown_4"]));
        Assert.Equal("ab", Assert.IsType<string>(values["unknown_8"]));
        Assert.Equal(9, Assert.IsType<int>(values["unknown_36"]));
    }

    private static PacketTables Tables() => PacketTables.Load(Path.Combine(RepoRoot(), "packets"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("The test does not run under the repository.");
    }
}
