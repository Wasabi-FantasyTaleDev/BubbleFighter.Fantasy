using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Launcher.Http;

internal sealed class LauncherPresence(LauncherOptions options, ISessionRegistry? sessions, IMatchConnectionTracker? matches) : ILocalLauncherPresence
{
    private readonly HashSet<string> _builds = options.Instances.Select(i => i.Build).ToHashSet(StringComparer.Ordinal);

    private sealed record Observation(PlayerIdentity Identity, string BuildId, bool Migrating);

    private Observation[] LocalPlayers(bool allBuilds = false)
    {
        if (sessions is null) throw new LauncherApiFailure(503, "player_state_unavailable");
        var players = sessions.All
            .Where(s => !s.Connection.IsClosed && (allBuilds || _builds.Count == 0 || _builds.Contains(s.Profile.Id)) && matches?.IsMatchBound(s) != true)
            .OrderByDescending(s => s.Id)
            .Select(s => s.TryFeature<IPlayerSession>(out var player)
                && (player.LoginConnectionId == s.Connection.Id || (player.LoginConnectionId is null && matches is not null))
                    ? new Observation(player.Identity, s.Profile.Id, player.Phase == SessionPhase.Migrating) : null)
            .OfType<Observation>().DistinctBy(p => p.Identity.UserNum)
            .Take(options.MaxPresencePlayers + 1).ToArray();
        if (players.Length > options.MaxPresencePlayers)
            throw new InvalidOperationException("Authenticated presence exceeds max_presence_players.");
        return players.OrderBy(p => p.Identity.Nickname, StringComparer.Ordinal).ThenBy(p => p.Identity.UserNum).ToArray();
    }

    internal PlayerIdentity[] Snapshot() => LocalPlayers().Select(p => p.Identity).ToArray();

    public IReadOnlyList<LauncherPresenceMember> Capture(bool includeRoster) => CaptureMembers(includeRoster, allBuilds: true);

    internal IReadOnlyList<LauncherPresenceMember> LocalRoster() => CaptureMembers(includeRoster: true, allBuilds: false);

    private IReadOnlyList<LauncherPresenceMember> CaptureMembers(bool includeRoster, bool allBuilds)
    {
        var disclose = includeRoster && options.RosterEnabled;
        return Array.AsReadOnly(LocalPlayers(allBuilds).Select(p => new LauncherPresenceMember(p.Identity.UserNum,
            p.BuildId, disclose ? p.Identity.Nickname : null, disclose ? p.Identity.Level : null, p.Migrating)).ToArray());
    }

    internal object Presentation(LauncherPresenceSnapshot? aggregate = null)
    {
        int? online = !options.ShowOnlinePlayers || sessions is null && aggregate is null ? null
            : aggregate?.OnlinePlayers ?? Snapshot().Length;
        var result = new Dictionary<string, object?>
        {
            ["motd"] = options.Motd, ["iconUrl"] = options.IconUrl.Length == 0 ? null : options.IconUrl,
            ["images"] = options.Images, ["onlinePlayers"] = options.ShowOnlinePlayers ? online : -1,
            ["state"] = options.Maintenance ? "maintenance" : "online", ["playerLimit"] = options.PlayerLimit,
            ["whitelist"] = options.WhitelistEnabled, ["rosterEnabled"] = options.RosterEnabled,
        };
        if (aggregate is not null && options.ShowOnlinePlayers)
        {
            result["presenceGroup"] = options.PresenceGroup;
            result["presenceComplete"] = aggregate.Complete;
            if (aggregate.PeakOnlinePlayers is { } peak) result["peakOnlinePlayers"] = peak;
        }
        return result;
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
