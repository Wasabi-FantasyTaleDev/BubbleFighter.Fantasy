using BubbleFighter.Fantasy.Contracts.Cluster;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

/// <summary>The lease change task a parked worker sleeps on.</summary>
public sealed class NodeLeaseSourceTests
{
    [Fact]
    public void AWaiterWithTheCurrentLeaseSleepsUntilADifferentOneIsPublished()
    {
        var source = new NodeLeaseSource("a", "http://directory.example");
        var none = source.WhenChangedFrom(null);
        Assert.False(none.IsCompleted);

        source.Publish(new("first", 1, 30));
        Assert.True(none.IsCompleted);
        var first = source.Current;
        var waiting = source.WhenChangedFrom(first);
        Assert.False(waiting.IsCompleted);

        // Every heartbeat publishes the lease again; the same lease wakes nobody.
        source.Publish(new("first", 1, 30));
        Assert.False(waiting.IsCompleted);
        Assert.False(source.WhenChangedFrom(new NodeLeaseCredentials("first", 1)).IsCompleted);

        source.Publish(new("second", 2, 30));
        Assert.True(waiting.IsCompleted);
        Assert.False(source.WhenChangedFrom(source.Current).IsCompleted);
    }

    [Fact]
    public void AWaiterWhoseLeaseIsAlreadyStaleDoesNotSleep()
    {
        var source = new NodeLeaseSource("a", "http://directory.example");
        source.Publish(new("first", 1, 30));
        var seen = source.Current;
        source.Publish(new("second", 2, 30));

        Assert.True(source.WhenChangedFrom(seen).IsCompleted);
        Assert.True(source.WhenChangedFrom(null).IsCompleted);
    }

    [Fact]
    public void LosingTheLeaseWakesTheWaiter()
    {
        var source = new NodeLeaseSource("a", "http://directory.example");
        source.Publish(new("first", 1, 30));
        var waiting = source.WhenChangedFrom(source.Current);

        source.Publish(null);

        Assert.True(waiting.IsCompleted);
        Assert.False(source.WhenChangedFrom(null).IsCompleted);
    }

    [Fact]
    public void ASourceThatCannotReportChangesKeepsItsCallerPolling()
    {
        INodeLeaseSource source = new Silent();
        Assert.True(source.WhenChangedFrom(source.Current).IsCompleted);
        Assert.True(source.WhenChangedFrom(null).IsCompleted);
    }

    private sealed class Silent : INodeLeaseSource
    {
        public string NodeId => "a";
        public string DirectoryUrl => "http://directory.example";
        public NodeLeaseCredentials? Current => new("token", 1);
    }
}
