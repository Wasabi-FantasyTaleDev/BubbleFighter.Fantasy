using BubbleFighter.Fantasy.Dialect.Messages;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Builds' login layouts read from the repository's packets/builds.yaml, as the legacy messages row reads them.</summary>
internal static class TestLogin
{
    /// <summary>The cn-774 layout: kr-812's request plus one wstr, and the LoginResponse reply. cn-774 has no packet tables yet.</summary>
    public static readonly LoginShape Cn774 = new(true, LoginResponse: true, RequestExtraWstr: true);

    /// <summary>Gets one build's layout.</summary>
    public static LoginShape Of(string build) => LoginShape.From(build, TestBuilds.Packets.ValuesOf(build));

    /// <summary>Gets the layout of a profile lineage's nearest packet-table build.</summary>
    public static LoginShape OfLineage(IEnumerable<string> lineage) =>
        Of(TestBuilds.Packets.NearestBuild(lineage) ?? throw new InvalidOperationException("No packet-table build in the lineage."));
}
