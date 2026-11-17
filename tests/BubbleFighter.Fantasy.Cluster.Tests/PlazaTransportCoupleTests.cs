using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>Preserves bounded relationship identity through the directory's plaza snapshots.</summary>
public sealed class PlazaTransportCoupleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoupleAndBreakupSurviveTheDirectoryTransport(bool paired)
    {
        var couple = paired ? new CoupleSceneState(22, "partner") : null;
        var member = new PlazaMember(0, 11, "owner", 1, 300001, [300001]) { Couple = couple };
        var outgoing = PlazaTransport.ToWire("kr-1256", new(PlazaFederationEventKind.Entered, 11, member, default));
        var incoming = JsonSerializer.Deserialize<PlazaWireEvent>(JsonSerializer.Serialize(outgoing, PlazaTransport.Json), PlazaTransport.Json)!;
        Assert.True(PlazaTransport.Valid(incoming));
        Assert.Equal(couple, PlazaTransport.FromWire(incoming).Member!.Couple);
    }

    [Fact]
    public void OlderSenderWithoutRelationshipIsAnExplicitAbsentSnapshot()
    {
        const string Older = """
            {"build":"kr-1256","kind":"entered","user":11,"member":{"nickname":"owner","level":1,"character_item_id":300001,"equipped_item_ids":[300001],"color_index":0},"position":null}
            """;
        var incoming = JsonSerializer.Deserialize<PlazaWireEvent>(Older, PlazaTransport.Json)!;
        Assert.True(PlazaTransport.Valid(incoming));
        Assert.Null(PlazaTransport.FromWire(incoming).Member!.Couple);
    }

    [Fact]
    public void InvalidRelationshipIdentityIsRejectedBeforeEnteringTheScene()
    {
        var member = new PlazaWireMember("owner", 1, 300001, [300001], 0);
        CoupleSceneState[] invalid = [new(0, "partner"), new(-1, "partner"), new(11, "owner"),
            new((long)int.MaxValue + 1, "partner"), new(22, ""), new(22, "bad\nname"), new(22, new('x', 65))];
        foreach (var couple in invalid)
        {
            var change = new PlazaWireEvent("kr-1256", PlazaFederationEventKind.Entered, 11, member with { Couple = couple }, null);
            Assert.False(PlazaTransport.Valid(change));
            Assert.Throws<InvalidDataException>(() => PlazaTransport.FromWire(change));
        }
    }
}
