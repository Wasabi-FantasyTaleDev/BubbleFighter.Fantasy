using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Publishes one player-directory-backed management scope with its configured notice encoder.</summary>
[Plugin("commands.players", Requires = [typeof(IOnlineCommandScopes), typeof(IOnlinePlayers), typeof(ISessionRegistry)],
    RequiresMany = [typeof(IMessageSender<ServerNotice>)])]
public sealed class OnlinePlayersPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<OnlinePlayerCommandOptions>();
        var senders = context.Services.GetAll<IMessageSender<ServerNotice>>()
            .Where(sender => sender.Protocol == options.Protocol).ToArray();
        if (senders.Length != 1)
            throw new ConfigException("commands.players requires exactly one notice encoder for its configured protocol.");
        var scope = new OnlinePlayerCommandScope(context.Row.Id, context.Services.Get<IOnlinePlayers>(),
            context.Services.Get<ISessionRegistry>(), senders[0]);
        context.Effect(context.Services.Get<IOnlineCommandScopes>().Register(context, scope));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Selects the actual game protocol in the player directory's service realm.</summary>
public sealed class OnlinePlayerCommandOptions
{
    /// <summary>Gets or sets the configured protocol whose dialect publishes the server notice encoder.</summary>
    [Required] public string Protocol { get; set; } = "";
}

internal sealed class OnlinePlayerCommandScope(string scope, IOnlinePlayers players, ISessionRegistry sessions,
    IMessageSender<ServerNotice> notices) : IOnlineCommandScope
{
    public string Scope => scope;

    public IReadOnlyList<ISession> Snapshot() => sessions.All.Where(session =>
        session.TryFeature<IPlayerSession>(out var player) && IsCurrent(session, player.Identity.UserNum)).ToArray();

    public bool IsCurrent(ISession session, int accountId) => !session.Connection.IsClosed &&
        session.TryFeature<IPlayerSession>(out var player) && player.Identity.UserNum == accountId &&
        ReferenceEquals(players.FindByUserNum(accountId), session);

    public ValueTask SendNoticeAsync(ISession session, string text, CancellationToken cancellationToken)
    {
        if (!session.TryFeature<IPlayerSession>(out var player) || !IsCurrent(session, player.Identity.UserNum))
            throw new InvalidOperationException("The notice target no longer holds its authenticated account.");
        return notices.SendAsync(session, new(text), cancellationToken);
    }
}
