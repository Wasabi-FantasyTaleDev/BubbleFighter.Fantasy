using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Per-packet writers reuse what an admitted packet snapshot resolves instead of resolving it again.</summary>
public sealed class PacketSnapshotReuseTests
{
    // Resolving every build's room tables allocates megabytes; a writer that reuses them allocates only its frame.
    private const long PerPacketBudget = 64 * 1024;

    [Fact]
    public void RoomTablesAreResolvedOncePerSnapshot()
    {
        var tables = PacketTables.Load(TestBuilds.PacketsDirectory());

        Assert.Same(RoomTables.For(tables), RoomTables.For(tables));
        Assert.Same(tables, RoomTables.For(tables).Tables);
    }

    [Fact]
    public void CollectedCoinFieldIsResolvedOncePerBuild()
    {
        var tables = PacketTables.Load(TestBuilds.PacketsDirectory());
        foreach (var build in tables.Builds)
        {
            var first = RoundResultWire.CollectedLucciField(tables, build);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var again = RoundResultWire.CollectedLucciField(tables, build);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(first, again);
            Assert.True(allocated == 0, $"{build}: resolving the collected coin field again allocated {allocated} bytes.");
        }
    }

    [Fact]
    public void RoomListRepliesDoNotResolveEveryBuild()
    {
        var tables = PacketTables.Load(TestBuilds.PacketsDirectory());
        var builds = tables.Builds.Where(build => tables.Resolve(build, "RoomListReply").Any(field => field.Name == "page")).ToArray();
        Assert.NotEmpty(builds);
        var empty = new RoomListSnapshot(0, []);
        foreach (var build in builds)
        {
            _ = RoomListCodec.Write(tables, build, empty);
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = RoomListCodec.Write(tables, build, empty);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < PerPacketBudget, $"{build}: an empty room list reply allocated {allocated} bytes.");
        }
    }
}
