using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private IMessageSender<PlazaGameReplied>? _plazaGameReplied;
    private IMessageSender<PlazaGameSyncNotified>? _plazaGameSync;
    private IMessageSender<PlazaGameRecordPublished>? _plazaGameRecord;
    private IPlazaGameRecordStore? _plazaGameRecords;
    private IPlayerWalletRepository? _plazaGameWallets;
    private InGameOptions _plazaGameOptions = new();

    // One booth request per player at a time; the entry is removed when the request ends, so it holds at most the
    // players currently inside a booth request.
    private readonly ConcurrentDictionary<int, byte> _plazaGameBusy = new();

    // Players whose cast was accepted and not yet lifted. The client sends a cast request for every cast it starts
    // (kr-1256 sub_842600, called only from the booth's click handler kr-1256 sub_841C80), so one lift uses one cast.
    // An entry leaves on the lift or when the player leaves the plaza, so it holds at most the plaza's players.
    private readonly ConcurrentDictionary<int, byte> _plazaCasts = new();

    /// <summary>Installs the plaza booths: the dialect's senders, the day record store and the wallet repository.</summary>
    internal void ConfigurePlazaGames(InGameOptions options, IMessageSender<PlazaGameReplied> replied,
        IMessageSender<PlazaGameSyncNotified> sync, IMessageSender<PlazaGameRecordPublished> record,
        IPlazaGameRecordStore records, IPlayerWalletRepository wallets)
    {
        if (!PlazaBound)
            throw new ConfigException("Plaza games require an installed plaza scene.");
        _plazaGameOptions = options;
        _plazaGameReplied = replied;
        _plazaGameSync = sync;
        _plazaGameRecord = record;
        _plazaGameRecords = records;
        _plazaGameWallets = wallets;
    }

    private DateOnly PlazaGameDay() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(_plazaGameOptions.PlazaGamesUtcOffsetHours));

    /// <summary>Sends the day's booth record to a player entering the plaza, so the fortune booth shows today's paper.</summary>
    private async ValueTask PublishPlazaGameRecordAsync(ISession session, int userNum, CancellationToken ct)
    {
        if (_plazaGameRecord is null || _plazaGameRecords is null) return;
        var record = await _plazaGameRecords.ReadAsync(userNum, PlazaGameDay(), ct).ConfigureAwait(false);
        await _plazaGameRecord.SendAsync(session, new(record.FishingCount, record.FortunePaper), ct).ConfigureAwait(false);
    }

    /// <summary>Answers a booth record request (kr-812 asks for it itself) with today's record of the player in the plaza.</summary>
    public async ValueTask HandlePlazaGameRecordAsync(ISession session, PlazaGameRecordRequested request, CancellationToken ct)
    {
        var member = _plaza.Find(session.Id);
        if (member is null || member.Reserved || _registry.TryGet(session.Id) is not null)
            return;
        await PublishPlazaGameRecordAsync(session, member.UserNumber, ct).ConfigureAwait(false);
    }

    /// <summary>Answers one booth request of a player standing in the plaza.</summary>
    public async ValueTask HandlePlazaGameAsync(ISession session, PlazaGameRequested request, CancellationToken ct)
    {
        if (_plazaGameReplied is null) return;
        var member = _plaza.Find(session.Id);
        if (member is null || member.Reserved || _registry.TryGet(session.Id) is not null)
            return;
        var userNum = member.UserNumber;
        if (!_plazaGameBusy.TryAdd(userNum, 0))
        {
            await RefusePlazaGameAsync(session, request.Game, 0, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            using var authority = _mutations?.Enter(session);
            var reply = request.Game switch
            {
                PlazaGame.Basketball => await ShootBasketballAsync(userNum, request, ct).ConfigureAwait(false),
                PlazaGame.FishingStart => await StartFishingAsync(userNum, request, ct).ConfigureAwait(false),
                PlazaGame.RodLift => await LiftRodAsync(userNum, request, ct).ConfigureAwait(false),
                _ => await DrawFortuneAsync(userNum, request, ct).ConfigureAwait(false),
            };
            await _plazaGameReplied.SendAsync(session, reply, ct).ConfigureAwait(false);
            if (request.Game is PlazaGame.RodLift or PlazaGame.Fortune && reply.Outcome != PlazaGameOutcome.Refused)
                await PublishPlazaGameRecordAsync(session, userNum, ct).ConfigureAwait(false);
            _logger.LogInformation("Plaza player {User}: {Game} -> {Outcome}.", userNum, request.Game, reply.Outcome);
        }
        finally { _plazaGameBusy.TryRemove(userNum, out _); }
    }

    /// <summary>Passes a booth animation step to everyone else in the plaza; the client skips its own steps.</summary>
    public async ValueTask HandlePlazaGameSyncAsync(ISession session, PlazaGameSyncRequested request, CancellationToken ct)
    {
        if (_plazaGameSync is null || _plaza.Find(session.Id) is null)
            return;
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            var member = _plaza.Find(session.Id);
            if (member is null || member.Reserved || _registry.TryGet(session.Id) is not null)
                return;
            var notice = new PlazaGameSyncNotified(member.UserNumber, request.Subcommand, request.Value);
            foreach (var other in _plaza.Sessions())
            {
                if (other.SessionId == session.Id || _registry.TryGet(other.SessionId) is not null)
                    continue;
                await NotifyScenePeerAsync(other.Game!, _plazaGameSync, notice, operationCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private ValueTask RefusePlazaGameAsync(ISession session, PlazaGame game, ulong paper, CancellationToken ct) =>
        _plazaGameReplied!.SendAsync(session, new(game, PlazaGameOutcome.Refused) { Paper = paper }, ct);

    // The shot's cost is charged and a hit's reward paid in one settlement; the player must hold the cost first.
    private async ValueTask<PlazaGameReplied> ShootBasketballAsync(int userNum, PlazaGameRequested request, CancellationToken ct)
    {
        var cost = request.PublishedCost;
        if (request.DisplayedCost != cost)
            return new(PlazaGame.Basketball, PlazaGameOutcome.Refused);
        var settled = await SettlePlazaGameAsync(userNum, PlazaGameDay(), ct, cost,
            request.Hit ? _plazaGameOptions.PlazaBasketballHitReward : 0).ConfigureAwait(false);
        return settled.Status != PlazaGameSettlementStatus.Settled ? new(PlazaGame.Basketball, PlazaGameOutcome.Refused)
            : new(PlazaGame.Basketball, request.Hit ? PlazaGameOutcome.BasketballHit : PlazaGameOutcome.BasketballMiss)
                { Wallet = settled.Wallet.Lucci };
    }

    // A cast is accepted while the day's limit is not reached and none is pending. A priced cast costs the published
    // base price times the day's catches plus one, the price the client shows (kr-1256 sub_842600).
    private async ValueTask<PlazaGameReplied> StartFishingAsync(int userNum, PlazaGameRequested request, CancellationToken ct)
    {
        var day = PlazaGameDay();
        var record = await _plazaGameRecords!.ReadAsync(userNum, day, ct).ConfigureAwait(false);
        if (record.FishingCount >= _plazaGameOptions.PlazaFishingDailyLimit || request.DisplayedCost != request.PublishedCost
            || _plazaCasts.ContainsKey(userNum))
            return new(PlazaGame.FishingStart, PlazaGameOutcome.Refused);
        var cost = checked(request.PublishedCost * (record.FishingCount + 1));
        if (cost > 0 && (await SettlePlazaGameAsync(userNum, day, ct, cost).ConfigureAwait(false)).Status
            != PlazaGameSettlementStatus.Settled)
            return new(PlazaGame.FishingStart, PlazaGameOutcome.Refused);
        _plazaCasts[userNum] = 0;
        return new(PlazaGame.FishingStart, PlazaGameOutcome.FishingStarted);
    }

    // A lift uses the pending cast. A hooked lift counts one fish (within the daily limit) and pays the catch reward
    // in one settlement; an empty lift, or one without a cast, is refused.
    private async ValueTask<PlazaGameReplied> LiftRodAsync(int userNum, PlazaGameRequested request, CancellationToken ct)
    {
        if (!_plazaCasts.TryRemove(userNum, out _) || !request.Hooked)
            return new(PlazaGame.RodLift, PlazaGameOutcome.Refused);
        var reward = _plazaGameOptions.PlazaFishingCatchReward;
        var settled = await SettlePlazaGameAsync(userNum, PlazaGameDay(), ct, credit: reward,
            fishLimit: _plazaGameOptions.PlazaFishingDailyLimit).ConfigureAwait(false);
        return settled.Status != PlazaGameSettlementStatus.Settled ? new(PlazaGame.RodLift, PlazaGameOutcome.Refused)
            : new(PlazaGame.RodLift, PlazaGameOutcome.Catch)
                { Paid = reward, Wallet = settled.Wallet.Lucci, FishingCount = settled.Record.FishingCount };
    }

    // One paper a day: today's stored paper is shown again for free; a new paper is stored and charged together.
    private async ValueTask<PlazaGameReplied> DrawFortuneAsync(int userNum, PlazaGameRequested request, CancellationToken ct)
    {
        var day = PlazaGameDay();
        var stored = await _plazaGameRecords!.ReadAsync(userNum, day, ct).ConfigureAwait(false);
        if (stored.FortunePaper != 0)
            return new(PlazaGame.Fortune, PlazaGameOutcome.FortunePaper) { Paper = stored.FortunePaper };
        var cost = request.PublishedCost;
        if (request.Paper == 0 || request.DisplayedCost != cost)
            return new(PlazaGame.Fortune, PlazaGameOutcome.Refused) { Paper = request.Paper };
        var settled = await SettlePlazaGameAsync(userNum, day, ct, cost, paper: request.Paper).ConfigureAwait(false);
        return settled.Status switch
        {
            PlazaGameSettlementStatus.Settled => new(PlazaGame.Fortune, PlazaGameOutcome.FortunePaper) { Paper = request.Paper },
            // Another session stored today's paper first: show that one, uncharged.
            PlazaGameSettlementStatus.PaperTaken => new(PlazaGame.Fortune, PlazaGameOutcome.FortunePaper)
                { Paper = settled.Record.FortunePaper },
            _ => new(PlazaGame.Fortune, PlazaGameOutcome.Refused) { Paper = request.Paper },
        };
    }

    // Applies one booth action's charge, credit, fish and paper to the stored record and wallet in one transaction.
    private async ValueTask<PlazaGameSettled> SettlePlazaGameAsync(int userNum, DateOnly day, CancellationToken ct,
        int charge = 0, int credit = 0, int fishLimit = 0, ulong paper = 0)
    {
        var seed = await _plazaGameWallets!.FindByUserNumAsync(userNum, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plaza player {userNum} has no wallet.");
        return await _plazaGameRecords!.SettleAsync(new(seed, day)
            { Charge = charge, Credit = credit, FishLimit = fishLimit, FortunePaper = paper }, ct).ConfigureAwait(false);
    }
}
