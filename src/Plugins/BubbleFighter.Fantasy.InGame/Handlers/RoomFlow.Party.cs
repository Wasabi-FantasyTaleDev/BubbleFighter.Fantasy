using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private sealed record SceneGroup(ISession Leader, PartySnapshot Party, ISession[] Followers);
    private readonly IPartyDirectory? _parties;
    private readonly IOnlinePlayers? _online;
    private readonly SceneTransitions _sceneTransitions;

    /// <summary>Brings a newly admitted member to the committed leader's current scene through normal admission.</summary>
    internal async ValueTask<bool> HandlePartySceneFollowAsync(ISession session, PartySceneFollowRequested request, CancellationToken ct)
    {
        var memberUser = SessionPlayer.ResolveUserNum(session);
        if (_online?.FindByUserNum(request.LeaderUserNum) is not { } leader
            || !ReferenceEquals(_online.FindByUserNum(memberUser), session)
            || _parties?.PartyOf(memberUser) is not { } party || party.Leader.UserNum != request.LeaderUserNum
            || ReferenceEquals(leader, session)) return false;
        var group = new SceneGroup(leader, party, [session]);
        var source = _registry.TryGet(session.Id);
        var destination = _registry.TryGet(leader.Id);
        var leaderPhase = leader.TryFeature<IPlayerSession>(out var leaderPlayer) ? leaderPlayer.Phase : (SessionPhase?)null;
        var memberPhase = session.TryFeature<IPlayerSession>(out var memberPlayer) ? memberPlayer.Phase : (SessionPhase?)null;
        var arrived = false;
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        await _sceneTransitions.RunAsync([leader.Id, session.Id], async token =>
        {
            if (!FollowerIsCurrent(group, session) || memberPlayer?.Phase != memberPhase || leaderPlayer?.Phase != leaderPhase
                || !ReferenceEquals(source, _registry.TryGet(session.Id))
                || !ReferenceEquals(destination, _registry.TryGet(leader.Id))) return;
            if (source is not null && !ReferenceEquals(source, destination))
            {
                if (source.Phase != RoomPhase.Waiting || !PlazaBound) return;
                if (destination is not null)
                {
                    var admissible = false;
                    try
                    {
                        await destination.Executor.ExecuteAsync(_ =>
                        {
                            admissible = FollowerIsCurrent(group, session)
                                && ReferenceEquals(destination, _registry.TryGet(leader.Id))
                                && destination.Phase == RoomPhase.Waiting && !destination.IsFull
                                && destination.Password.Length == 0;
                            return ValueTask.CompletedTask;
                        }, token).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) when (_registry.Find(destination.Id) is null) { return; }
                    if (!admissible) return;
                }
                // Leave through the normal scene owner with a reserved plaza seat. Target admission is
                // checked again after leaving; a room that fills meanwhile never receives an extra member.
                await FollowRoomLeaveAsync(group, source, token).ConfigureAwait(false);
            }
            if (ReferenceEquals(destination, _registry.TryGet(session.Id)) && destination is not null)
            {
                arrived = true;
                return;
            }
            if (!CanFollowFromMenuOrPlaza(session)) return;
            if (destination is not null)
            {
                // An invitation conveys membership, not the password of an existing room.
                await FollowRoomAsync(group, destination, "", token).ConfigureAwait(false);
                arrived = ReferenceEquals(destination, _registry.TryGet(session.Id));
                return;
            }
            if (leaderPhase == SessionPhase.Plaza && _plaza.Find(leader.Id) is { Reserved: false })
                await FollowPlazaAsync(group, token).ConfigureAwait(false);
            else if (leaderPhase == SessionPhase.MainMenu && memberPlayer?.Phase == SessionPhase.Plaza)
                await ReturnMenuSceneAsync(session, new() { PartyTransition = true }, token).ConfigureAwait(false);
            arrived = memberPlayer?.Phase == leaderPhase
                && (leaderPhase == SessionPhase.MainMenu || _plaza.Find(session.Id) is { Reserved: false });
        }, queued.Token).ConfigureAwait(false);
        return arrived;
    }

    private async ValueTask RunSceneAsync(ISession session,
        Func<SceneGroup?, CancellationToken, ValueTask> operation, CancellationToken ct)
    {
        var group = CaptureSceneGroup(session);
        var originalRoom = _registry.TryGet(session.Id);
        var originalPhase = session.TryFeature<IPlayerSession>(out var player) ? player.Phase : (SessionPhase?)null;
        var sessions = group is null ? new[] { session.Id } : group.Followers.Select(member => member.Id).Prepend(session.Id).ToArray();
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        await _sceneTransitions.RunAsync(sessions, async token =>
        {
            if (session.Connection.IsClosed) return;
            if (_online is not null && !ReferenceEquals(_online.FindByUserNum(SessionPlayer.ResolveUserNum(session)), session))
            {
                _logger.LogInformation("Session {Session} scene request belongs to a replaced connection.", session.Id);
                return;
            }
            if (!ReferenceEquals(_registry.TryGet(session.Id), originalRoom)
                || (originalPhase is not null && player!.Phase != originalPhase))
            {
                _logger.LogInformation("Session {Session} scene request was overtaken by another scene move.", session.Id);
                return;
            }
            using var running = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
            await operation(GroupIsCurrent(group) ? group : null, running.Token).ConfigureAwait(false);
        }, queued.Token).ConfigureAwait(false);
    }

    private SceneGroup? CaptureSceneGroup(ISession leader)
    {
        var user = SessionPlayer.ResolveUserNum(leader);
        if (_online is null || !ReferenceEquals(_online.FindByUserNum(user), leader)
            || _parties?.PartyOf(user) is not { Members.Count: > 1 } party || party.Leader.UserNum != user)
            return null;
        var followers = party.Members.Skip(1).Select(member => _online.FindByUserNum(member.UserNum))
            .OfType<ISession>().Where(member => !member.Connection.IsClosed).ToArray();
        return followers.Length == 0 ? null : new SceneGroup(leader, party, followers);
    }

    private bool GroupIsCurrent(SceneGroup? group) => group is not null
        && !group.Leader.Connection.IsClosed
        && ReferenceEquals(_online?.FindByUserNum(group.Party.Leader.UserNum), group.Leader)
        && _parties?.PartyOf(group.Party.Leader.UserNum) is { } current
        && current.Members.SequenceEqual(group.Party.Members);

    private bool FollowerIsCurrent(SceneGroup group, ISession member) => GroupIsCurrent(group)
        && !member.Connection.IsClosed
        && ReferenceEquals(_online?.FindByUserNum(SessionPlayer.ResolveUserNum(member)), member);

    private bool CanFollowFromMenuOrPlaza(ISession member) => _registry.TryGet(member.Id) is null
        && member.TryFeature<IPlayerSession>(out var player)
        && player.Phase is SessionPhase.MainMenu or SessionPhase.Plaza;

    private ValueTask FollowPlazaAsync(SceneGroup? group, CancellationToken ct) =>
        ForFollowersAsync(group, async (member, token) =>
        {
            if (!CanFollowFromMenuOrPlaza(member) || _plaza.Find(member.Id) is not null) return;
            var (user, nickname) = SessionPlayer.RequireIdentity(member);
            var equipment = await SeatCharacter.ResolveEquipmentAsync(member, user, _selection, _characters, token).ConfigureAwait(false);
            await _plaza.Executor.ExecuteAsync(async operationCt =>
            {
                if (!FollowerIsCurrent(group!, member) || !CanFollowFromMenuOrPlaza(member)) return;
                if (!_plaza.HasSpaceFor(member.Id))
                {
                    _logger.LogInformation("Party member session {Session} stays in its scene because the plaza is full.", member.Id);
                    return;
                }
                await EnterPlazaCoreAsync(member, user, nickname, equipment, operationCt, true).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }, ct);

    private async ValueTask ForFollowersAsync(SceneGroup? group, Func<ISession, CancellationToken, ValueTask> follow, CancellationToken ct)
    {
        if (group is null) return;
        foreach (var member in group.Followers)
        {
            if (!FollowerIsCurrent(group, member)) continue;
            try { await follow(member, ct).ConfigureAwait(false); }
            catch (IOException error)
            {
                _logger.LogWarning(error, "Party scene move failed for session {Session}; closing its connection.", member.Id);
                await member.Connection.CloseAsync("Party scene transition failed.").ConfigureAwait(false);
            }
        }
    }

    private ValueTask FollowRoomAsync(SceneGroup? group, PlayRoom room, string password, CancellationToken ct) =>
        ForFollowersAsync(group, async (member, token) =>
        {
            if (!CanFollowFromMenuOrPlaza(member)) return;
            var (user, nickname) = SessionPlayer.RequireIdentity(member);
            var equipment = await SeatCharacter.ResolveEquipmentAsync(member, user, _selection, _characters, token).ConfigureAwait(false);
            try
            {
                await room.Executor.ExecuteAsync(async operationCt =>
                {
                    if (!FollowerIsCurrent(group!, member) || !CanFollowFromMenuOrPlaza(member)
                        || !ReferenceEquals(_registry.TryGet(group!.Leader.Id), room)) return;
                    await JoinCoreAsync(member, new RoomJoinRequested(room.Id, password), room, user, nickname, equipment,
                        operationCt, notifyRefusal: false, partyTransition: true).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (_registry.Find(room.Id) is null) { return; }
            if (ReferenceEquals(_registry.TryGet(member.Id), room))
            {
                if (PlazaBound) await LeavePlazaForRoomAsync(member, token).ConfigureAwait(false);
            }
            else
                _logger.LogInformation("Party member session {Session} could not follow into room {Room}; its previous scene is retained.", member.Id, room.Id);
        }, ct);

    private ValueTask FollowRoomLeaveAsync(SceneGroup? group, PlayRoom source, CancellationToken ct) =>
        ForFollowersAsync(group, async (member, token) =>
        {
            if (!ReferenceEquals(_registry.TryGet(member.Id), source) || source.Phase != RoomPhase.Waiting) return;
            var (user, nickname) = SessionPlayer.RequireIdentity(member);
            var equipment = await SeatCharacter.ResolveEquipmentAsync(member, user, _selection, _characters, token).ConfigureAwait(false);
            var reserved = false;
            await _plaza.Executor.ExecuteAsync(_ =>
            {
                if (FollowerIsCurrent(group!, member) && _plaza.HasSpaceFor(member.Id))
                {
                    // Keep admission reserved but invisible until the entrant's plaza callback installs its scene.
                    _plaza.Enter(member, user, nickname, equipment, reserved: true);
                    reserved = true;
                }
                return ValueTask.CompletedTask;
            }, token).ConfigureAwait(false);
            if (!reserved)
            {
                _logger.LogInformation("Party member session {Session} retains its room because no plaza seat is available.", member.Id);
                return;
            }
            try { await LeaveSceneAsync(member, true, token, group, source).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (_registry.Find(source.Id) is null) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && _registry.Find(source.Id) is null) { }
            finally
            {
                if (_plaza.Find(member.Id)?.Reserved == true)
                    await _plaza.Executor.ExecuteAsync(_ =>
                    {
                        _plaza.Remove(member.Id);
                        return ValueTask.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        }, ct);

    private async ValueTask NotifyScenePeerAsync<T>(ISession recipient, IMessageSender<T> sender, T message, CancellationToken ct)
        where T : IMessage
    {
        if (recipient.Connection.IsClosed) return;
        try { await sender.SendAsync(recipient, message, ct).ConfigureAwait(false); }
        catch (IOException error)
        {
            _logger.LogWarning(error, "Scene notification failed for session {Session}; closing its connection.", recipient.Id);
            await recipient.Connection.CloseAsync("Scene notification failed.").ConfigureAwait(false);
        }
    }

    internal async Task StopScenesAsync()
    {
        await _sceneTransitions.StopAsync().ConfigureAwait(false);
        await _plaza.Executor.StopAsync().ConfigureAwait(false);
    }
}
