using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace SqliteSaveFixture;

/// <summary>Validates the independently loaded storage row across real Host process lifetimes.</summary>
[Plugin("test.sqlite.save", Requires = [typeof(IPlayerRepository), typeof(IInventoryRepository),
    typeof(ILauncherAccountRepository), typeof(IPlayerWalletRepository), typeof(IPlayerProgressRepository),
    typeof(IShopPurchaseStore), typeof(IPlayerInventoryState)])]
public sealed class SaveFixture : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<SaveFixtureOptions>();
        var players = context.Services.Get<IPlayerRepository>();
        var accounts = context.Services.Get<ILauncherAccountRepository>();
        var wallets = context.Services.Get<IPlayerWalletRepository>();
        var progress = context.Services.Get<IPlayerProgressRepository>();
        var inventory = context.Services.Get<IInventoryRepository>();
        var presets = context.Services.Get<IPlayerInventoryState>();
        var purchases = context.Services.Get<IShopPurchaseStore>();
        if (options.Mode == "seed")
        {
            if (!await accounts.RegisterAsync("fixture", "fixture-hash", cancellationToken))
                throw new InvalidOperationException("Fixture requires a fresh save.");
            var created = await players.CreateCharacterAsync("fixture", "saved", new([300001, 201001], [300001, 201001]), cancellationToken)
                ?? throw new InvalidOperationException("Fixture character was not created.");
            await wallets.UpsertAsync(new(created.UserNum, 100, 50), cancellationToken);
            await presets.SetActiveWeaponPresetSlotAsync(created.UserNum, 2, cancellationToken);
            await purchases.PurchaseAsync(new(new(created.UserNum, 0, 0), 42, 0, "lucci", 10, 1000,
                [new(100001, 3) { Lifetime = InventoryLifetime.Until(DateTimeOffset.FromUnixTimeMilliseconds(options.Deadline)) }]), cancellationToken);
            await progress.SettleRoundAsync(new("fixture-round", created.UserNum, 7, _ => new(created.UserNum, 2, 123)), cancellationToken);
            await players.CompleteTutorialAsync(created.UserNum, cancellationToken);
        }
        var player = await players.FindByUsernameAsync("fixture", cancellationToken)
            ?? throw new InvalidOperationException("Saved game identity is missing.");
        var account = await accounts.FindAsync("fixture", cancellationToken);
        var wallet = await wallets.FindByUserNumAsync(player.UserNum, cancellationToken);
        var advancement = await progress.FindAsync(player.UserNum, cancellationToken);
        var slots = await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 512, cancellationToken);
        var grant = slots.Single(slot => slot.ItemId == 100001);
        if (!players.PersistsState || !players.CreatesStarterInventoryAtomically || player.Level != 2
            || account?.UserNumber != player.UserNum || account.PasswordHash != "fixture-hash"
            || wallet?.Lucci != 97 || wallet.Cash != 50 || advancement?.Exp != 123
            || grant.Quantity != 3 || grant.Lifetime.ExpiresAtUnixMilliseconds != options.Deadline
            || await presets.GetActiveWeaponPresetSlotAsync(player.UserNum, cancellationToken) != 2
            || await players.NeedsTutorialAsync(player.UserNum, cancellationToken)
            || (await purchases.ListRecentAsync(player.UserNum, 16, cancellationToken)).Count != 1)
            throw new InvalidOperationException("Durable game snapshot differs after activation.");
    }
}

/// <summary>Defines the bounded seed or verification phase of the save-process fixture.</summary>
public sealed class SaveFixtureOptions : IValidatableOptions
{
    /// <summary>Gets or sets seed for first activation or verify for subsequent activation.</summary>
    public string Mode { get; set; } = "verify";

    /// <summary>Gets or sets the known absolute inventory deadline retained across processes.</summary>
    public long Deadline { get; set; } = 1893456000000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Mode is not ("seed" or "verify")) yield return new("mode must be seed or verify.");
    }
}
