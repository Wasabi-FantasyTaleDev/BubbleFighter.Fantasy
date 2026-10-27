using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>Owns unlisted tutorial admission without ordinary room publication or automatic start.</summary>
internal sealed class TutorialRoomFlow(RoomRegistry registry, IReadOnlyDictionary<string, int>? stageNumbers = null, ITutorialStageStore? stages = null,
    ILogger? logger = null, IReadOnlyDictionary<string, int>? gifts = null, IShopGiftService? giftService = null)
{
    internal async ValueTask OpenAsync(ISession session, TutorialRoundOpened request, CancellationToken ct)
    {
        if (request.ModeId is < 1 or > ushort.MaxValue || string.IsNullOrEmpty(request.MapKey)
            || request.CharacterItemId <= 0 || request.CountdownMs < 0)
            throw new InvalidOperationException("Tutorial admission requires a native mode, map, character and nonnegative countdown.");
        await CloseAsync(session, new TutorialRoundClosed(), ct).ConfigureAwait(false);
        var (user, nickname) = SessionPlayer.RequireIdentity(session);
        var (room, seat) = registry.CreateForMode(session, checked((ushort)request.ModeId), request.MapKey,
            string.Empty, user, nickname, listed: false);
        lock (room.Gate)
        {
            room.Tutorial = request with { EquippedItemIds = request.EquippedItemIds.ToArray() };
            room.TutorialCancellation = new CancellationTokenSource();
            seat.CharacterId = request.CharacterItemId;
            seat.EquippedItemIds = request.EquippedItemIds.ToArray();
            seat.Team = request.Team;
            room.MarkStarting();
            if (request.GameConnectionOnly)
                seat.GameLinkGeneration = room.StartGeneration;
        }
    }

    /// <summary>
    /// Records a cleared tutorial-room stage under its MapMeta stageNum with the time since the round started, so
    /// the room can mark it (kr-812 sub_6B50E0) and show the time (kr-812 sub_6B4110). The first clear also grants the
    /// stage's MapMeta IsItemAward through the shop when gifts are configured. A map that is not a numbered stage, or
    /// a build without client data or stage storage, records nothing.
    /// </summary>
    internal async ValueTask ClearedAsync(ISession session, TutorialStageCleared request, CancellationToken ct)
    {
        if (stageNumbers is null || stages is null || registry.TryGet(session.Id) is not { } room) return;
        if (!stageNumbers.TryGetValue(request.MapKey, out var stage)) return;
        long since;
        lock (room.Gate)
        {
            if (room.Tutorial is null) return;
            since = room.PlayingSince;
        }
        var seconds = since > 0 ? (int)Math.Clamp((Environment.TickCount64 - since) / 1000, 1, 359999) : 0;
        var (user, _) = SessionPlayer.RequireIdentity(session);
        var first = await stages.RecordClearAsync(user, new TutorialStageClear(stage, 0, seconds), ct).ConfigureAwait(false);
        logger?.LogInformation("Session {Id} player {User} cleared tutorial stage {Stage} ({Map}) in {Seconds} s; first clear: {First}.",
            session.Id, user, stage, request.MapKey, seconds, first);
        // The gift is granted once: the store reports a first clear only when no clear of the stage was stored.
        if (first && giftService is not null && gifts is not null && gifts.TryGetValue(request.MapKey, out var gift))
            await giftService.GrantAsync(session, user, gift, ct).ConfigureAwait(false);
    }

    internal async ValueTask CloseAsync(ISession session, TutorialRoundClosed request, CancellationToken ct)
    {
        if (registry.TryGet(session.Id) is not { } room) return;
        await room.Executor.ExecuteAsync(_ =>
        {
            lock (room.Gate)
            {
                if (room.Tutorial is null)
                    throw new InvalidOperationException("Tutorial cleanup cannot detach an ordinary room.");
                registry.Detach(session.Id);
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
    }
}
