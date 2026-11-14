using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A hidden tutorial owns its admitted equipment values and clears them on a subsequent empty admission.</summary>
    [Fact]
    public async Task TutorialSeatRetainsAndClearsItsCapturedModifiers()
    {
        var rig = new Rig();
        try
        {
            int[] ids = [300005, 301001];
            uint[] words = [1, 37, 0, 0];
            var admission = new TutorialRoundOpened(101, "rel_prologue_01", 300005, ids, 1, 0, 99999000, true)
                { GameConnectionOnly = true, ItemAttributes = [new RoomItemAttribute(301001, 7, words, 0, "")] };
            ids[1] = 200001;
            words[0] = 99;
            var tutorials = new TutorialRoomFlow(rig.Registry);
            await tutorials.OpenAsync(rig.Host, admission, default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            Assert.Equal(1U, Assert.Single(room.Tutorial!.ItemAttributes).Values[0]);
            Assert.Equal(1U, Assert.Single(room.FindByGame(rig.Host.Id)!.ItemAttributes).Values[0]);
            Assert.Contains(301001, room.FindByGame(rig.Host.Id)!.EquippedItemIds!);
            await tutorials.OpenAsync(rig.Host, admission with { ItemAttributes = [] }, default);
            room = rig.Registry.TryGet(rig.Host.Id)!;
            Assert.Empty(room.Tutorial!.ItemAttributes);
            Assert.Empty(room.FindByGame(rig.Host.Id)!.ItemAttributes);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
