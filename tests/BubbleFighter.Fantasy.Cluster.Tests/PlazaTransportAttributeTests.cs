using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Checks complete, bounded appearance transfer across the plaza federation boundary.</summary>
public sealed class PlazaTransportAttributeTests
{
    /// <summary>JSON preserves every modifier field, and each transport boundary owns nested values and worn identities.</summary>
    [Fact]
    public void AttributeRoundTripOwnsItsSnapshot()
    {
        int[] ids = [300014, 203100];
        uint[] words = [4294965296u, 0x3F800000u, 0, 7];
        var member = new PlazaMember(3, 7, "seven", 5, 300014, ids)
        {
            ColorIndex = 2,
            ItemAttributes = [new(203100, 7, words, uint.MaxValue, "A")],
        };
        var sent = PlazaTransport.ToWire("cn-774", new(PlazaFederationEventKind.Entered, 7, member, default));
        ids[1] = 203101;
        words[0] = 0;
        var decoded = JsonSerializer.Deserialize<PlazaWireEvent>(JsonSerializer.Serialize(sent, PlazaTransport.Json), PlazaTransport.Json)!;
        Assert.True(PlazaTransport.Valid(decoded));
        var received = PlazaTransport.FromWire(decoded).Member!;
        decoded.Member!.EquippedItemIds[1] = 203102;
        var wireAttribute = Assert.Single(decoded.Member.ItemAttributes);
        ((IList<uint>)wireAttribute.Values)[0] = 1;
        var attribute = Assert.Single(received.ItemAttributes);
        Assert.Equal(new[] { 300014, 203100 }, received.EquippedItemIds);
        Assert.Equal((203100, 7u, uint.MaxValue, "A"), (attribute.ItemId, attribute.Kind, attribute.Unknown28, attribute.Unknown32));
        Assert.Equal(new uint[] { 4294965296u, 0x3F800000u, 0, 7 }, attribute.Values);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)attribute.Values)[0] = 2);
    }

    /// <summary>Absent attributes from an older node and an explicit empty vector both produce a clear appearance.</summary>
    [Fact]
    public void OlderMessagesAndExplicitEmptyAttributesRemainCompatible()
    {
        const string Older = """
            {"build":"cn-774","kind":"entered","user":7,"member":{"nickname":"seven","level":5,"character_item_id":300014,"equipped_item_ids":[300014],"color_index":2},"position":null}
            """;
        var old = JsonSerializer.Deserialize<PlazaWireEvent>(Older, PlazaTransport.Json)!;
        Assert.True(PlazaTransport.Valid(old));
        Assert.Empty(PlazaTransport.FromWire(old).Member!.ItemAttributes);
        var explicitEmpty = old with { Member = old.Member! with { ItemAttributes = [] } };
        Assert.Empty(PlazaTransport.FromWire(explicitEmpty).Member!.ItemAttributes);
    }

    /// <summary>Malformed, unrelated and aggregate oversize vectors are rejected before they reach a scene.</summary>
    [Fact]
    public void InvalidAttributeVectorsAreRejected()
    {
        var look = new PlazaWireMember("seven", 5, 300014, [300014, 203100], 2);
        var valid = new RoomItemAttribute(203100, 7, [1, 2, 3, 4], 0, "");
        var invalid = new RoomItemAttribute[][]
        {
            null!,
            [null!],
            [valid with { ItemId = 203101 }],
            [valid with { Values = null! }],
            [valid with { Unknown32 = null! }],
            [valid with { Values = new uint[InventoryLimits.MaxInventorySlotsPerBag + 1] }],
            Enumerable.Repeat(valid, InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(),
            Enumerable.Repeat(valid with { Values = new uint[InventoryLimits.MaxInventorySlotsPerBag] }, 32).ToArray(),
            [valid with { Unknown32 = new string('A', 16384) }, valid with { Unknown32 = new string('B', 16384) }],
        };
        foreach (var attributes in invalid)
        {
            var change = new PlazaWireEvent("cn-774", PlazaFederationEventKind.Entered, 7,
                look with { ItemAttributes = attributes }, null);
            Assert.False(PlazaTransport.Valid(change));
            Assert.Throws<InvalidDataException>(() => PlazaTransport.FromWire(change));
        }
        Assert.False(PlazaTransport.Valid(new("cn-774", PlazaFederationEventKind.Entered, 7,
            look with { EquippedItemIds = [300014, 300014] }, null)));
    }
}
