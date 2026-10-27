using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the table-driven main-menu <c>gspGotoRoom</c> body of the three modern builds to
/// <c>Golden/modern-lobby.txt</c> for userNumber 1001, nickname "nick", level 10 and character item 300012.
/// The kr-1256 fixture retains the constructor defaults of kr-1256 sub_113C3E0 / kr-1256 sub_113BFA0.
/// Readers: kr-1256 sub_9AC490 / kr-1256 sub_10D4520, cn-774 sub_BAB9F0 and tw-454 sub_A89100. This test uses only
/// the packet tables and the dialect writer, so it outlives the lobby classes the writer replaced.
/// </summary>
public sealed class MainMenuInstallTests
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

    /// <summary>Each build writes exactly the main-menu bytes its golden line pins.</summary>
    /// <param name="build">The client build whose table layout is written.</param>
    [Theory]
    [MemberData(nameof(Builds))]
    public void TheMainMenuBodyMatchesTheGoldenBytes(string build)
    {
        var body = MainMenuInstall.Write(Tables.Value, build, 1001, "nick", 10, 300012);
        Assert.Equal(Golden.Value[$"{build[..2]}.gotoroomref"], Convert.ToHexString(body));
    }

    /// <summary>A solo login never sets the native switch that announces a party-led stage move.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void SoloLoginClearsPartyTransition(string build)
    {
        var body = MainMenuInstall.Write(Tables.Value, build, 1001, "nick", 10, 300012);
        var fields = PacketCodec.Read(Tables.Value, build, "gspGotoRoom", body);
        Assert.False(Assert.IsType<bool>(fields["party_transition"]));
    }
}
