using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Party;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Coordinates shared-account room arrival without making lobby and room plugins depend on each other.</summary>
[Plugin("cluster.migration-admission", Description = "Authorizes prepared native room arrivals and completes pending promotion.",
    Requires = [typeof(IMessageRouter), typeof(IRetainedPlayerAdmission), typeof(IMigrationArrivals), typeof(INodeLeaseSource),
        typeof(IAccountSessionHandoff), typeof(IAccountSessionAdmission)], Optional = [typeof(IHostReadiness), typeof(IPartyDirectory)],
    OptionalMany = [typeof(IMigrationRoomRecovery), typeof(IMigrationHomeAdmission), typeof(IRoomAdmissionDirectory), typeof(IInboundMessageBinding<MoveIntoRequested>),
        typeof(IInboundMessageBinding<PendingPromotionAcknowledged>), typeof(IMessageSender<MoveIntoReplied>),
        typeof(IMessageSender<PendingConnectionPromoted>), typeof(IMessageSender<PendingPromotionCompleted>)])]
public sealed class MigrationAdmissionPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MigrationAdmissionOptions>();
        var players = context.Services.Get<IRetainedPlayerAdmission>();
        var protocol = players.Protocol;
        RequireBinding<MoveIntoRequested>(context, protocol);
        var complete = Senders<PendingPromotionCompleted>(context, protocol);
        if (complete.Length > 1 || context.Services.GetAll<IInboundMessageBinding<PendingPromotionAcknowledged>>()
            .Count(binding => binding.Protocol == protocol) != complete.Length)
            throw new ConfigException("Native promotion acknowledgement and completion must be paired exactly once.");
        var rooms = context.Services.GetAll<IRoomAdmissionDirectory>()
            .Where(directory => protocol == directory.ClientBuild + "/game").ToArray();
        if (rooms.Length != 1)
            throw new ConfigException("Migration admission requires exactly one room directory for its lobby protocol.");
        context.Services.TryGet<IHostReadiness>(out var readiness);
        var homes = context.Services.GetAll<IMigrationHomeAdmission>()
            .Where(directory => protocol == directory.ClientBuild + "/game").ToArray();
        if (homes.Length > 1) throw new ConfigException("Migration admission requires at most one home scene directory per build.");
        var recoveries = context.Services.GetAll<IMigrationRoomRecovery>()
            .Where(service => protocol == service.ClientBuild + "/game").ToArray();
        if (recoveries.Length != 1) throw new ConfigException("Migration admission requires exactly one room recovery service per build; enable home_migration_admission.");
        context.Services.TryGet<IPartyDirectory>(out var parties);
        if (parties is { SupportsSoloReservation: false })
            throw new ConfigException("Migration admission requires atomic solo reservations from the party directory.");
        var migration = new MigrationAdmission(options, players, context.Services.Get<IMigrationArrivals>(),
            context.Services.Get<INodeLeaseSource>(), context.Services.Get<IAccountSessionHandoff>(),
            context.Services.Get<IAccountSessionAdmission>(), rooms[0], RequireSender<MoveIntoReplied>(context, protocol),
            RequireSender<PendingConnectionPromoted>(context, protocol), complete.SingleOrDefault(),
            readiness, TimeProvider.System, context.Logger, homes.SingleOrDefault(), parties, recoveries.SingleOrDefault());
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(migration), cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("native-migration-cleanup", migration.RunAsync, cancellationToken).ConfigureAwait(false);
        var messages = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(messages.On<MoveIntoRequested>(protocol, migration.MoveAsync));
        if (complete.Length != 0) context.Effect(messages.On<PendingPromotionAcknowledged>(protocol, migration.AcknowledgeAsync));
    }

    private static void RequireBinding<T>(IPluginContext context, string protocol) where T : IMessage
    {
        if (context.Services.GetAll<IInboundMessageBinding<T>>().Count(binding => binding.Protocol == protocol) != 1)
            throw new ConfigException($"Migration admission requires exactly one {typeof(T).Name} binding for {protocol}.");
    }

    private static IMessageSender<T>[] Senders<T>(IPluginContext context, string protocol) where T : IMessage =>
        context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();

    private static IMessageSender<T> RequireSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = Senders<T>(context, protocol);
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Migration admission requires exactly one {typeof(T).Name} sender for {protocol}.");
    }
}

/// <summary>Bounds native room admission and selects the deployment's proven channels for its client build.</summary>
public sealed class MigrationAdmissionOptions : IValidatableOptions
{
    /// <summary>Gets or sets the native channels accepted for retained room arrivals on this build.</summary>
    public int[] Channels { get; set; } = [];

    /// <summary>Gets or sets the combined limit for pending promotions, recovered scenes awaiting return and failed arrivals awaiting cleanup.</summary>
    [Range(1, 4096)]
    public int MaxMigrations { get; set; } = 64;

    /// <summary>Gets or sets the deadline for admission, promotion acknowledgement and each cleanup attempt.</summary>
    [Range(100, 60000)]
    public int TimeoutMs { get; set; } = 10000;

    /// <summary>Gets or sets the interval for retiring disconnected or expired pending promotions.</summary>
    [Range(100, 10000)]
    public int CleanupIntervalMs { get; set; } = 1000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Channels is null || Channels.Length is 0 or > 256 || Channels.Any(channel => channel is <= 0 or > byte.MaxValue)
            || Channels.Distinct().Count() != Channels.Length)
            yield return new ValidationResult("channels requires distinct nonzero native channel bytes proven for this build.", [nameof(Channels)]);
    }
}
