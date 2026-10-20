using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopBalanceTests
{
    [Fact]
    public async Task RepeatedReadsUseLatestWalletWithoutWritingOrReseeding()
    {
        var repository = new Wallets { Current = new(42, 100, 5000) };
        var sender = new Sender();
        var handler = new ShopBalanceHandler(repository, _ => throw new InvalidOperationException("Unexpected seed."), sender);
        await handler.HandleAsync(new Session(42), new(), default);
        repository.Current = new(42, 100, 500);
        await handler.HandleAsync(new Session(42), new(), default);
        Assert.Equal(new[] { 5000, 500 }, sender.Sent.Select(s => s.Wallet.Cash));
        Assert.Equal(2, repository.Reads);
    }

    [Fact]
    public async Task MissingWalletPublishesFallbackWithoutCreatingPersistentState()
    {
        var repository = new Wallets();
        var sender = new Sender();
        var handler = new ShopBalanceHandler(repository, user => new(user, 123, 456), sender);
        await handler.HandleAsync(new Session(42), new(), default);
        Assert.Equal(new PlayerWallet(42, 123, 456), Assert.Single(sender.Sent).Wallet);
        Assert.Null(repository.Current);
    }

    [Fact]
    public async Task AckOnlyModeReadsItsCurrentFallbackOnEveryRequest()
    {
        var current = new PlayerWallet(42, 100, 5000);
        var sender = new Sender();
        var handler = new ShopBalanceHandler(null, _ => current, sender);
        await handler.HandleAsync(new Session(42), new(), default);
        current = current with { Cash = 500 };
        await handler.HandleAsync(new Session(42), new(), default);
        Assert.Equal(new[] { 5000, 500 }, sender.Sent.Select(s => s.Wallet.Cash));
    }

    [Fact]
    public async Task MissingIdentityAndWrongWalletAreRejectedWithoutPublication()
    {
        var repository = new Wallets { Current = new(99, 100, 5000) };
        var sender = new Sender();
        var handler = new ShopBalanceHandler(repository, _ => throw new InvalidOperationException(), sender);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new Session(0), new(), default).AsTask());
        Assert.Equal(0, repository.Reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new Session(42), new(), default).AsTask());
        Assert.Empty(sender.Sent);
    }

    private sealed class Wallets : IPlayerWalletRepository
    {
        internal PlayerWallet? Current { get; set; }
        internal int Reads { get; private set; }
        public ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct)
        {
            Reads++;
            Assert.Equal(42, userNum);
            return ValueTask.FromResult(Current);
        }
        public ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct) =>
            throw new InvalidOperationException("A balance check must never write a wallet.");
    }

    private sealed class Sender : IMessageSender<ShopBalanceUpdated>
    {
        public string Protocol => "test/game";
        internal List<ShopBalanceUpdated> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, ShopBalanceUpdated message, CancellationToken ct)
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long user) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(user);
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new InvalidOperationException("Business logic must use the typed sender.");
    }
}
