using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal ValueTask HandleTutorialSelectionOpeningAsync(ISession session, TutorialSelectionOpening request, CancellationToken ct) =>
        RunSceneAsync(session, async (_, token) =>
        {
            if (!SessionPlayer.IsPastLoginBurst(session))
                throw new InvalidOperationException("Tutorial selection requires the completed login burst.");
            if (_registry.TryGet(session.Id) is not null)
                throw new InvalidOperationException("Tutorial selection requires an unseated player.");
            await LeavePlazaForRoomAsync(session, token).ConfigureAwait(false);
        }, ct);

    internal async ValueTask HandleInitialTutorialAsync(ISession session, InitialTutorialRequested request,
        IMessageSender<InitialTutorialEntered> sender, Lazy<StageMainTutorialTable?> numberedStages, CancellationToken ct)
    {
        if (!SessionPlayer.IsPastLoginBurst(session))
            throw new InvalidOperationException("Initial tutorial admission requires the completed login burst.");
        if (!session.TryFeature<IPlayerSession>(out var player) || player.Phase != SessionPhase.MainMenu)
            throw new InvalidOperationException("Initial tutorial admission requires the main menu.");
        if (_registry.TryGet(session.Id) is not null)
            throw new InvalidOperationException("Initial tutorial admission requires an unseated player.");
        if (request.ModeId == (int)GameModeId.SinglePlay)
        {
            // A missing map table, spoofed directory, wrong clock or nonselectable row cannot create a room.
            if (numberedStages.Value?.Accepts(request) != true)
            {
                _logger.LogWarning("Session {Id} tutorial selection did not match a numbered stage in the client's map table.", session.Id);
                return;
            }
        }
        else if (request.Team == 0)
            throw new InvalidOperationException("Initial tutorial admission requires an assigned player team.");
        var (user, _) = SessionPlayer.RequireIdentity(session);
        var equipment = await ResolveEquipmentAsync(session, user, ct).ConfigureAwait(false);
        var tutorials = new TutorialRoomFlow(_registry);
        var admission = new TutorialRoundOpened(request.ModeId, request.MapKey, equipment.CharacterId,
            equipment.ItemIds ?? [equipment.CharacterId], request.Team, _options?.StartTimerMs ?? 0, request.TimeLimitMs, true)
        {
            GameConnectionOnly = true,
            ItemAttributes = equipment.ItemAttributes ?? [],
        };
        await tutorials.OpenAsync(session, admission, ct).ConfigureAwait(false);
        try
        {
            var room = _registry.TryGet(session.Id) ?? throw new InvalidOperationException("Tutorial admission did not create a room.");
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                ScenePhase.Entered(session, SessionPhase.Room, _logger);
                await sender.SendAsync(session, new InitialTutorialEntered(View(room), request.NativeRequest)
                {
                    StageDirectory = request.StageDirectory,
                    Difficulty = request.Difficulty,
                }, operationCt).ConfigureAwait(false);
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

    internal ValueTask HandleSoloStageResultAsync(ISession session, RoundStageResultReported result,
        TutorialRoomFlow numbered, CancellationToken ct)
    {
        // The numbered stage's own goal emits 1; time-out and the confirmed quit control emit 0.
        // Other modes reuse the wire word differently. Only this player's active numbered round can earn a clear.
        if (result.Outcome != 1 || _registry.TryGet(session.Id) is not { Tutorial.GameConnectionOnly: true } room
            || room.ModeId != (int)GameModeId.SinglePlay)
            return HandleInitialTutorialExitAsync(session, new(), ct);
        return HandlePlazaMenuReturnAsync(session, new(), token => new ValueTask(room.Executor.ExecuteAsync(async roundToken =>
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.Seats.Count != 1
                    || room.FindByGame(session.Id)?.Slot != room.MasterSlot) return;
            }
            await numbered.ClearedAsync(session, new(room.MapKey), roundToken).ConfigureAwait(false);
        }, token)), ct);
    }

    internal ValueTask HandleInitialTutorialNpcAsync(ISession session, InitialTutorialNpcReported request,
        IMessageSender<InitialTutorialNpcAcknowledged> sender, CancellationToken ct) =>
        AnswerTutorialHostAsync(session, operationCt =>
            sender.SendAsync(session, new(request.Operation, request.Payload), operationCt), ct);

    /// <summary>Whether the session plays the solo initial tutorial, whose NPC operations only its host sees.</summary>
    internal bool PlaysSoloTutorial(ISession session) =>
        _registry.TryGet(session.Id) is { Tutorial.GameConnectionOnly: true };

    /// <summary>Answers the solo tutorial host's ordinary wave and spawn operations, which only it applies.</summary>
    internal ValueTask HandleTutorialNpcAsync(ISession session, RoundNpcReported request,
        IMessageSender<RoundNpcAcknowledged> sender, CancellationToken ct) =>
        AnswerTutorialHostAsync(session, operationCt =>
            sender.SendAsync(session, new(request.Operation, request.Payload), operationCt), ct);

    private async ValueTask AnswerTutorialHostAsync(ISession session, Func<CancellationToken, ValueTask> answer, CancellationToken ct)
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
            await answer(operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
