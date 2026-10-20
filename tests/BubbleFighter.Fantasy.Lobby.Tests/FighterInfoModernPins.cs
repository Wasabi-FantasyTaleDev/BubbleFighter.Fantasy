using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Pins the shared binding's two fighter-info packet bodies after slice S4b moved them off the modern writers:
/// S2C <c>gspRepFighterInfo</c> (133; parser kr-1256 sub_99A430, handler kr-1256 sub_9BCE60, cn-774 sub_BBA7A0)
/// and S2C <c>gspUserStat</c> (105; parser kr-1256 sub_9D8030, handler kr-1256 sub_9A5C40). The expected bodies are
/// the existing <c>Golden/modern-lobby.txt</c> keys, so the move changes the writers and not the bytes.
/// </summary>
public sealed class FighterInfoModernPins
{
    private const ushort ColorIndex = 3;
    private const int Lucci = 1234;
    private const int Cash = 5678;

    // The golden sample: identity 1001, account "user", nickname "nick", level 10.
    private static readonly PlayerIdentity Player = new(1001, "user", "nick", 10);

    private static readonly Lazy<Dictionary<string, string>> Golden = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "BubbleFighter.Fantasy.Lobby.Tests", "Golden", "modern-lobby.txt");
            if (File.Exists(candidate))
                return File.ReadAllLines(candidate).Where(line => line.Length > 0)
                    .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        }
        throw new InvalidOperationException("Golden/modern-lobby.txt was not found above the test output folder.");
    });

    private static FighterInfoBinding Binding(string build)
    {
        var profile = new ClientProfile(build, build[..2], build == "kr-1256" ? 1256 : 774, 0, 0, build);
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        return new FighterInfoBinding(build + "/game", table, WireLengthSupport.Tables, build);
    }

    private static byte[] Found(string build) => Binding(build).Write(new FighterInfoReplied(Player));

    private static byte[] NotFound(string build) => Binding(build).Write(new FighterInfoReplied(null));

    private static void Pin(string key, byte[] body) => Assert.Equal(Golden.Value[key], Convert.ToHexString(body));

    /// <summary>
    /// Found: result 1, the player's FighterInfo, the constructor-default partner, two empty strings and four words
    /// (kr-1256 sub_99A430; cn-774 sub_BBA7A0 reads the same sequence). The reader's length is 924 for kr-1256 and
    /// 970 for cn-774.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 924)]
    [InlineData("cn-774", 970)]
    public void FoundReplyPinsTheGoldenBody(string build, int length)
    {
        var body = Found(build);
        Assert.Equal(length, body.Length);
        Pin($"{build[..2]}.repfighter", body);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body)); // result: sub_9BCE60 builds the dialog
        Assert.Equal(Player.UserNum, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4))); // FighterInfo +4
        Assert.Equal(Player.Nickname.Length, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8))); // +8 wstring
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20))); // +32 Dao character type
        Assert.Equal(Player.Level, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(24))); // +36 level
    }

    /// <summary>
    /// Not found: result 0 and two constructor-default blocks, because the reader consumes the whole body whatever
    /// the result (kr-1256 sub_9BCE60 takes its notice branch; cn-774 sub_BBA7A0 tests <c>v21 == 1</c>).
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 908)]
    [InlineData("cn-774", 954)]
    public void NotFoundReplyPinsTheGoldenBody(string build, int length)
    {
        var body = NotFound(build);
        Assert.Equal(length, body.Length);
        Pin($"{build[..2]}.repfighternotfound", body);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body)); // result: the notice branch
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4))); // first block identity
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8))); // empty nickname
    }

    /// <summary>
    /// S2C 105 with the golden sample: color 3, lucci 1234, cash 5678 (kr-1256 sub_9A5C40 stores them at user
    /// state +76/+80) and the nested FighterInfo the modern family reads (identity, character type, level, name
    /// at +504). 494 bytes for kr-1256; 521 for cn-774, whose block is 463 bytes.
    /// </summary>
    [Theory]
    [InlineData("kr-1256", 494)]
    [InlineData("cn-774", 521)]
    public void UserStatPinsTheGoldenBody(string build, int length)
    {
        var body = UserStatWire.Write(WireLengthSupport.Tables, build, Player.Nickname, Player.Level, Lucci, Cash,
            ColorIndex, Player.UserNum);
        Assert.Equal(length, body.Length);
        Pin($"{build[..2]}.userstat", body);
        Assert.Equal(ColorIndex, BinaryPrimitives.ReadInt32LittleEndian(body)); // color_index
        Assert.Equal(Lucci, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(Cash, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
    }

    /// <summary>
    /// A +304 element carries the reader's stack defaults beside its first word (kr-1256 sub_115B060): 1000,
    /// -1, 100.0f and 0 after the item id, twenty bytes per element after the vector count. The golden bodies above
    /// send the vector empty, so this pins the element itself.
    /// </summary>
    [Fact]
    public void Entry304CarriesTheReaderDefaults()
    {
        var empty = PacketCodec.Write(WireLengthSupport.Tables, "kr-1256", "FighterInfo", new Dictionary<string, object?>(StringComparer.Ordinal));
        var withEntry = PacketCodec.Write(WireLengthSupport.Tables, "kr-1256", "FighterInfo", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unknown_304"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["unknown_0"] = 300001 } },
        });
        Assert.Equal(empty.Length + 20, withEntry.Length);
        Assert.Contains("01000000" + "E1930400" + "E8030000" + "FFFFFFFF" + "0000C842" + "00000000", Convert.ToHexString(withEntry), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
