using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the clan-server connection's handshake bodies through <c>packets/clan-server.yaml</c>, the passport the
/// login echoes, the four clan-connection opcode numbers, and the naming rule of that connection's rows.
/// </summary>
public sealed class ClanServerTableTests
{
    private static PacketTables Tables => TestBuilds.Packets;

    /// <summary>
    /// The login as kr-1256 sub_525DF0 writes it: a word, a NUL-terminated wide string, two words, a byte count
    /// and the bytes. 4 + (2 * 2 + 2) + 4 + 4 + 4 + 3 = 25 bytes.
    /// </summary>
    [Fact]
    public void LoginReadsTheEchoedPassport()
    {
        var body = Convert.FromHexString("2A000000" + "480069000000" + "01000000" + "01000000" + "03000000" + "AABBCC");
        Assert.Equal(25, body.Length);

        var login = ClanServerBinding.ReadLogin(Tables, "kr-1256", body);

        Assert.Equal(42, login.UserNumber);
        Assert.Equal("Hi", login.Nickname);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, login.Ticket.ToArray());
    }

    /// <summary>A login whose string has no terminator, or with bytes after the ticket, is a format error.</summary>
    [Theory]
    [InlineData("2A0000004800690001000000")]
    [InlineData("2A0000000000000000000000000000000000FF")]
    public void MalformedLoginIsRejected(string hex) =>
        Assert.Throws<PacketFormatException>(() => ClanServerBinding.ReadLogin(Tables, "kr-1256", Convert.FromHexString(hex)));

    /// <summary>The login result is the one word kr-1256 sub_525170 case 0x3EE switches on: 1, -1 or -2.</summary>
    [Theory]
    [InlineData(ClanServerLoginResult.Accepted, "01000000")]
    [InlineData(ClanServerLoginResult.NotAuthenticated, "FFFFFFFF")]
    [InlineData(ClanServerLoginResult.Failed, "FEFFFFFF")]
    public void LoginResultIsOneWord(ClanServerLoginResult result, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(ClanServerBinding.WriteLoginResult(Tables, "kr-1256", result)));

    /// <summary>
    /// The passport carries what the login must echo, in the reader's order (kr-1256 sub_99C1D0): the account id,
    /// the counted nickname, two zero words, the counted ticket. 4 + (4 + 4) + 4 + 4 + (4 + 2) = 26 bytes.
    /// </summary>
    [Fact]
    public void PassportStatesTheAccountAndTheTicket()
    {
        var body = ClanBinding.WritePassport(Tables, "kr-1256", new ClanPassportIssued(42, "Hi", new byte[] { 0xAA, 0xBB }));

        Assert.Equal("2A000000" + "02000000" + "48006900" + "00000000" + "00000000" + "02000000" + "AABB", Convert.ToHexString(body));
    }

    /// <summary>A wcstr field writes UTF-16LE and one NUL word, and refuses a value the terminator would cut short.</summary>
    [Fact]
    public void TerminatedStringsWriteNoCount()
    {
        var values = new Dictionary<string, object?> { ["user_number"] = 1, ["nickname"] = "A", ["ticket"] = Array.Empty<object?>() };
        Assert.Equal("01000000" + "41000000" + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(PacketCodec.Write(Tables, "kr-1256", "ClanServerLogin", values)));

        values["nickname"] = "A\0B";
        Assert.Throws<ArgumentException>(() => PacketCodec.Write(Tables, "kr-1256", "ClanServerLogin", values));
    }

    /// <summary>The handshake numbers of the clan connection, which is a space of its own beside game and relay.</summary>
    [Fact]
    public void ClanConnectionOpcodesAreTheClientNumbers()
    {
        var clan = TestBuilds.Opcodes("kr-1256").ForConnection(OpcodeConnection.Clan);

        Assert.Equal(1002, (int)clan.S2C("ClanHello"));
        Assert.Equal(1003, (int)clan.C2S("ClanHeartbeat"));
        Assert.Equal(1005, (int)clan.C2S("ClanLogin"));
        Assert.Equal(1006, (int)clan.S2C("ClanLoginResult"));
        Assert.Null(TestBuilds.Opcodes("kr-1256").Find(PacketDirection.ClientToServer, "ClanLogin"));
    }

    /// <summary>Clan-connection rows are plain coined names; the game connection keeps the trailing underscore rule.</summary>
    [Theory]
    [InlineData("clan", "ClanLogin", "coined", true)]
    [InlineData("clan", "Unknown1099", "none", true)]
    [InlineData("clan", "ClanLogin_", "coined", false)]
    [InlineData("clan", "ClanLogin", "label@kr-812", false)]
    [InlineData("game", "ClanLogin", "coined", false)]
    [InlineData("game", "gcpReqThing_", "coined", true)]
    public void ClanConnectionNamesArePlainCoinedNames(string connection, string name, string source, bool accepted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"clan-opcodes-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path,
            $"profile: kr-1256\nopcodes:\n  - value: 1005\n    direction: c2s\n    connection: {connection}\n    name: {name}\n    name_source: {source}\n    evidence: verified\n");
        try
        {
            var profile = new ClientProfile("kr-1256", "kr", 1256, 0, 0, "test");
            if (accepted)
                Assert.Equal(1005, (int)OpcodeTableLoader.Load(path, profile).ForConnection(
                    connection == "clan" ? OpcodeConnection.Clan : OpcodeConnection.Game).C2S(name));
            else
                Assert.Throws<ConfigException>(() => OpcodeTableLoader.Load(path, profile));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
