using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Checks the medal-free Favorite title grant, its idempotence and the condition progress it publishes.</summary>
public sealed class FavoriteTitleTests
{
    private static async Task<(TitleTestRig Rig, Counter Masters, List<TitleProgressNotified> Progress, TitleRuntime Runtime)> CreateAsync()
    {
        var rig = await TitleTestRig.CreateAsync(true);
        rig.CatalogData.AddFavoriteTitles();
        rig.Options.FavoriteTitles = true;
        var masters = new Counter();
        var progress = new List<TitleProgressNotified>();
        var runtime = rig.Runtime(favorite: masters, progress: new TitleTestSender<TitleProgressNotified>(TitleTestRig.Protocol,
            (message, _) => { rig.Order.Add("progress"); progress.Add(message); return ValueTask.CompletedTask; }));
        return (rig, masters, progress, runtime);
    }

    /// <summary>A Favorite condition makes a type-3 title supported only with the option on; other challenge conditions stay unsupported.</summary>
    [Fact]
    public async Task OnlyASingleFavoriteConditionIsSupported()
    {
        using var rig = await TitleTestRig.CreateAsync(true);
        rig.CatalogData.AddFavoriteTitles();
        Assert.True(rig.CatalogData.Load().Titles[400006].HasUnsupportedConditions);
        var catalog = rig.CatalogData.Load(favorite: true);
        Assert.Equal(1, catalog.Titles[400006].FavoriteMasters);
        Assert.Equal(2, catalog.Titles[400007].FavoriteMasters);
        Assert.False(catalog.Titles[400006].HasUnsupportedConditions);
        Assert.False(catalog.Titles[400006].UsesMedals);
        Assert.Null(catalog.Titles[400001].FavoriteMasters);

        rig.CatalogData.Conditions[0] = ["400006", "LuxuryGrade", "5", "1"];
        var luxury = rig.CatalogData.Load(favorite: true).Titles[400006];
        Assert.Null(luxury.FavoriteMasters);
        Assert.True(luxury.HasUnsupportedConditions);
        rig.CatalogData.Conditions[0] = ["400006", "Favorite", "1", "1"];
        rig.CatalogData.Conditions.Add(["400006", "LuxuryGrade", "5", "2"]);
        Assert.Null(rig.CatalogData.Load(favorite: true).Titles[400006].FavoriteMasters);
        rig.CatalogData.Conditions.Add(["400999", "Favorite", "1", "1"]);
        Assert.Throws<InvalidDataException>(() => rig.CatalogData.Load(favorite: true));
        rig.CatalogData.Conditions.RemoveAt(rig.CatalogData.Conditions.Count - 1);
        rig.CatalogData.Conditions[0] = ["400006", "Favorite", "0", "1"];
        Assert.Throws<InvalidDataException>(() => rig.CatalogData.Load(favorite: true));
    }

    /// <summary>Reaching the count grants the title without medals, publishes the inventory first and never grants it twice.</summary>
    [Fact]
    public async Task GrantIsMedalFreePermanentAndIdempotent()
    {
        var (rig, masters, progress, runtime) = await CreateAsync();
        using (rig) using (runtime)
        {
            var medalsBefore = await rig.Store.ReadAsync(1, rig.Scope.Key, default);
            await runtime.EvaluateFavoriteAsync(rig.Session, default);
            Assert.Equal(["progress"], rig.Order);
            Assert.Equal([new TitleConditionProgress(6, 0, 1)], progress[0].Titles[0].Conditions);
            Assert.Equal([new TitleConditionProgress(6, 0, 2)], progress[0].Titles[1].Conditions);
            Assert.Empty(rig.Medals.Claims);

            masters.Value = 1;
            rig.Order.Clear();
            await runtime.EvaluateFavoriteAsync(rig.Session, default);
            Assert.Equal(["inventory", "progress"], rig.Order);
            var claim = Assert.Single(rig.Medals.Claims);
            Assert.True(claim.IsAutomaticGrant);
            Assert.Empty(claim.MedalDebits);
            var held = Assert.Single(rig.Inventories[0].CharacterBag, row => row.ItemId == 400906);
            Assert.Equal(InventoryLifetime.Permanent, held.Lifetime);
            Assert.DoesNotContain(rig.Inventories[0].CharacterBag, row => row.ItemId == 400907);
            Assert.True(rig.Inventories[0].IsCompleteSnapshot);
            Assert.Equal(new TitleConditionProgress(6, 1, 1), progress[1].Titles[0].Conditions[0]);
            Assert.Equal(new TitleConditionProgress(6, 1, 2), progress[1].Titles[1].Conditions[0]);
            Assert.Equal(medalsBefore, await rig.Store.ReadAsync(1, rig.Scope.Key, default));

            rig.Order.Clear();
            await runtime.EvaluateFavoriteAsync(rig.Session, default);
            Assert.Equal(["progress"], rig.Order);
            Assert.Single(rig.Medals.Claims);

            masters.Value = 5;
            rig.Order.Clear();
            await runtime.EvaluateFavoriteAsync(rig.Session, default);
            Assert.Equal(["inventory", "progress"], rig.Order);
            Assert.Equal(2, rig.Medals.Claims.Count);
            Assert.Equal(new TitleConditionProgress(6, 2, 2), progress[^1].Titles[1].Conditions[0]);
            Assert.Equal(1, (await rig.BagAsync()).Count(row => row.ItemId == 400906));
            Assert.Equal(1, (await rig.BagAsync()).Count(row => row.ItemId == 400907));
        }
    }

    /// <summary>The native screen never offers medal acquisition for a Favorite title, and the server refuses it too.</summary>
    [Fact]
    public async Task MedalAcquisitionOfAFavoriteTitleStaysRefused()
    {
        var (rig, _, _, runtime) = await CreateAsync();
        using (rig) using (runtime)
        {
            await runtime.HandleMakeAsync(rig.Session, new(1, 400006), default);
            Assert.Equal(new TitleMakeReplied(-1, 400006, 0), Assert.Single(rig.Made));
            Assert.Empty(rig.Medals.Claims);
        }
    }

    /// <summary>Outside the main menu nothing is read, granted or sent.</summary>
    [Fact]
    public async Task EvaluationOutsideTheMainMenuDoesNothing()
    {
        var (rig, masters, progress, runtime) = await CreateAsync();
        using (rig) using (runtime)
        {
            masters.Value = 3;
            rig.Session.Features.Get<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>().MoveTo(BubbleFighter.Fantasy.Contracts.Sessions.SessionPhase.Room);
            await runtime.EvaluateFavoriteAsync(rig.Session, default);
            Assert.Empty(progress);
            Assert.Empty(rig.Medals.Claims);
            Assert.Equal(0, masters.Reads);
        }
    }

    private sealed class Counter : IFavoriteWeaponProgress
    {
        internal int Value { get; set; }
        internal int Reads { get; private set; }
        public string Protocol => TitleTestRig.Protocol;
        public ValueTask<int> CountMasterWeaponsAsync(long userNum, CancellationToken ct) { Reads++; return ValueTask.FromResult(Value); }
    }
}