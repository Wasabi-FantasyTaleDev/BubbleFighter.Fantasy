using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Unit tests for the plugin-owned version-verified connection table.
/// </summary>
public sealed class VersionVerifiedScopeTests
{
    [Fact]
    public void MarkStoresByConnectionAndIsIdempotent()
    {
        using var scope = new VersionVerifiedScope(2);
        scope.Mark(1);
        Assert.True(scope.IsVerified(1));
        Assert.Equal(1, scope.Count);

        scope.Mark(1);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.IsVerified(1));
    }

    [Fact]
    public void MarkRefusesANewConnectionPastCapacity()
    {
        using var scope = new VersionVerifiedScope(1);
        scope.Mark(1);
        var error = Assert.Throws<InvalidOperationException>(() => scope.Mark(2));
        Assert.Contains("max_tracked_connections", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.IsVerified(1));
        Assert.False(scope.IsVerified(2));
    }

    [Fact]
    public void RemoveAndDisposeDropEntries()
    {
        var scope = new VersionVerifiedScope(4);
        scope.Mark(1);
        scope.Mark(2);

        scope.Remove(1);
        Assert.False(scope.IsVerified(1));
        Assert.True(scope.IsVerified(2));

        scope.Dispose();
        Assert.Equal(0, scope.Count);
        Assert.False(scope.IsVerified(2));
    }

    [Fact]
    public void ConstructorRejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VersionVerifiedScope(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VersionVerifiedScope(-1));
    }
}
