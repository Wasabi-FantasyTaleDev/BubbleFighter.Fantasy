using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;

namespace BubbleFighter.Fantasy.Lobby;

internal sealed class RetainedPlayerAdmission(string protocol, IPlayerRepository players, PlayerSessionScope sessions,
    LoginInventoryResolver inventory, LoginMoneyResolver money) : IRetainedPlayerAdmission
{
    public string Protocol => protocol;

    public async ValueTask<IPlayerSession> InstallAsync(ISession session, long userNum, CancellationToken ct)
    {
        if (userNum is <= 0 or > int.MaxValue || protocol != session.Profile.Id + "/game"
            || session.Connection.IsClosed || session.TryFeature<IPlayerSession>(out _))
            throw new InvalidOperationException("The retained player cannot be installed on this connection.");
        var account = session.Feature<IAccountSession>();
        if (account.UserNum != userNum || !await account.IsHeldAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Retained player loading requires this connection's account authority.");
        var player = await players.FindByUserNumAsync(userNum, ct).ConfigureAwait(false);
        if (player is null || player.UserNum != userNum)
            throw new InvalidOperationException("The retained player does not exist in this data domain.");
        _ = await inventory.ResolveAsync(userNum, ct).ConfigureAwait(false);
        _ = await money.ResolveAsync(userNum, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (session.Connection.IsClosed || !await account.IsHeldAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Account authority was lost during retained player loading.");
        return sessions.Attach(session, new(checked((int)userNum), player.Username, player.Nickname,
            Math.Max(1, player.Level)) { ColorIndex = player.ColorIndex });
    }

    public void Remove(ISession session) => sessions.Remove(session.Connection.Id);
}
