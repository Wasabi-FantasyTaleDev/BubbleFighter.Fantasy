using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal CustomGameRulesCatalog? CustomRules { get; set; }
    internal IMessageSender<RoomCustomRulesReplied>? CustomRulesReply { get; set; }

    /// <summary>Validates and commits a waiting master's custom options before acknowledging their installation.</summary>
    public async ValueTask HandleCustomRulesChangeAsync(ISession session, RoomCustomRulesChangeRequested request, CancellationToken ct)
    {
        var catalog = CustomRules ?? throw new InvalidOperationException("Custom room rules require a native catalogue.");
        var reply = CustomRulesReply ?? throw new InvalidOperationException("Custom room rules require a reply sender.");
        var sender = _settingsChanged ?? throw new InvalidOperationException("Custom room rules require a settings sender.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Lineage);
        ArgumentNullException.ThrowIfNull(request.Values);
        // kr-1256 sub_1138B20 defines 35 native selectors. Copy the complete request before queuing room work.
        if (request.Values.Count > 35 || request.Values.Any(row => row is null || row.Values is null || row.Values.Count > 35))
            throw new ArgumentException("The custom rule vector exceeds the native controls.", nameof(request));
        var values = Array.AsReadOnly(request.Values.Select(row => new RoomCustomRuleValue(row.ValueId,
            Array.AsReadOnly(row.Values.ToArray()))).ToArray());
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members = [];
            RoomSettingsChanged? changed = null;
            ushort result = 0;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, session) || session.Connection.IsClosed
                    || seat.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting)
                    result = 1;
                else
                {
                    var held = room.BuildSettings ?? new RoomBuildSettings(request.Lineage, default) { Mode = room.ModeId };
                    try
                    {
                        if (held.Lineage != request.Lineage) throw new ArgumentException("Custom options require the room's native lineage.");
                        catalog.Validate(room.ModeId, values);
                        if (request.MatchesModeDefaults != catalog.MatchesModeDefaults(room.ModeId, values))
                            throw new ArgumentException("The custom rule default marker disagrees with its native vector.");
                        room.BuildSettings = held with { CustomRules = values };
                        members = [.. room.Seats];
                        changed = new(View(room));
                    }
                    catch (ArgumentException) { result = 2; }
                }
            }
            // kr-1256 sub_9DB8A0 installs complete settings; kr-1256 sub_9D2A60 only displays status 0 for the master or 4 for peers.
            if (changed is not null)
            {
                foreach (var member in members)
                    await NotifyScenePeerAsync(member.Game, sender, changed, token).ConfigureAwait(false);
                foreach (var member in members)
                    await NotifyScenePeerAsync(member.Game, reply,
                        new(ReferenceEquals(member.Game, session) ? (ushort)0 : (ushort)4), token).ConfigureAwait(false);
            }
            else
                await NotifyScenePeerAsync(session, reply, new(result), token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private void ResetCustomRules(PlayRoom room)
    {
        if (CustomRules is { } catalog && room.BuildSettings is { } settings)
            room.BuildSettings = settings with { CustomRules = catalog.InitialValues(room.ModeId) };
    }
}
