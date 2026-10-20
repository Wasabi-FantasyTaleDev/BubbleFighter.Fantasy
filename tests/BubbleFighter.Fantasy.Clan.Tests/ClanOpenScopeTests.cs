using BubbleFighter.Fantasy.Contracts.Clans;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

public sealed class ClanOpenScopeTests
{
    [Fact]
    public void RemembersReplacesAndRemovesByConnection()
    {
        using var scope = new ClanOpenScope(2);
        var first = new ClanOpenRecord(1, ClanOpenKind.MainOpen);
        var second = new ClanOpenRecord(1, ClanOpenKind.Attendance);
        scope.Remember(10, first);
        Assert.Equal(first, scope.TryGet(10));
        scope.Remember(10, second);
        Assert.Equal(second, scope.TryGet(10));
        scope.Remove(10);
        Assert.Null(scope.TryGet(10));
    }

    [Fact]
    public void RefusesANewConnectionOnceCapacityIsReached()
    {
        using var scope = new ClanOpenScope(1);
        scope.Remember(1, new ClanOpenRecord(1, ClanOpenKind.MainOpen));
        Assert.Throws<InvalidOperationException>(() =>
            scope.Remember(2, new ClanOpenRecord(2, ClanOpenKind.Attendance)));
        // Same connection may still replace its own record.
        var replaced = new ClanOpenRecord(1, ClanOpenKind.ClanServerAck);
        scope.Remember(1, replaced);
        Assert.Equal(replaced, scope.TryGet(1));
    }

    [Fact]
    public void DisposeClearsEveryRetainedOpen()
    {
        var scope = new ClanOpenScope(4);
        scope.Remember(1, new ClanOpenRecord(1, ClanOpenKind.MainOpen));
        scope.Remember(2, new ClanOpenRecord(2, ClanOpenKind.Attendance));
        scope.Dispose();
        Assert.Null(scope.TryGet(1));
        Assert.Null(scope.TryGet(2));
        Assert.Throws<ObjectDisposedException>(() =>
            scope.Remember(3, new ClanOpenRecord(3, ClanOpenKind.MainOpen)));
    }
}
