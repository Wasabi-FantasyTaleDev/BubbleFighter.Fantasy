using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class PrologueStartTests
{
    /// <summary>Legacy tutorial admission and its initial equipped record share modifiers derived from the resolved bag.</summary>
    [Fact]
    public async Task TutorialPublishesTheCapturedPetModifiers()
    {
        const string build = "cn-406";
        var (schema, play) = TestBuilds.Of(build);
        var lifecycle = new TutorialLifecycleRouter();
        var events = new EventBus(NullLogger.Instance);
        uint[] words = [1, 37, 0, 0];
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
            ValueTask.FromResult(request.Slot.ItemId == 301001
                ? new InventoryItemPresentation([new RoomItemAttribute(301001, 7, words, 0, "")], null)
                : InventoryItemPresentation.Empty));
        var bag = new WornBag([new(12, InventoryBag.CharacterBag, 1, 300001, 1, 1),
            new(12, InventoryBag.CharacterBag, 2, 301001, 1, 1)]);
        using var handler = new TutorialHandler(TestBuilds.Packets, build, schema, play.Tutorial,
            play.MatchStart, play.Wire, new TutorialTables(new TutorialTable()), NullLogger.Instance,
            oneSeatWaves: play.OneSeatWaves, messages: lifecycle, inventory: bag, sharedLifecycle: true, events: events);
        var values = new Dictionary<string, object?> { ["time_attack_time_limit_ms"] = 99999000, ["difficulty"] = 1 };
        foreach (var field in TestBuilds.Packets.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr"))
            values[field.Name] = "rel_prologue_01";
        var session = new TutorialSession(build);
        await handler.HandlePlayTutorialAsync(session,
            new(107, PacketCodec.Write(TestBuilds.Packets, build, "TutorialPlayRequest", values)), default);
        words[0] = 99;
        var admission = Assert.IsType<TutorialRoundOpened>(Assert.Single(lifecycle.Messages));
        Assert.Equal(1U, Assert.Single(admission.ItemAttributes).Values[0]);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", Assert.Single(session.Sent, frame => frame.Opcode == 110).Payload);
        var equipped = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(decoded["equipped_item_lists"])));
        var attribute = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(equipped["unknown_48"])));
        Assert.Equal(301001, attribute["unknown_4"]);
        Assert.Equal(new[] { 1, 37, 0, 0 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(attribute["unknown_12"]).Select(Convert.ToInt32));
    }
}
