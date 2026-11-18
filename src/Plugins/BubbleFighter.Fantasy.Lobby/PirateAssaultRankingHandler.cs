using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>Answers only complete board snapshots from an authoritative source in the lobby's data domain.</summary>
internal static class PirateAssaultRankingHandler
{
    internal static async ValueTask HandleAsync(IOnlinePlayers online, IPirateAssaultRankingSource source,
        IMessageSender<PirateAssaultRankingPage> replies, ISession session, PirateAssaultRankingRequested request,
        CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException("A Pirate Assault ranking query requires an authenticated player.");
        if (!ReferenceEquals(online.FindByUserNum(player.Identity.UserNum), session)) return;
        var key = new PirateAssaultRankingKey(request.Build, request.RankId, request.Personal ? player.Identity.UserNum : null);
        var page = await source.ReadAsync(key, ct).ConfigureAwait(false);
        // No error variant is established. An empty success would put a false result into the client's cache.
        if (page is null) return;
        if (page.Key != key)
            throw new InvalidOperationException("The Pirate Assault source returned another board, build or player's page.");
        if (ReferenceEquals(online.FindByUserNum(player.Identity.UserNum), session)
            && session.TryFeature<IPlayerSession>(out var current) && ReferenceEquals(current, player))
            await replies.SendAsync(session, page, ct).ConfigureAwait(false);
    }
}
