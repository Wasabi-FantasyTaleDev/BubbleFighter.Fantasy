using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Pins the clan rows of the wire-ratchet baseline after slice S9 moved the plugin onto typed messages:
/// the raw router registrations, opcode constants and byte codecs are gone, and the one remaining build
/// reference is the row's default protocol name. When a metric changes, re-record the file with
/// <c>BFF_RATCHET_WRITE=1</c> and update this pin in the same commit.
/// </summary>
public sealed class ClanWireRatchetPins
{
    /// <summary>The post-move Clan counts: only the default protocol name is left as a build reference.</summary>
    [Fact]
    public void ClanRatchetBaselineIsPinned()
    {
        var baseline = File.ReadAllLines(Path.Combine(
            RepoRoot(), "tests", "BubbleFighter.Fantasy.Architecture.Tests", "wire-ratchet.txt"));

        Assert.Equal(
            [
                "Clan.build_check 1",
                "Clan.opcode_constant 0",
                "Clan.raw_router 0",
                "Clan.wire_codec 0",
            ],
            baseline.Where(line => line.StartsWith("Clan.", StringComparison.Ordinal)));
    }

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
