using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class FighterInfoBindingTests
{
    /// <summary>The target's cache receives instance modifiers before the native inspection reply.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task InspectionPublishesTheCapturedTargetModifiers(string build)
    {
        var binding = Binding(build, out var table);
        var session = new Session();
        uint[] words = [2, 17, 0, 0];
        var appearance = new FighterAppearance(300007, [300007, 301001], [new RoomItemAttribute(301001, 7, words, 1, "")]);
        words[1] = 999;
        await binding.SendAsync(session, new(new PlayerIdentity(77, "target", "Target", 9), appearance), default);
        Assert.Equal(table.S2C("gspInventoryViewUpdateView"), session.Sent[0].Opcode);
        Assert.Equal(table.S2C("gspRepFighterInfo"), session.Sent[1].Opcode);
        var structure = EquipmentViewValues.Of(TestBuilds.Packets, build).ViewStructure;
        var field = Assert.Single(TestBuilds.Packets.Resolve(build, structure), entry => entry.Type == "list<ItemAttr>").Name;
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, structure, session.Sent[0].Body);
        var attribute = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(decoded[field])));
        Assert.Equal(301001, attribute["unknown_4"]);
        Assert.Equal(new[] { 2, 17, 0, 0 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(attribute["unknown_12"]).Select(Convert.ToInt32));
    }
}
