using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    // A party member goes where its leader goes: the leader's own return carries it home (MigrationSource).
    private long? CaptureHomeReturn(ISession session)
    {
        if (Migration is null || _options?.ReturnOnLeave == "stay" || _registry.TryGet(session.Id) is not null
            || !session.TryFeature<IPlayerSession>(out var player) || player.MigrationHome is null
            || player.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza)) return null;
        return _parties?.PartyOf(player.Identity.UserNum) is { Members.Count: > 1 } party
            && party.Leader.UserNum != player.Identity.UserNum ? null : player.Generation;
    }

    private async ValueTask ReturnHomeAfterSceneAsync(ISession session, long? generation, CancellationToken ct)
    {
        if (generation is null) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        // Release scene ownership during the idle delay so joining another room can cancel the return.
        await _autoStartDelay(TimeSpan.FromMilliseconds(_options?.ReturnDelayMs ?? 1500), lifetime.Token).ConfigureAwait(false);
        await RunSceneAsync(session, async (_, token) =>
        {
            if (CaptureHomeReturn(session) != generation) return;
            await Migration!.ReturnHomeAsync(session, token).ConfigureAwait(false);
        }, lifetime.Token).ConfigureAwait(false);
    }
}
