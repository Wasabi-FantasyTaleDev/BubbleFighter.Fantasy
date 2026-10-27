using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Publishes durable local identity and gameplay repositories from one bounded SQLite save.</summary>
[Plugin("persistence.sqlite", Description = "Explicit local SQLite save; mutually exclusive with other persistence providers.",
    Provides = [typeof(IPlayerRepository), typeof(ILauncherAccountRepository), typeof(ILauncherSessionStore), typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore), typeof(IFighterRepository),
        typeof(IInventoryRepository), typeof(IPlayerWalletRepository), typeof(IPlayerProgressRepository),
        typeof(IPlayerInventoryState), typeof(IShopPurchaseStore), typeof(IPresentStore), typeof(IShopSessionStore), typeof(IClanStore),
        typeof(IPlayerMovementState), typeof(IPlayerSettingsRepository), typeof(BubbleFighter.Fantasy.Contracts.Messenger.IMessengerStore)])]
public sealed class SqlitePlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = new SqliteDatabase(context.Config.Bind<SqliteOptions>(), context.Logger);
        context.Effect(database);
        var repositories = new SqliteRepositories(database);
        context.Effect(context.Services.Register<IPlayerRepository>(repositories));
        context.Effect(context.Services.Register<IPlayerSettingsRepository>(new SqlitePlayerSettings(database)));
        context.Effect(context.Services.Register<ILauncherAccountRepository>(repositories));
        context.Effect(context.Services.Register<ILauncherSessionStore>(new SqliteLauncherSessions(database)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Messenger.IMessengerStore>(new SqliteMessengerStore(database)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore>(new SqliteTutorialStages(database)));
        context.Effect(context.Services.Register<IFighterRepository>(repositories));
        context.Effect(context.Services.Register<IInventoryRepository>(repositories));
        context.Effect(context.Services.Register<IPlayerWalletRepository>(repositories));
        context.Effect(context.Services.Register<IPlayerProgressRepository>(repositories));
        context.Effect(context.Services.Register<IPlayerInventoryState>(repositories));
        context.Effect(context.Services.Register<IShopPurchaseStore>(repositories));
        context.Effect(context.Services.Register<IPresentStore>(repositories));
        context.Effect(context.Services.Register<IShopSessionStore>(repositories));
        context.Effect(context.Services.Register<IClanStore>(repositories));
        context.Effect(context.Services.Register<IPlayerMovementState>(new SqliteMovement(database.Options.MaxMovementActors)));
        context.Logger.LogInformation("SQLite save active; capacity {MaxPlayers} players, {MaxAccounts} accounts, {MaxBytes} main-file bytes.",
            database.Options.MaxPlayers, database.Options.MaxAccounts, database.Options.MaxDatabaseBytes);
        return ValueTask.CompletedTask;
    }
}
