using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Clan / guild / crew info and attendance on the game connection, through typed messages: the build's dialect
/// row decodes the three empty C2S requests (<see cref="ClanServerAckReceived"/>, <see cref="ClanMainOpened"/>,
/// <see cref="ClanAttendanceCheckedIn"/>) and writes the empty open snapshots and attendance state
/// (<see cref="ClanOpened"/>, <see cref="ClanAttendanceStateUpdated"/>). This row remembers the open and answers
/// it; the build is selected by the row's protocol and packet tables, not by this plugin. Search-list and roster
/// bodies are not invented: those counted rows are filled by clan-server NxPacket opcodes <c>0x3FC</c> /
/// <c>0x3F4</c> / <c>0x3F6</c>, not by this dialect. Join and leave have no proven game-port C2S builder; the
/// store therefore stays empty until a later dialect adds those writers.
/// </summary>
[Plugin("clan",
    Description = "Clan passport, mark list, guild info, and attendance. Search and roster rows are not invented.",
    Requires = [typeof(IMessageRouter), typeof(IClanStore)],
    OptionalMany =
    [
        typeof(IInboundMessageBinding<ClanServerAckReceived>), typeof(IInboundMessageBinding<ClanMainOpened>),
        typeof(IInboundMessageBinding<ClanAttendanceCheckedIn>), typeof(IMessageSender<ClanOpened>),
        typeof(IMessageSender<ClanAttendanceStateUpdated>),
    ])]
public sealed class ClanPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ClanOptions>();
        var messages = context.Services.Get<IMessageRouter>();
        var store = context.Services.Get<IClanStore>();
        context.Logger.LogInformation("Clan using {Store} for opens and membership.", store.GetType().Name);

        var opens = new ClanOpenScope(options.MaxTrackedOpens);
        context.Effect(opens);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => opens.Remove(e.Connection.Id)));

        // Resolve the adapters this row consumes before registering handlers: a row mounted without the
        // clan dialect row fails at activation with its name, not on the first client frame.
        var protocol = options.Protocol;
        RequireBinding<ClanServerAckReceived>(context, protocol);
        RequireBinding<ClanMainOpened>(context, protocol);
        RequireBinding<ClanAttendanceCheckedIn>(context, protocol);
        var opened = RequireSender<ClanOpened>(context, protocol);
        var attendance = RequireSender<ClanAttendanceStateUpdated>(context, protocol);

        var handlers = new Handlers(store, opens, options.MaxSnapshotEntries, opened, attendance, context.Logger);
        context.Effect(messages.For(context).On<ClanServerAckReceived>(protocol, handlers.ServerAckReceivedAsync));
        context.Effect(messages.For(context).On<ClanMainOpened>(protocol, handlers.MainOpenedAsync));
        context.Effect(messages.For(context).On<ClanAttendanceCheckedIn>(protocol, handlers.AttendanceCheckedInAsync));
        context.Logger.LogInformation(
            "Clan dialect on {Protocol}: typed open snapshots and attendance; search and roster rows stay uninvented.",
            protocol);
        return ValueTask.CompletedTask;
    }

    /// <summary>The logged-in player's id, or 0 when the session has no player.</summary>
    /// <param name="session">The live session.</param>
    public static long TryGetUserNum(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player.Identity.UserNum : 0;

    private static void RequireBinding<T>(IPluginContext context, string protocol) where T : IMessage
    {
        if (context.Services.GetAll<IInboundMessageBinding<T>>().Count(binding => binding.Protocol == protocol) != 1)
            throw new ConfigException($"Clan requires exactly one IInboundMessageBinding<{typeof(T).Name}> for '{protocol}'.");
    }

    private static IMessageSender<T> RequireSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Clan requires exactly one IMessageSender<{typeof(T).Name}> for '{protocol}'.");
    }

    private sealed class Handlers(
        IClanStore store,
        ClanOpenScope opens,
        int maxSnapshotEntries,
        IMessageSender<ClanOpened> opened,
        IMessageSender<ClanAttendanceStateUpdated> attendance,
        ILogger logger)
    {
        public async ValueTask ServerAckReceivedAsync(ISession session, ClanServerAckReceived _, CancellationToken ct)
        {
            var record = new ClanOpenRecord(TryGetUserNum(session), ClanOpenKind.ClanServerAck);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} C2S 25 Unknown25 (clan-server 0x3EA bounce) user={User}. No game-port reply is proven.",
                session.Id, record.UserNum);
        }

        public async ValueTask MainOpenedAsync(ISession session, ClanMainOpened _, CancellationToken ct)
        {
            var userNum = TryGetUserNum(session);
            var record = new ClanOpenRecord(userNum, ClanOpenKind.MainOpen);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            var membership = await store.GetMembershipAsync(userNum, ct).ConfigureAwait(false);
            var clans = await store.ListClanIdsAsync(maxSnapshotEntries, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} C2S 2040 Unknown2040 user={User} clanId={Clan} storedClans={Count}. Replying S2C 21/140/918 empty snapshots; search/roster rows stay uninvented.",
                session.Id, userNum, membership.ClanId, clans.Count);
            await opened.SendAsync(session, new ClanOpened(), ct).ConfigureAwait(false);
        }

        public async ValueTask AttendanceCheckedInAsync(ISession session, ClanAttendanceCheckedIn _, CancellationToken ct)
        {
            var record = new ClanOpenRecord(TryGetUserNum(session), ClanOpenKind.Attendance);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} C2S 2041 Unknown2041 user={User}. Replying S2C 1508 with an empty attendance card list.",
                session.Id, record.UserNum);
            await attendance.SendAsync(session, new ClanAttendanceStateUpdated(), ct).ConfigureAwait(false);
        }
    }
}
