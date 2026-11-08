using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal IRoomBrowser? Browser { get; set; }
    internal BubbleFighter.Fantasy.Contracts.Cluster.IRemoteRoomMigration? Migration { get; set; }

    private async ValueTask ListBrowserAsync(ISession session, RoomListRequested request,
        IMessageSender<RoomListSnapshot> sender, CancellationToken ct)
    {
        var browser = Browser!;
        IReadOnlyList<RoomListEntry> rows;
        try
        {
            rows = await browser.ListAsync(session, _registry.SnapshotListedRooms(matchRoomsOnly: false, ct), ct)
                .ConfigureAwait(false);
        }
        catch (RoomBrowserUnavailableException error)
        {
            browser.Invalidate(session);
            _logger.LogWarning(error, "Session {Session} could not refresh its cluster room list.", session.Id);
            rows = [];
        }
        try { await sender.SendAsync(session, new(request.Cursor, rows), ct).ConfigureAwait(false); }
        catch
        {
            browser.Invalidate(session);
            throw;
        }
    }

    private async ValueTask JoinBrowserAsync(ISession session, RoomJoinRequested request, SceneGroup? group, CancellationToken ct)
    {
        RoomBrowserSelection? selection;
        try { selection = await Browser!.ResolveAsync(session, request.RoomId, ct).ConfigureAwait(false); }
        catch (RoomBrowserUnavailableException error)
        {
            _logger.LogWarning(error, "Session {Session} could not revalidate room alias {Alias}.", session.Id, request.RoomId);
            selection = null;
        }
        if (selection is { Local: false, JoinAllowed: true } && Migration is not null && _registry.TryGet(session.Id) is null)
        {
            var refusal = await Migration.BeginAsync(session, selection, request.Password, ct).ConfigureAwait(false);
            if (refusal is { } reason) await RefuseAsync(session, request.RoomId, reason, ct).ConfigureAwait(false);
            return;
        }
        if (selection is not { Local: true })
        {
            if (selection is not null)
                _logger.LogInformation("Session {Session} selected a remote room without an authenticated transfer binding.", session.Id);
            await RefuseAsync(session, request.RoomId, RoomJoinRefusal.NoRoom, ct).ConfigureAwait(false);
            return;
        }
        await JoinSceneAsync(session, request with { RoomId = selection.RoomId }, group, ct, selection.InstanceId)
            .ConfigureAwait(false);
    }
}
