using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the table-driven solo party snapshot (<c>gspUpdateParty</c>) for the three modern builds against the
/// bytes the hand-written login writer produced before the layout moved into <c>packets/party.yaml</c>. The
/// test reads the same golden file as the modern lobby pins but touches only the shared dialect writer, so it
/// outlives the modern plugin's own encoders.
/// </summary>
public sealed class LoginPartySnapshotTests
{
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

    private static readonly Lazy<PacketTables> Tables = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return PacketTables.Load(candidate);
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    });

    public static TheoryData<string> Builds => new() { "kr-1256", "cn-774", "tw-454" };

    /// <summary>The solo snapshot reproduces the golden body byte for byte in every modern build's layout.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void WriteMatchesTheGoldenSoloPartyBody(string build)
    {
        var expected = Golden.Value[build[..2] + ".updateparty"];
        var actual = Convert.ToHexString(LoginPartySnapshot.Write(Tables.Value, build, 1001, "nick", 300012));
        Assert.Equal(expected, actual);
    }
}
