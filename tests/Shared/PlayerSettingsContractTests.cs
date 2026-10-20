using BubbleFighter.Fantasy.Contracts.Settings;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>Exercises the account-scoped whole-map replacement contract against each local persistence provider.</summary>
public abstract class PlayerSettingsContractTests
{
    protected abstract Task<IPlayerSettingsRepository> CreateAsync(CustomizedKeyLimits limits);

    [Fact]
    public async Task OrderedMapsRemainIsolatedByAccountAndExplicitScope()
    {
        var store = await CreateAsync(new());
        Assert.Empty(await store.ReadKeysAsync(1, "control-a", default));
        CustomizedKey[] first = [new("Jump", 32), new("\U00010400", -1), new("jump", 0)];
        await store.ReplaceKeysAsync(1, "control-a", first, default);
        await store.ReplaceKeysAsync(1, "control-b", [new("Fire", int.MinValue)], default);
        await store.ReplaceKeysAsync(2, "control-a", [new("Jump", 65)], default);
        Assert.Equal(first, await store.ReadKeysAsync(1, "control-a", default));
        Assert.Equal([new CustomizedKey("Fire", int.MinValue)], await store.ReadKeysAsync(1, "control-b", default));
        Assert.Equal([new CustomizedKey("Jump", 65)], await store.ReadKeysAsync(2, "control-a", default));
        Assert.Empty(await store.ReadKeysAsync(2, "control-b", default));
    }

    [Fact]
    public async Task ResetReleasesScopeCapacityAndDoesNotDeleteOtherScopes()
    {
        var store = await CreateAsync(new() { MaxScopes = 1 });
        await store.ReplaceKeysAsync(1, "a", [new("Jump", 1)], default);
        await store.ReplaceKeysAsync(1, "a", [new("Jump", 2)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceKeysAsync(1, "b", [new("Fire", 3)], default).AsTask());
        Assert.Equal([new CustomizedKey("Jump", 2)], await store.ReadKeysAsync(1, "a", default));
        await store.ReplaceKeysAsync(1, "a", [], default);
        await store.ReplaceKeysAsync(1, "b", [new("Fire", 3)], default);
        await store.ReplaceKeysAsync(1, "a", [], default);
        Assert.Empty(await store.ReadKeysAsync(1, "a", default));
        Assert.Equal([new CustomizedKey("Fire", 3)], await store.ReadKeysAsync(1, "b", default));
    }

    [Fact]
    public async Task RejectedAndCancelledWritesPreserveTheCompletePreviousMap()
    {
        var store = await CreateAsync(new() { MaxMappings = 2, MaxNameCharacters = 4 });
        CustomizedKey[] valid = [new("Jump", 1), new("Fire", 2)];
        await store.ReplaceKeysAsync(1, "a", valid, default);
        CustomizedKey[][] invalid = [
            [new("Jump", 1), new("Jump", 2)], [new("TooLong", 3)], [new("", 3)],
            [new("\0", 4)], [new("\uD800", 5)], [new("\uDC00", 6)], [null!],
            [new("A", 1), new("B", 2), new("C", 3)],
        ];
        foreach (var rows in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceKeysAsync(1, "a", rows, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceKeysAsync(1, "Bad Scope", valid, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReplaceKeysAsync(0, "a", valid, default).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReplaceKeysAsync(1, "a", [], new(true)).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadKeysAsync(1, "a", new(true)).AsTask());
        Assert.Equal(valid, await store.ReadKeysAsync(1, "a", default));
    }

    [Fact]
    public async Task CallerMutationsCannotModifySavedMappings()
    {
        var store = await CreateAsync(new());
        CustomizedKey[] rows = [new("Jump", 1), new("Fire", 2)];
        await store.ReplaceKeysAsync(1, "a", rows, default);
        rows[0] = new("Other", 9);
        var received = await store.ReadKeysAsync(1, "a", default);
        if (received is CustomizedKey[] mutable) mutable[0] = new("Other", 10);
        Assert.Equal([new CustomizedKey("Jump", 1), new CustomizedKey("Fire", 2)],
            await store.ReadKeysAsync(1, "a", default));
    }

    [Fact]
    public async Task ConcurrentSnapshotsNeverObserveAPartiallyReplacedMap()
    {
        var store = await CreateAsync(new());
        await store.ReplaceKeysAsync(1, "a", [new("Jump", 0), new("Fire", 0)], default);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(n => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await store.ReplaceKeysAsync(1, "a", [new("Jump", n), new("Fire", n)], default);
                var snapshot = await store.ReadKeysAsync(1, "a", default);
                Assert.Equal(2, snapshot.Count);
                Assert.Equal(snapshot[0].Key, snapshot[1].Key);
            }
        })));
    }
}
