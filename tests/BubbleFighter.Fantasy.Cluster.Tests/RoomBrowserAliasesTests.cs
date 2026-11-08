using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class RoomBrowserAliasesTests
{
    [Fact]
    public void EqualNativeRoomNumbersOnDifferentNodesReceiveDistinctAliases()
    {
        var aliases = new RoomBrowserAliases(8);
        var local = Room("a");
        var remote = Room("b");
        var rows = aliases.Replace([local, remote]);
        Assert.NotEqual(rows[0].RoomId, rows[1].RoomId);
        Assert.Equal("a", aliases.Resolve(rows[0].RoomId)!.NodeId);
        Assert.Equal("b", aliases.Resolve(rows[1].RoomId)!.NodeId);
        Assert.Equal(1, aliases.Resolve(rows[1].RoomId)!.RoomId);
        Assert.Null(aliases.Resolve(0));
        Assert.Null(aliases.Resolve(65536));
    }

    [Fact]
    public void AHighAuthoritativeRoomNumberUsesAPositiveNativeClickAlias()
    {
        var aliases = new RoomBrowserAliases(short.MaxValue);
        var room = Room("a");
        room = room with { RoomId = ushort.MaxValue, Listing = room.Listing! with { RoomId = ushort.MaxValue } };
        var alias = Assert.Single(aliases.Replace([room])).RoomId;
        Assert.InRange(alias, 1, short.MaxValue);
        Assert.Equal(ushort.MaxValue, aliases.Resolve(alias)!.RoomId);
    }

    [Fact]
    public void RefreshOrderingDoesNotChangeExistingAliasesAndMissingRoomsStopResolving()
    {
        var aliases = new RoomBrowserAliases(8);
        var a = Room("a");
        var b = Room("b");
        var rows = aliases.Replace([a, b]);
        var aId = rows[0].RoomId;
        var bId = rows[1].RoomId;
        rows = aliases.Replace([b with { Title = "Changed", Listing = b.Listing! with { Name = "Changed" } }, a]);
        Assert.Equal(bId, rows[0].RoomId);
        Assert.Equal(aId, rows[1].RoomId);
        aliases.Replace([a]);
        Assert.Null(aliases.Resolve(bId));
        Assert.Equal(bId, aliases.Replace([b])[0].RoomId);
        Assert.Null(aliases.Resolve(aId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecreatedRoomsAndReplacementNodeLeasesNeverReuseAnOldAlias(bool changeLease)
    {
        var aliases = new RoomBrowserAliases(8);
        var original = Room("a");
        var oldAlias = Assert.Single(aliases.Replace([original])).RoomId;
        var replacement = changeLease ? original with { Epoch = 2 } : original with { InstanceId = Guid.NewGuid() };
        var newAlias = Assert.Single(aliases.Replace([replacement])).RoomId;
        Assert.NotEqual(oldAlias, newAlias);
        Assert.Null(aliases.Resolve(oldAlias));
        Assert.Equal(replacement.InstanceId, aliases.Resolve(newAlias)!.InstanceId);
        Assert.Equal(replacement.Epoch, aliases.Resolve(newAlias)!.Epoch);
    }

    [Fact]
    public void ExhaustionInvalidatesVisibilityAndDoesNotSpendAPartialHistory()
    {
        var aliases = new RoomBrowserAliases(2);
        var first = Room("a");
        var second = Room("b");
        var oldAlias = Assert.Single(aliases.Replace([first])).RoomId;
        Assert.Throws<InvalidOperationException>(() => aliases.Replace([second, Room("c")]));
        Assert.Null(aliases.Resolve(oldAlias));
        var secondAlias = Assert.Single(aliases.Replace([second])).RoomId;
        Assert.NotEqual(oldAlias, secondAlias);
        Assert.Throws<InvalidOperationException>(() => aliases.Replace([Room("c")]));
        Assert.Null(aliases.Resolve(secondAlias));
        Assert.Equal(oldAlias, Assert.Single(aliases.Replace([first])).RoomId);
    }

    [Fact]
    public void MalformedOrDuplicateRefreshesCannotRetainAnOldDestination()
    {
        var aliases = new RoomBrowserAliases(8);
        var room = Room("a");
        FederatedRoom[][] invalid =
        [
            [room, room], [room with { InstanceId = Guid.Empty }], [room with { Epoch = 0 }],
            [room with { Listing = null }], [room with { RoomId = 65536 }], [room with { NodeId = "../b" }],
        ];
        foreach (var rooms in invalid)
        {
            var oldAlias = Assert.Single(aliases.Replace([room])).RoomId;
            Assert.Throws<InvalidDataException>(() => aliases.Replace(rooms));
            Assert.Null(aliases.Resolve(oldAlias));
        }
    }

    [Fact]
    public void ExplicitInvalidationRetainsHistoryWithoutAllowingAnyJoinResolution()
    {
        var aliases = new RoomBrowserAliases(1);
        var room = Room("a");
        var id = Assert.Single(aliases.Replace([room])).RoomId;
        aliases.Invalidate();
        Assert.Null(aliases.Resolve(id));
        Assert.Equal(id, Assert.Single(aliases.Replace([room])).RoomId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    [InlineData(65536)]
    public void AliasCapacityStaysPositiveAfterTheNativeSignedWordEcho(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomBrowserAliases(capacity));

    private static FederatedRoom Room(string node) => new(node, 1, "kr-1256", 1, "Room", "3", "map", "Waiting", 1, 8, false, false)
    {
        InstanceId = Guid.NewGuid(), Listing = new(1, "Room", "Host", 3, 1, 8, false),
    };
}
