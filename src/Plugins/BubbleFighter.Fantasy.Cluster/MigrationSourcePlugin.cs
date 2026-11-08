using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Offers prepared remote rooms only after transferring the source connection's account authority.</summary>
[Plugin("cluster.migration-source", Description = "Bounded native room departure and exact account-handoff withdrawal.",
    Requires = [typeof(IMigrationBroker), typeof(INodeLeaseSource), typeof(IAccountSessionHandoff), typeof(IOnlinePlayers)],
    Optional = [typeof(IPartyDirectory), typeof(IHostReadiness), typeof(IMigrationTargetDirectory)], OptionalMany = [typeof(IRemoteRoomMigrationEndpoint), typeof(IMessageSender<ChannelMoveOffered>), typeof(IMessageSender<ServerNotice>)],
    ProvidesMany = [typeof(IRemoteRoomMigration)])]
public sealed class MigrationSourcePlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MigrationSourceOptions>();
        var senders = context.Services.GetAll<IMessageSender<ChannelMoveOffered>>()
            .Where(sender => sender.Protocol == options.ClientBuild + "/game").ToArray();
        if (senders.Length != 1) throw new ConfigException("Migration source requires exactly one native move offer sender for its client_build.");
        context.Services.TryGet<IPartyDirectory>(out var parties);
        if (parties is { SupportsSoloReservation: false })
            throw new ConfigException("Migration source requires atomic solo reservations from the party directory.");
        context.Services.TryGet<IHostReadiness>(out var readiness);
        context.Services.TryGet<IMigrationTargetDirectory>(out var targets);
        var notices = context.Services.GetAll<IMessageSender<ServerNotice>>()
            .Where(sender => sender.Protocol == options.ClientBuild + "/game").ToArray();
        if (targets is null || notices.Length > 1 || options.HomeUnavailableNoticeEnabled && notices.Length != 1)
            throw new ConfigException("Migration source requires a target directory and, when enabled, exactly one native notice sender.");
        var endpoints = context.Services.GetAll<IRemoteRoomMigrationEndpoint>()
            .Where(endpoint => endpoint.ClientBuild == options.ClientBuild).ToArray();
        if (endpoints.Length != 1)
            throw new ConfigException("Migration source requires exactly one room migration endpoint for its client_build.");
        var source = new MigrationSource(options, context.Services.Get<IMigrationBroker>(), context.Services.Get<INodeLeaseSource>(),
            context.Services.Get<IAccountSessionHandoff>(), context.Services.Get<IOnlinePlayers>(), parties, senders[0],
            readiness, TimeProvider.System, context.Logger, targets, notices.SingleOrDefault());
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(source), cancellationToken).ConfigureAwait(false);
        await context.RunTaskAsync("native-departure-cleanup", source.RunAsync, cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.RegisterMany<IRemoteRoomMigration>(source));
        await context.AcquireEffectAsync(_ => ValueTask.FromResult(endpoints[0].Bind(source)), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Chooses the source build and native channel and bounds pending departures and cleanup.</summary>
public sealed class MigrationSourceOptions
{
    /// <summary>Gets or sets the build served by this migration row.</summary>
    [Required, RegularExpression("[a-z]{2}-[0-9]+")]
    public string ClientBuild { get; set; } = "";

    /// <summary>Gets or sets the source's native channel, which the destination must explicitly admit.</summary>
    [Range(1, 255)]
    public int Channel { get; set; }

    /// <summary>Gets or sets whether an unavailable home return sends a native notice; disable only for builds without an audited notice consumer. Refusals are always logged.</summary>
    public bool HomeUnavailableNoticeEnabled { get; set; } = true;

    /// <summary>Gets or sets the notice shown when the player remains on the visiting node after a refused return.</summary>
    [Required, StringLength(512, MinimumLength = 1)]
    public string HomeUnavailableNotice { get; set; } = "Return to your home server is unavailable. You remain on this server; leave any party and retry from the plaza exit.";

    /// <summary>Gets or sets the maximum pending departures, including failed cleanup awaiting retry.</summary>
    [Range(1, 4096)]
    public int MaxMigrations { get; set; } = 64;

    /// <summary>Gets or sets the deadline for preparing a departure and for each cleanup attempt.</summary>
    [Range(100, 60000)]
    public int OperationTimeoutMs { get; set; } = 10000;

    /// <summary>Gets or sets the maximum wait for the client to leave its retained source connection.</summary>
    [Range(100, 60000)]
    public int MigrationTimeoutMs { get; set; } = 10000;

    /// <summary>Gets or sets the interval for inspecting expired, disconnected or stale departures.</summary>
    [Range(100, 10000)]
    public int CleanupIntervalMs { get; set; } = 1000;
}
