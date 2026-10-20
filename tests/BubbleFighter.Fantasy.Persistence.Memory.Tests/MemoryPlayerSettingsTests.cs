using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks key-map replacement and table capacity without a database.</summary>
public sealed class MemoryPlayerSettingsTests : PlayerSettingsContractTests
{
    protected override Task<IPlayerSettingsRepository> CreateAsync(CustomizedKeyLimits limits) =>
        Task.FromResult<IPlayerSettingsRepository>(new MemoryPlayerSettings(new(4), limits));

    [Fact]
    public async Task ReadAndUnseenResetDoNotConsumePlayerCapacity()
    {
        var store = new MemoryPlayerSettings(new(1), new());
        Assert.Empty(await store.ReadKeysAsync(1, "a", default));
        await store.ReplaceKeysAsync(1, "a", [], default);
        await store.ReplaceKeysAsync(2, "a", [new("Jump", 1)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceKeysAsync(1, "a", [new("Fire", 2)], default).AsTask());
        Assert.Equal([new CustomizedKey("Jump", 1)], await store.ReadKeysAsync(2, "a", default));
    }

    [Fact]
    public void InvalidCapacityPolicyFailsBeforeStoreCreation()
    {
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => new MemoryPlayerSettings(new(1), new() { MaxScopes = 0 }));
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => new MemoryPlayerSettings(new(1), new() { MaxMappings = 512, MaxNameCharacters = 1024 }));
    }
}
