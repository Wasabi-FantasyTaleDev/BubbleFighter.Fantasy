using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Lobby.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Distinguishes automatically provisioned accounts from completed native character creation.</summary>
public sealed class PlayerOnboardingTests
{
    /// <summary>A placeholder keeps its identity and retries the tutorial until completion is reported.</summary>
    [Fact]
    public async Task ExistingAccountCanCompleteNativeCreationOnce()
    {
        var repository = new InMemoryPlayerRepository();
        var placeholder = await repository.CreateAsync("old-account", "old-account", default);
        Assert.False(await repository.HasCreatedCharacterAsync(placeholder.UserNum, default));
        Assert.False(await repository.NicknameExistsAsync("old-account", default, "old-account"));
        var created = await repository.CreateCharacterAsync("old-account", "new-name", StarterKit.Default, default);
        Assert.NotNull(created);
        Assert.Equal(placeholder.UserNum, created.UserNum);
        Assert.Equal("new-name", created.Nickname);
        Assert.True(await repository.HasCreatedCharacterAsync(created.UserNum, default));
        Assert.True(await repository.NeedsTutorialAsync(created.UserNum, default));
        Assert.True(await repository.NeedsTutorialAsync(created.UserNum, default));
        await repository.CompleteTutorialAsync(created.UserNum, default);
        Assert.False(await repository.NeedsTutorialAsync(created.UserNum, default));
        Assert.Null(await repository.CreateCharacterAsync("old-account", "another-name", StarterKit.Default, default));
    }

    /// <summary>Finishing a placeholder cannot steal another player's nickname.</summary>
    [Fact]
    public async Task PlaceholderCannotTakeAnotherAccountName()
    {
        var repository = new InMemoryPlayerRepository();
        await repository.CreateAsync("one", "one-name", default);
        var second = await repository.CreateAsync("two", "two-name", default);
        Assert.True(await repository.NicknameExistsAsync("one-name", default, "two"));
        Assert.Null(await repository.CreateCharacterAsync("two", "one-name", StarterKit.Default, default));
        Assert.False(await repository.HasCreatedCharacterAsync(second.UserNum, default));
    }
}
