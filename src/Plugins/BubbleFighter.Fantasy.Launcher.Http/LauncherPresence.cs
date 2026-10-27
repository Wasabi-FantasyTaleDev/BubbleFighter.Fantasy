using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Launcher.Http;

internal sealed class LauncherPresence(LauncherOptions options, ISessionRegistry? sessions, IMatchConnectionTracker? matches)
{
    private readonly HashSet<string> _builds = options.Instances.Select(i => i.Build).ToHashSet(StringComparer.Ordinal);

    internal PlayerIdentity[] Snapshot()
    {
        if (sessions is null) throw new LauncherApiFailure(503, "player_state_unavailable");
        var players = sessions.All
            .Where(s => !s.Connection.IsClosed && (_builds.Count == 0 || _builds.Contains(s.Profile.Id)) && matches?.IsMatchBound(s) != true)
            .OrderByDescending(s => s.Id)
            // A relay shares player state, not login ownership. Reading that identity avoids a ticket/lobby/match startup cycle.
            .Select(s => s.TryFeature<IPlayerSession>(out var player)
                && (player.LoginConnectionId == s.Connection.Id || (player.LoginConnectionId is null && matches is not null))
                    ? player.Identity : null)
            .OfType<PlayerIdentity>()
            .DistinctBy(p => p.UserNum)
            .OrderBy(p => p.Nickname, StringComparer.Ordinal).ThenBy(p => p.UserNum).ToArray();
        return players;
    }

    internal object Presentation()
    {
        int? online = !options.ShowOnlinePlayers || sessions is null ? null : Snapshot().Length;
        return new
        {
            options.Motd, iconUrl = options.IconUrl.Length == 0 ? null : options.IconUrl, options.Images,
            onlinePlayers = options.ShowOnlinePlayers ? online : -1,
            state = options.Maintenance ? "maintenance" : "online", options.PlayerLimit,
            whitelist = options.WhitelistEnabled, options.RosterEnabled,
        };
    }

    internal void CheckAdmission(string username)
    {
        if (options.Maintenance) throw new LauncherApiFailure(503, "server_maintenance");
        if (options.WhitelistEnabled && !options.Whitelist.Contains(username, StringComparer.OrdinalIgnoreCase))
            throw new LauncherApiFailure(403, "not_whitelisted");
        if (options.PlayerLimit is { } limit)
        {
            var players = Snapshot();
            if (players.Length >= limit && !players.Any(p => string.Equals(p.Username, username, StringComparison.Ordinal)))
                throw new LauncherApiFailure(503, "server_full");
        }
    }
}

internal sealed class LauncherApiFailure(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}
