using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Clan;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

public sealed class ClanIdentityTests
{
    [Fact]
    public void ClanRequestsResolveTheLoggedInPlayer()
    {
        var session = new FeatureSession();
        Assert.Equal(0, ClanPlugin.TryGetUserNum(session));
        session.SetLobbyPlayer(42, "clanner");
        Assert.Equal(42, ClanPlugin.TryGetUserNum(session));
    }

    private sealed class FeatureSession : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        public long Id => 1;

        public IConnection Connection => throw new NotSupportedException();

        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
