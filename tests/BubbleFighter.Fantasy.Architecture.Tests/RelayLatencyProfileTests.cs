using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>Checks that passive delay adapters stay within the explicitly audited, disabled profile rows.</summary>
public sealed class RelayLatencyProfileTests
{
    /// <summary>Composes every client profile and requires both disabled relay rows only in the five admitted profiles.</summary>
    [Fact]
    public void OnlyTheFiveAuditedProfilesComposeBothDisabledRows()
    {
        var profiles = Path.Combine(RepositoryRoot(), "profiles");
        var composer = new ProfileComposer(profiles, _ => null, unsetReadsEmpty: true);
        var audited = new HashSet<string>(["kr-1256", "cn-738", "cn-742", "cn-774", "tw-454"], StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in composer.ListProfiles().Where(profile => profile.IsClientBuild))
        {
            var rows = composer.Compose(profile.Id, [])
                .Where(row => row.Plugin is "dialect.relay-latency" or "match-latency").ToArray();
            if (!audited.Contains(profile.Id))
            {
                Assert.Empty(rows);
                continue;
            }
            found.Add(profile.Id);
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.True(row.Disabled);
                // A disabled row keeps its placeholders unresolved; the protocol must still name this profile's relay.
                var protocol = Assert.IsType<string>(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(row.Config)["protocol"]);
                Assert.Equal(profile.Id + "/relay", protocol.Replace("${BFF_PROFILE_ID}", profile.Id, StringComparison.Ordinal));
            });
            Assert.Single(rows, row => row.Plugin == "dialect.relay-latency");
            var logic = Assert.Single(rows, row => row.Plugin == "match-latency");
            Assert.Equal("0", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(logic.Config)["reply_word"]?.ToString());
        }
        Assert.True(audited.SetEquals(found));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("The test does not run under the repository.");
    }
}
