using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves an explicitly supported tutorial generation from the packet build data.</summary>
internal sealed record InitialTutorialRoute(int Mode, byte PlayerTeam, string RequestMap, string Map, int Difficulty, int TimeLimitMs)
{
    internal static InitialTutorialRoute Load(PacketTables tables, string lineage, ClientBuild clientBuild, string kind)
    {
        if (kind is not ("classic" or "pirate_prologue" or "ai_battle"))
            throw new ConfigException("initial_tutorial_kind must be classic, pirate_prologue or ai_battle.");
        var clientId = FormattableString.Invariant($"{clientBuild.Region}-{clientBuild.Major}");
        if (!tables.ValuesOf(lineage).TryGetValue("initial_tutorial_routes", out var raw)
            || raw is not Dictionary<string, object?> routes
            || routes.GetValueOrDefault(kind) is not Dictionary<string, object?> route
            || route.GetValueOrDefault("builds") is not string[] builds || !builds.Contains(clientId, StringComparer.Ordinal))
            throw new ConfigException($"Initial tutorial '{kind}' has no implemented route for '{clientBuild}'.");
        string Text(string field) => route.GetValueOrDefault(field) is string { Length: > 0 } value ? value
            : throw new ConfigException($"initial_tutorial_routes.{kind}.{field} requires a value.");
        int Positive(string field) => int.TryParse(Text(field), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new ConfigException($"initial_tutorial_routes.{kind}.{field} requires a positive integer.");
        var mode = Positive("mode");
        if (mode > ushort.MaxValue)
            throw new ConfigException($"initial_tutorial_routes.{kind}.mode exceeds the native mode width.");
        var team = Positive("player_team");
        if (team > byte.MaxValue)
            throw new ConfigException($"initial_tutorial_routes.{kind}.player_team exceeds the native team width.");
        return new(mode, checked((byte)team), Text("request_map"), Text("map"), Positive("difficulty"), Positive("time_limit_ms"));
    }
}
