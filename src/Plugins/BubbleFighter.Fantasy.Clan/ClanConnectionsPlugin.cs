using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The node's one table of logged-in clan connections, shared by every build's clan row, so a clan's members hear
/// each other whichever client build each of them plays. Entries leave when their connection closes or when the
/// build row that attached them unloads. The table is also the clan relay's endpoint: a cluster row may bind a link
/// that carries notices for members on other nodes, and hands this node the notices other nodes publish.
/// </summary>
[Plugin("clan.connections", Category = "social", Names = ["en=Clan connections", "zh-Hans=公会连接", "zh-Hant=公會連線", "ko=클랜 연결"],
    Description = "The logged-in clan-server connections of this node across every client build, for clan notices, chat and whispers.",
    Provides = [typeof(IClanConnections), typeof(IClanRelayEndpoint)])]
public sealed class ClanConnectionsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var table = new ClanConnections(context.Logger);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => table.Detach(e.Connection.Id)));
        context.Effect(context.Services.Register<IClanConnections>(table));
        context.Effect(context.Services.Register<IClanRelayEndpoint>(table));
        return ValueTask.CompletedTask;
    }
}

/// <summary>The connection table behind <see cref="IClanConnections"/> and the clan relay's endpoint.</summary>
internal sealed class ClanConnections(ILogger? logger = null) : IClanConnections, IClanRelayEndpoint
{
    private IClanRelayLink? _relay;

    // One entry per live clan connection, removed when the connection closes, so the listeners' connection limits
    // bound the table.
    private readonly ConcurrentDictionary<long, Entry> _byUser = new();
    private readonly ConcurrentDictionary<long, long> _userByConnection = new();
    // The last ClanMemberReport of each live connection, keyed like _byUser and dropped with it.
    private readonly ConcurrentDictionary<long, ClanMemberReported> _reports = new();

    private sealed record Entry(ISession Session, IClanDelivery Delivery);

    /// <inheritdoc />
    public void Attach(long userNum, ISession session, IClanDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(delivery);
        _byUser[userNum] = new Entry(session, delivery);
        _reports.TryRemove(userNum, out _);
        _userByConnection[session.Connection.Id] = userNum;
    }

    /// <summary>Forgets a closed connection. The player stays attached when a newer connection replaced it.</summary>
    public void Detach(long connectionId)
    {
        if (_userByConnection.TryRemove(connectionId, out var userNum)
            && _byUser.TryGetValue(userNum, out var entry) && entry.Session.Connection.Id == connectionId)
        {
            _byUser.TryRemove(new KeyValuePair<long, Entry>(userNum, entry));
            _reports.TryRemove(userNum, out _);
        }
    }

    /// <inheritdoc />
    public bool TryGet(long userNum, out ISession session, out IClanDelivery delivery)
    {
        if (_byUser.TryGetValue(userNum, out var entry) && !entry.Session.Connection.IsClosed)
        {
            (session, delivery) = (entry.Session, entry.Delivery);
            return true;
        }
        (session, delivery) = (null!, null!);
        return false;
    }

    /// <inheritdoc />
    public void Report(long userNum, ISession session, ClanMemberReported report)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(report);
        if (_byUser.TryGetValue(userNum, out var current) && current.Session.Connection.Id == session.Connection.Id)
            _reports[userNum] = report;
    }

    /// <inheritdoc />
    public ClanMemberReported? ReportOf(long userNum) =>
        TryGet(userNum, out _, out _) && _reports.TryGetValue(userNum, out var report) ? report : null;

    /// <inheritdoc />
    public bool Forward<T>(IReadOnlyList<long> userNums, T message) where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(userNums);
        ArgumentNullException.ThrowIfNull(message);
        var relay = Volatile.Read(ref _relay);
        if (relay is null) return false;
        var forwarded = false;
        foreach (var notice in ClanRelayKinds.Encode(userNums, message))
        {
            relay.Publish(notice);
            forwarded = true;
        }
        return forwarded;
    }

    /// <inheritdoc />
    public IDisposable Bind(IClanRelayLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (Interlocked.CompareExchange(ref _relay, link, null) is not null)
            throw new InvalidOperationException("The clan relay is already bound.");
        return new Binding(this, link);
    }

    /// <inheritdoc />
    public async ValueTask DeliverAsync(IReadOnlyList<ClanRelayNotice> notices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notices);
        foreach (var notice in notices)
        {
            try { await ClanRelayKinds.DeliverAsync(this, notice, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException error)
            {
                // One malformed notice from another node must not keep the rest from their members.
                logger?.LogWarning("A relayed clan notice ({Kind}) was refused: {Reason}", notice?.Kind, error.Message);
            }
        }
    }

    private sealed class Binding(ClanConnections table, IClanRelayLink link) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref table._relay, null, link);
    }

    /// <inheritdoc />
    public void Release(IClanDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        foreach (var (userNum, entry) in _byUser)
        {
            if (!ReferenceEquals(entry.Delivery, delivery))
                continue;
            if (_byUser.TryRemove(new KeyValuePair<long, Entry>(userNum, entry)))
            {
                _userByConnection.TryRemove(entry.Session.Connection.Id, out _);
                _reports.TryRemove(userNum, out _);
            }
        }
    }
}
