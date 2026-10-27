using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Process-local persistence row. Publishes the same repository interfaces as the
/// MySQL persistence row (fighters, inventory, money, create-user submits, clan and shop opens) plus
/// the per-player active weapon-preset slot, backed by bounded in-process
/// dictionaries, so a deployment whose database is not reachable still gets
/// store-backed behaviour: the lobby can resolve the login inventory and money
/// bursts from the store, the shop can charge a stored wallet and grant
/// purchased bag slots, and a granted weapon-preset swap survives within the
/// process lifetime. State lives only for the life of the process and resets on
/// restart; a deployment that must survive restarts mounts the MySQL row
/// instead. The row deliberately publishes no
/// <c>IDbConnectionFactory</c>: domain consumers resolve repository contracts,
/// and remaining database-specific consumers must not receive a factory without
/// a reachable server.
///
/// </summary>
/// <remarks>
/// Mutually exclusive with the MySQL persistence row in one profile: the service
/// registry refuses a second registration of the same interface, so exactly one
/// of the two rows is enabled at a time. Enabling both fails the second row at
/// load, which is the loud outcome the architecture requires.
/// </remarks>
[Plugin("persistence.memory",
    Description = "Bounded in-process persistence for deployments without a database. State resets on restart.",
    Provides = [
        typeof(IClanStore),
        typeof(ILauncherAccountRepository),
        typeof(ILauncherSessionStore),
        typeof(IShopSessionStore),
        typeof(IFighterRepository),
        typeof(IInventoryRepository),
        typeof(IPlayerWalletRepository),
        typeof(IPlayerRepository),
        typeof(IPlayerSettingsRepository),
        typeof(BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository),
        typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore),
        typeof(IPlayerInventoryState),
        typeof(IShopPurchaseStore),
        typeof(IPresentStore),
        typeof(IPlayerMovementState),
        typeof(BubbleFighter.Fantasy.Contracts.Messenger.IMessengerStore)])]
public sealed class MemoryPersistencePlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MemoryPersistenceOptions>();
        var table = new MemoryPlayerTable(options.MaxPlayers);
        var settings = new MemoryPlayerSettings(table, options.CustomizedKeys);
        var movement = new MemoryPlayerMovementState(options.MaxMovementActors);

        context.Effect(context.Services.Register<ILauncherAccountRepository>(new MemoryLauncherAccounts(options.MaxPlayers)));
        context.Effect(context.Services.Register<ILauncherSessionStore>(new MemoryLauncherSessions(options.MaxPlayers)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore>(new MemoryTutorialStages(options.MaxPlayers)));
        var players = new MemoryPlayerRepository(table);
        var presents = new MemoryPresentStore(table, players);
        context.Effect(context.Services.Register<IPlayerRepository>(players));
        context.Effect(context.Services.Register<IPlayerSettingsRepository>(settings));
        context.Effect(context.Services.Register<IShopSessionStore>(new MemoryShopSessionStore(table)));
        context.Effect(context.Services.Register<IClanStore>(new MemoryClanStore(table)));
        context.Effect(context.Services.Register<IFighterRepository>(new MemoryFighterRepository(table)));
        context.Effect(context.Services.Register<IInventoryRepository>(new MemoryInventoryRepository(table)));
        context.Effect(context.Services.Register<IPlayerWalletRepository>(new MemoryPlayerWalletRepository(table)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository>(
            new MemoryPlayerProgressRepository(table)));
        context.Effect(context.Services.Register<IPlayerInventoryState>(new MemoryPlayerInventoryState(table)));
        context.Effect(context.Services.Register<IShopPurchaseStore>(new MemoryShopPurchaseStore(table, presents)));
        context.Effect(context.Services.Register<IPresentStore>(presents));
        context.Effect(context.Services.Register<IPlayerMovementState>(movement));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Messenger.IMessengerStore>(new MemoryMessengerStore(options.MaxPlayers)));

        context.Logger.LogInformation(
            "In-memory persistence active for up to {MaxPlayers} players and {MaxMovementActors} movement actor(s); state is process-local and resets on restart.",
            options.MaxPlayers, options.MaxMovementActors);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Configuration for the <see cref="MemoryPersistencePlugin"/> row.</summary>
public sealed class MemoryPersistenceOptions
{
    /// <summary>Gets or sets the per-account scope, row and name budgets for saved customized keys.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public CustomizedKeyLimits CustomizedKeys { get; set; } = new();

    /// <summary>
    /// Gets or sets how many distinct players the row tracks before it refuses a
    /// new one. Every store in the row is keyed by player (create-user submits
    /// share the bound by wire nickname), so this is the row's only growth bound;
    /// per-player row counts are already capped by
    /// <see cref="InventoryLimits"/>. A full table fails the offending call
    /// instead of growing without limit.
    ///
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int MaxPlayers { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how many distinct in-session movement actors the row tracks for the others-only
    /// movement broadcast before it refuses a new one. Actors are keyed by the id each C2S 32923 sample
    /// carries, which the client computes per seat, so the bound is a distinct-actor count, not a per-player
    /// count. A full store fails the offending merge instead of growing without limit.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int MaxMovementActors { get; set; } = 4096;
}
