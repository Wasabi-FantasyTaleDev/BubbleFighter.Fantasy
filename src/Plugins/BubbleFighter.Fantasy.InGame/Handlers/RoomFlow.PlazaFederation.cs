using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The plaza shared with the same build on the other nodes of one account domain. A player present on another node
/// takes a seat here without a connection; local players are shown that seat and its positions with the same packets
/// as a local member (seat record, then position lines), so no client difference exists between the two.
/// </summary>
internal sealed partial class RoomFlow
{
    private IPlazaFederationLink? _plazaLink;

    private void PublishPlaza(PlazaFederationEvent change) => Volatile.Read(ref _plazaLink)?.Publish(change);

    /// <summary>Attaches the link and offers it every local member now present, with its last position.</summary>
    internal IDisposable BindPlazaFederation(IPlazaFederationLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (Interlocked.CompareExchange(ref _plazaLink, link, null) is not null)
            throw new InvalidOperationException("The plaza is already shared through another link.");
        foreach (var member in _plaza.Sessions())
        {
            var view = member.ToMember();
            link.Publish(new(PlazaFederationEventKind.Entered, view.UserNumber, view, default));
            if (!member.LastPosition.IsEmpty)
                link.Publish(new(PlazaFederationEventKind.Moved, view.UserNumber, null, member.LastPosition.ToArray()));
        }
        return new PlazaFederationBinding(this, link);
    }

    private sealed class PlazaFederationBinding(RoomFlow flow, IPlazaFederationLink link) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0
                || Interlocked.CompareExchange(ref flow._plazaLink, null, link) != link) return;
            // Without the link nothing refreshes the remote seats, so they leave now.
            _ = flow.ApplyPlazaFederationAsync(new(true, []), CancellationToken.None).AsTask().ContinueWith(
                failed => flow._logger.LogError(failed.Exception!.GetBaseException(), "Remote plaza members could not be cleared."),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>Applies remote changes on the plaza executor.</summary>
    internal ValueTask ApplyPlazaFederationAsync(PlazaFederationBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!PlazaBound) return ValueTask.CompletedTask;
        return new(_plaza.Executor.ExecuteAsync(async operationCt =>
        {
            if (batch.Snapshot)
            {
                var present = batch.Events.Where(change => change.Kind == PlazaFederationEventKind.Entered)
                    .Select(change => change.UserNumber).ToHashSet();
                foreach (var stale in _plaza.RemoteMembers().Where(member => !present.Contains(member.UserNumber)))
                    await RemoveRemotePlazaMemberAsync(stale.UserNumber, operationCt).ConfigureAwait(false);
            }
            foreach (var change in batch.Events)
            {
                switch (change.Kind)
                {
                    case PlazaFederationEventKind.Entered when change.Member is { } member:
                        await AddRemotePlazaMemberAsync(member with { UserNumber = change.UserNumber }, operationCt).ConfigureAwait(false);
                        break;
                    case PlazaFederationEventKind.Moved:
                        await MoveRemotePlazaMemberAsync(change.UserNumber, change.Position, operationCt).ConfigureAwait(false);
                        break;
                    case PlazaFederationEventKind.Left:
                        await RemoveRemotePlazaMemberAsync(change.UserNumber, operationCt).ConfigureAwait(false);
                        break;
                }
            }
        }, ct));
    }

    // Runs on the plaza executor. A new seat is announced exactly as a local entrant's.
    private async ValueTask AddRemotePlazaMemberAsync(PlazaMember member, CancellationToken ct)
    {
        var previousView = _plaza.FindRemote(member.UserNumber)?.ToMember();
        if (_plaza.FindRemote(member.UserNumber) is { } previous && previous.EntrySeed != member.EntrySeed)
            await RemoveRemotePlazaMemberAsync(member.UserNumber, ct).ConfigureAwait(false);
        if (_plaza.EnterRemote(member) is not { } entered)
        {
            _logger.LogInformation("Remote plaza member {User} is not shown: connected here, or the plaza is full.", member.UserNumber);
            return;
        }
        if (!entered.Added)
        {
            var updated = entered.Seat.ToMember();
            if (previousView is not null && (previousView.Nickname != updated.Nickname || previousView.Level != updated.Level
                || previousView.ColorIndex != updated.ColorIndex || previousView.CharacterItemId != updated.CharacterItemId
                || !previousView.EquippedItemIds.SequenceEqual(updated.EquippedItemIds)
                || !Equal(previousView.ItemAttributes, updated.ItemAttributes)))
            {
                foreach (var other in _plaza.Sessions())
                {
                    if (_registry.TryGet(other.SessionId) is not null) continue;
                    await NotifyScenePeerAsync(other.Game!, _plazaMemberEntered!, new(updated), ct).ConfigureAwait(false);
                    if (_plazaPositionReceived is not null && !entered.Seat.LastPosition.IsEmpty)
                        await NotifyScenePeerAsync(other.Game!, _plazaPositionReceived,
                            new(updated.UserNumber, entered.Seat.PositionSequence, entered.Seat.LastPosition, updated.EntrySeed), ct)
                            .ConfigureAwait(false);
                }
            }
            if (_recruitmentChanged is not null && previousView?.Recruitment != entered.Seat.Recruitment)
                await PublishPlazaRecruitmentAsync(entered.Seat, ct, federate: false).ConfigureAwait(false);
            return;
        }
        PlazaMember shown;
        lock (_plaza.Gate) shown = entered.Seat.ToMember();
        foreach (var other in _plaza.Sessions())
        {
            if (_registry.TryGet(other.SessionId) is not null) continue;
            await NotifyScenePeerAsync(other.Game!, _plazaMemberEntered!, new PlazaMemberEntered(shown), ct).ConfigureAwait(false);
            await SendPlazaRecruitmentTextAsync(other.Game!, shown, ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Remote plaza member {User} shown at seat {Slot}.", member.UserNumber, shown.Slot);
    }

    // Runs on the plaza executor. The sequence is this node's own count for the seat, as the clients here expect.
    private async ValueTask MoveRemotePlazaMemberAsync(int userNumber, ReadOnlyMemory<byte> position, CancellationToken ct)
    {
        if (_plazaPositionReceived is null || position.IsEmpty) return;
        PlazaPositionReceived received;
        lock (_plaza.Gate)
        {
            if (_plaza.FindRemote(userNumber) is not { } member) return;
            member.PositionSequence++;
            member.LastPosition = position;
            received = new(member.UserNumber, member.PositionSequence, position, member.EntrySeed);
        }
        foreach (var other in _plaza.Sessions())
        {
            if (_registry.TryGet(other.SessionId) is not null) continue;
            await NotifyScenePeerAsync(other.Game!, _plazaPositionReceived, received, ct).ConfigureAwait(false);
        }
    }

    // Runs on the plaza executor; the same leave a local member's departure sends.
    private ValueTask RemoveRemotePlazaMemberAsync(int userNumber, CancellationToken ct) =>
        _plaza.FindRemote(userNumber) is { } member ? LeavePlazaCoreAsync(member.SessionId, ct) : ValueTask.CompletedTask;
}

/// <summary>Offers a build's plaza to the cluster row that shares it across nodes.</summary>
internal sealed class PlazaFederationEndpoint(string clientBuild, RoomFlow flow) : IPlazaFederationEndpoint
{
    public string ClientBuild => clientBuild;

    public IDisposable Bind(IPlazaFederationLink link) => flow.BindPlazaFederation(link);

    public ValueTask ApplyAsync(PlazaFederationBatch batch, CancellationToken cancellationToken) =>
        flow.ApplyPlazaFederationAsync(batch, cancellationToken);
}
