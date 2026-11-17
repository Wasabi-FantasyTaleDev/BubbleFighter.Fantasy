using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>
/// The server-wide <see cref="IAccountBuildLock"/>: one lock for every build's login claim, and the account's live
/// sessions on other builds read from the server's session registry. Mounted once per server (bundles/core.yaml),
/// so a server composing several builds keeps each account on one of them.
/// </summary>
[Plugin("account-builds", Category = "cluster", Names = ["en=One build per account", "zh-Hans=账号单版本登录", "zh-Hant=帳號單版本登入", "ko=계정당 한 빌드"],
    Description = "Keeps each account logged in on one client build at a time.",
    Requires = [typeof(ISessionRegistry)], Provides = [typeof(IAccountBuildLock)])]
public sealed class AccountBuildLockPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<IAccountBuildLock>(new AccountBuildLock(context.Services.Get<ISessionRegistry>())));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Serialises every build's login claim and finds the account's live game sessions on the other builds.</summary>
/// <param name="sessions">The server's live sessions.</param>
public sealed class AccountBuildLock(ISessionRegistry sessions) : IAccountBuildLock
{
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public T Claim<T>(int userNumber, string buildId, Func<IReadOnlyList<ISession>, T> claim)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            // A match connection shares its login's player state but not its connection id; the login's own
            // connection is the one that holds the account (IPlayerSession.LoginConnectionId).
            var elsewhere = sessions.All.Where(session => !session.Connection.IsClosed
                && !string.Equals(session.Profile.Id, buildId, StringComparison.Ordinal)
                && session.TryFeature<IPlayerSession>(out var player) && player.Identity.UserNum == userNumber
                && (player.LoginConnectionId is null || player.LoginConnectionId == session.Connection.Id)).ToArray();
            return claim(elsewhere);
        }
    }
}
