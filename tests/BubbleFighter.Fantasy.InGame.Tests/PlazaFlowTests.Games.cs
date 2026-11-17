using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class PlazaFlowTests
{
    private sealed record GameRig(Rig Rig, Sink<PlazaGameReplied> Replies, Sink<PlazaGameSyncNotified> Sync,
        Sink<PlazaGameRecordPublished> Records, MemoryPlayerWalletRepository Wallets);

    private static async Task<GameRig> GamesAsync(InGameOptions options, params (long User, int Lucci)[] players)
    {
        var rig = new Rig(options);
        var table = new MemoryPlayerTable(8);
        var wallets = new MemoryPlayerWalletRepository(table);
        foreach (var (user, lucci) in players)
            await wallets.UpsertAsync(new PlayerWallet(user, lucci, 0), default);
        var replies = new Sink<PlazaGameReplied>();
        var sync = new Sink<PlazaGameSyncNotified>();
        var records = new Sink<PlazaGameRecordPublished>();
        rig.Flow.ConfigurePlazaGames(options, replies, sync, records, new MemoryPlazaGameRecords(table), wallets);
        return new(rig, replies, sync, records, wallets);
    }

    /// <summary>A shot is charged at the configured price, a hit is paid back, and a short wallet is refused.</summary>
    [Fact]
    public async Task BasketballChargesTheShotAndPaysAHit()
    {
        var options = new InGameOptions { PlazaBasketballHitReward = 100 };
        var games = await GamesAsync(options, (11, 120), (22, 10));
        var shooter = Rig.Player(11, "shooter");
        var poor = Rig.Player(22, "poor");
        try
        {
            await EnterAsync(games.Rig, shooter);
            await EnterAsync(games.Rig, poor);
            await games.Rig.Flow.HandlePlazaGameAsync(shooter, Shot(50), default);
            await games.Rig.Flow.HandlePlazaGameAsync(shooter, Shot(50) with { Hit = true }, default);
            // A shot showing another price than the published one is refused.
            await games.Rig.Flow.HandlePlazaGameAsync(shooter, Shot(40) with { Hit = true }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(poor, Shot(50), default);
            // A hit would leave the wallet above the cost, but the player must be able to pay the shot first.
            await games.Rig.Flow.HandlePlazaGameAsync(poor, Shot(50) with { Hit = true }, default);
            var shots = games.Replies.To(shooter);
            Assert.Equal((PlazaGameOutcome.BasketballMiss, 70L), (shots[0].Outcome, shots[0].Wallet));
            Assert.Equal((PlazaGameOutcome.BasketballHit, 120L), (shots[1].Outcome, shots[1].Wallet));
            Assert.Equal(PlazaGameOutcome.Refused, shots[2].Outcome);
            Assert.All(games.Replies.To(poor), reply => Assert.Equal(PlazaGameOutcome.Refused, reply.Outcome));
            Assert.Equal(2, games.Replies.To(poor).Count());
            Assert.Equal(10, (await games.Wallets.FindByUserNumAsync(22, default))!.Lucci);
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    // A shot showing displayedCost, at the published price of 50.
    private static PlazaGameRequested Shot(int displayedCost) => new(PlazaGame.Basketball, displayedCost) { PublishedCost = 50 };

    /// <summary>A priced cast costs the published base price times the day's catches plus one, as the client shows it.</summary>
    [Fact]
    public async Task PricedCastsCostTheBasePriceTimesTheCatchesPlusOne()
    {
        var games = await GamesAsync(new InGameOptions { PlazaFishingCatchReward = 0 }, (11, 35));
        var angler = Rig.Player(11, "angler");
        var cast = new PlazaGameRequested(PlazaGame.FishingStart, 10) { PublishedCost = 10 };
        try
        {
            await EnterAsync(games.Rig, angler);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, cast, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, cast, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            // Two catches make the third cast cost 30, more than the 5 left.
            await games.Rig.Flow.HandlePlazaGameAsync(angler, cast, default);
            Assert.Equal(new[]
            {
                PlazaGameOutcome.FishingStarted, PlazaGameOutcome.Catch, PlazaGameOutcome.FishingStarted, PlazaGameOutcome.Catch,
                PlazaGameOutcome.Refused,
            }, games.Replies.To(angler).Select(reply => reply.Outcome));
            Assert.Equal(5, (await games.Wallets.FindByUserNumAsync(11, default))!.Lucci);
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    /// <summary>A hooked lift counts a fish and pays it until the daily limit; an empty lift pays nothing.</summary>
    [Fact]
    public async Task FishingPaysEachCatchUpToTheDailyLimit()
    {
        var options = new InGameOptions { PlazaFishingCatchReward = 200, PlazaFishingDailyLimit = 2 };
        var games = await GamesAsync(options, (11, 0));
        var angler = Rig.Player(11, "angler");
        try
        {
            await EnterAsync(games.Rig, angler);
            Assert.Equal(new PlazaGameRecordPublished(0, 0), Assert.Single(games.Records.To(angler)));
            // An empty lift uses its cast; three hooked casts land two fish within the limit of two.
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.FishingStart, 0), default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0), default);
            for (var cast = 0; cast < 3; cast++)
            {
                await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.FishingStart, 0), default);
                await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            }
            var replies = games.Replies.To(angler);
            Assert.Equal(PlazaGameOutcome.FishingStarted, replies[0].Outcome);
            Assert.Equal(PlazaGameOutcome.Refused, replies[1].Outcome);
            Assert.Equal(PlazaGameOutcome.FishingStarted, replies[2].Outcome);
            Assert.Equal((PlazaGameOutcome.Catch, 200, 200L, 1), (replies[3].Outcome, replies[3].Paid, replies[3].Wallet, replies[3].FishingCount));
            Assert.Equal((PlazaGameOutcome.Catch, 400L, 2), (replies[5].Outcome, replies[5].Wallet, replies[5].FishingCount));
            // The day's limit is reached: the next cast is refused and its lift has no cast to use.
            Assert.Equal(PlazaGameOutcome.Refused, replies[6].Outcome);
            Assert.Equal(PlazaGameOutcome.Refused, replies[7].Outcome);
            Assert.Equal(new PlazaGameRecordPublished(2, 0), games.Records.To(angler)[^1]);
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    /// <summary>A lift is paid only for an accepted cast: never without one, never twice, never after leaving the plaza.</summary>
    [Fact]
    public async Task EachLiftNeedsItsOwnAcceptedCast()
    {
        var games = await GamesAsync(new InGameOptions { PlazaFishingCatchReward = 200 }, (11, 0));
        var angler = Rig.Player(11, "angler");
        try
        {
            await EnterAsync(games.Rig, angler);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.FishingStart, 0), default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.FishingStart, 0), default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.FishingStart, 0), default);
            await games.Rig.Flow.LeavePlazaForRoomAsync(angler, default);
            await EnterAsync(games.Rig, angler);
            await games.Rig.Flow.HandlePlazaGameAsync(angler, new(PlazaGame.RodLift, 0) { Hooked = true }, default);
            Assert.Equal(new[]
            {
                PlazaGameOutcome.Refused, PlazaGameOutcome.FishingStarted, PlazaGameOutcome.Refused, PlazaGameOutcome.Catch,
                PlazaGameOutcome.Refused, PlazaGameOutcome.FishingStarted, PlazaGameOutcome.Refused,
            }, games.Replies.To(angler).Select(reply => reply.Outcome));
            Assert.Equal(200, (await games.Wallets.FindByUserNumAsync(11, default))!.Lucci);
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    /// <summary>One paper a day: the first draw is charged and stored, a later draw shows the stored paper for free.</summary>
    [Fact]
    public async Task FortuneKeepsOnePaperADay()
    {
        var options = new InGameOptions();
        var games = await GamesAsync(options, (11, 500));
        var reader = Rig.Player(11, "reader");
        try
        {
            await EnterAsync(games.Rig, reader);
            await games.Rig.Flow.HandlePlazaGameAsync(reader, new(PlazaGame.Fortune, 200) { Paper = 77, PublishedCost = 200 }, default);
            await games.Rig.Flow.HandlePlazaGameAsync(reader, new(PlazaGame.Fortune, 200) { Paper = 88, PublishedCost = 200 }, default);
            var replies = games.Replies.To(reader);
            Assert.Equal((PlazaGameOutcome.FortunePaper, 77UL), (replies[0].Outcome, replies[0].Paper));
            Assert.Equal((PlazaGameOutcome.FortunePaper, 77UL), (replies[1].Outcome, replies[1].Paper));
            Assert.Equal(300, (await games.Wallets.FindByUserNumAsync(11, default))!.Lucci);
            await EnterAsync(games.Rig, reader);
            Assert.Equal(new PlazaGameRecordPublished(0, 77), games.Records.To(reader)[^1]);
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    /// <summary>A booth animation step reaches the other plaza players, never the actor.</summary>
    [Fact]
    public async Task AnimationStepsReachTheOtherPlayers()
    {
        var games = await GamesAsync(new InGameOptions(), (11, 0), (22, 0));
        var actor = Rig.Player(11, "actor");
        var watcher = Rig.Player(22, "watcher");
        try
        {
            await EnterAsync(games.Rig, actor);
            await EnterAsync(games.Rig, watcher);
            await games.Rig.Flow.HandlePlazaGameSyncAsync(actor, new(100, 1), default);
            Assert.Empty(games.Sync.To(actor));
            Assert.Equal(new PlazaGameSyncNotified(11, 100, 1), Assert.Single(games.Sync.To(watcher)));
        }
        finally { await games.Rig.DisposeAsync(); }
    }

    /// <summary>A record request (kr-812 asks for it itself) is answered with today's record, and only inside the plaza.</summary>
    [Fact]
    public async Task TheRecordRequestIsAnsweredWithTodaysRecordInThePlaza()
    {
        var games = await GamesAsync(new InGameOptions(), (11, 0), (22, 0));
        var angler = Rig.Player(11, "angler");
        var stranger = Rig.Player(22, "stranger");
        try
        {
            await EnterAsync(games.Rig, angler);
            await games.Rig.Flow.HandlePlazaGameRecordAsync(angler, new(), default);
            // One record came with the plaza entry, one with the request.
            Assert.Equal(2, games.Records.To(angler).Count());
            Assert.All(games.Records.To(angler), record => Assert.Equal(new PlazaGameRecordPublished(0, 0), record));
            await games.Rig.Flow.HandlePlazaGameRecordAsync(stranger, new(), default);
            Assert.Empty(games.Records.To(stranger));
        }
        finally { await games.Rig.DisposeAsync(); }
    }
}
