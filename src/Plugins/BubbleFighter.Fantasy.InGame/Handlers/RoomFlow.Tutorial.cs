using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal async ValueTask HandleInitialTutorialAsync(ISession session, InitialTutorialRequested request,
        IMessageSender<InitialTutorialEntered> sender, CancellationToken ct)
    {
        if (!SessionPlayer.IsPastLoginBurst(session))
            throw new InvalidOperationException("Initial tutorial admission requires the completed login burst.");
        if (!session.TryFeature<IPlayerSession>(out var player) || player.Phase != SessionPhase.MainMenu)
            throw new InvalidOperationException("Initial tutorial admission requires the main menu.");
        if (_registry.TryGet(session.Id) is not null)
            throw new InvalidOperationException("Initial tutorial admission requires an unseated player.");
        if (request.Team == 0)
            throw new InvalidOperationException("Initial tutorial admission requires an assigned player team.");
        var (user, _) = SessionPlayer.RequireIdentity(session);
        var equipment = await SeatCharacter.ResolveEquipmentAsync(session, user, _selection, _characters, ct).ConfigureAwait(false);
        var tutorials = new TutorialRoomFlow(_registry);
        var admission = new TutorialRoundOpened(request.ModeId, request.MapKey, equipment.CharacterId,
            equipment.ItemIds ?? [equipment.CharacterId], request.Team, _options?.StartTimerMs ?? 0, request.TimeLimitMs, true)
        {
            GameConnectionOnly = true,
        };
        await tutorials.OpenAsync(session, admission, ct).ConfigureAwait(false);
        try
        {
            var room = _registry.TryGet(session.Id) ?? throw new InvalidOperationException("Tutorial admission did not create a room.");
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                ScenePhase.Entered(session, SessionPhase.Room, _logger);
                await sender.SendAsync(session, new InitialTutorialEntered(View(room), request.NativeRequest), operationCt).ConfigureAwait(false);
                ScenePhase.Entered(session, SessionPhase.Match, _logger);
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await tutorials.CloseAsync(session, new TutorialRoundClosed(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask CloseTutorialForSceneAsync(ISession session, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { Tutorial: not null })
        {
            if (session.TryFeature<IPlayerSession>(out var player) && player.Phase == SessionPhase.Match)
                throw new InvalidOperationException("An ordinary match must leave through its room flow.");
            return;
        }
        await new TutorialRoomFlow(_registry).CloseAsync(session, new TutorialRoundClosed(), ct).ConfigureAwait(false);
        ScenePhase.Entered(session, SessionPhase.Plaza, _logger);
    }

    internal ValueTask HandleInitialTutorialExitAsync(ISession session, InitialTutorialExitRequested request, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { Tutorial.GameConnectionOnly: true })
            throw new InvalidOperationException("Initial tutorial exit requires its active solo room.");
        return HandlePlazaMenuReturnAsync(session, new PlazaMenuReturnRequested(), ct);
    }

    internal async ValueTask HandleInitialTutorialNpcAsync(ISession session, InitialTutorialNpcReported request,
        IMessageSender<InitialTutorialNpcAcknowledged> sender, CancellationToken ct)
    {
        var room = _registry.TryGet(session.Id)
            ?? throw new InvalidOperationException("Tutorial NPC operations require an admitted solo room.");
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (room.Tutorial is not { GameConnectionOnly: true }
                    || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing)
                    || room.Seats.Count != 1 || seat?.Slot != room.MasterSlot)
                    throw new InvalidOperationException("Tutorial NPC operations require the active solo host.");
            }
            await sender.SendAsync(session, new(request.Operation, request.Payload), operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
