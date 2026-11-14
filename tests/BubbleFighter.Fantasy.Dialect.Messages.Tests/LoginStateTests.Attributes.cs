using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class LoginStateTests
{
    /// <summary>Login copies its supplied bag and derives menu modifiers before another publication can change source arrays.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task MenuUsesCapturedLoginRowsWithoutLaterAttributeResolution(string build)
    {
        int[] ids = [300001, 301001];
        uint[] words = [1, 37, 0, 0];
        InventorySlotRecord[] bag = [new(42, InventoryBag.CharacterBag, 1, 301001, 1, 1)];
        var events = new EventBus(NullLogger.Instance);
        var services = new ServiceRegistry();
        var menu = new MenuSender(build);
        using var menuRegistration = services.RegisterMany<BubbleFighter.Fantasy.Abstractions.Protocol.IMessageSender<MainMenuRoomOpened>>(menu);
        var projections = 0;
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            Assert.Equal(301001, request.Slot.ItemId);
            projections++;
            return ValueTask.FromResult(new InventoryItemPresentation([new RoomItemAttribute(301001, 7, words, 0, "")], null));
        });
        using var boundary = events.Intercept<LoginEquipmentRequested, bool>(async (request, next, ct) =>
        {
            ids[1] = 200001;
            words[0] = 99;
            bag[0] = bag[0] with { ItemId = 200001 };
            Assert.Equal(301001, request.Login.CharacterBag![0].ItemId);
            Assert.Equal(301001, request.Login.EquippedItemIds[1]);
            return await next(ct);
        });
        var session = new RecordingSession();
        await Binding(build, services, events, null).SendAsync(session,
            Login() with { EquippedItemIds = ids, CharacterBag = bag }, default);
        Assert.Equal(1, projections);
        if (build == "kr-812")
        {
            var opened = Assert.Single(menu.Opened);
            Assert.Equal(301001, opened.EquippedItemIds[1]);
            Assert.Equal(1U, Assert.Single(opened.ItemAttributes).Values[0]);
        }
        else
        {
            var opcode = TestBuilds.Opcodes(build).S2C("gspGotoRoom");
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", Assert.Single(session.Sent, frame => frame.Opcode == opcode).Payload);
            var profileField = build == "kr-1256" ? "profile_entries" : "equipped_objects";
            var structure = build == "kr-1256" ? "RoomProfileEntry" : "EquippedObjectEntry";
            var attributeField = Assert.Single(TestBuilds.Packets.Resolve(build, structure), field => field.Type == "list<ItemAttr>").Name;
            var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(decoded[profileField])));
            Assert.Contains(301001, Assert.IsAssignableFrom<IReadOnlyList<object?>>(profile["equipped_items"]));
            var attribute = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(profile[attributeField])));
            Assert.Equal(new[] { 1, 37, 0, 0 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(attribute["unknown_12"]).Select(Convert.ToInt32));
        }
    }
}
