using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    /// <summary>Commits a waiting room master's round selection and publishes the retained settings to its members.</summary>
    public async ValueTask HandleRoundSettingsChangeAsync(ISession session, RoomRoundSettingsChangeRequested request, CancellationToken ct)
    {
        // kr-812 sub_5BD770 and kr-1256 sub_969730 select only the three native round-map controls.
        ArgumentOutOfRangeException.ThrowIfNegative(request.Index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(request.Index, 3);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Lineage);
        if (request.Bytes.IsEmpty || request.Bytes.Length > ushort.MaxValue)
            throw new ArgumentException("A native round selection requires a nonempty bounded body.", nameof(request));
        var selection = new RoomRoundSelection(request.Index, request.Bytes.ToArray());
        var sender = _settingsChanged
            ?? throw new InvalidOperationException("Round settings changes require a settings notification sender.");
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members;
            RoomSettingsChanged changed;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || seat.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                {
                    _logger.LogWarning("Session {Id} changed round settings without waiting-room master authority in room {Room}; ignored.", session.Id, room.Id);
                    return;
                }
                var held = room.BuildSettings ?? new RoomBuildSettings(request.Lineage, default) { Mode = room.ModeId };
                if (held.Lineage != request.Lineage)
                    throw new InvalidOperationException("Round settings require the room's retained native lineage.");
                var selections = held.RoundSelections.Where(value => value.Index != request.Index)
                    .Append(selection)
                    .OrderBy(value => value.Index).ToArray();
                if (selections.Length > 3)
                    throw new InvalidOperationException("The retained native round selection count exceeds three.");
                room.BuildSettings = held with { RoundSelections = selections };
                members = [.. room.Seats];
                changed = new(View(room));
            }
            foreach (var member in members)
                await NotifyScenePeerAsync(member.Game, sender, changed, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
