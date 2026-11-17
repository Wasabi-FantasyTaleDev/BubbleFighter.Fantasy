using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Clans, through typed messages on two connections of the same client. On the game connection the build's
/// dialect row decodes the three empty C2S requests (<see cref="ClanServerAckReceived"/>,
/// <see cref="ClanMainOpened"/>, <see cref="ClanAttendanceCheckedIn"/>) and writes the passport, the empty open
/// snapshots and the attendance state. On the clan-server connection, where the build has one, this row runs
/// the login handshake: it greets until the client logs in, checks the passport the client echoes back against
/// the one it issued, and ends the clan connection when the game session behind it ends. The build is selected
/// by the row's protocol and packet tables, not by this plugin.
/// </summary>
[Plugin("clan", Category = "social", Names = ["en=Clans", "zh-Hans=公会", "zh-Hant=公會", "ko=클랜"], 
    Description = "Clan passport, mark list, guild info, and attendance. Search and roster rows are not invented.",
    Requires = [typeof(IMessageRouter), typeof(IClanStore), typeof(IClanDirectory)],
    // Needed where the build has the clan connection's create request: the card table and the stored bag it consumes from.
    // IClanConnections (the clan.connections row) is needed where the build has the clan connection: it is the node's
    // table of logged-in clan connections that every build's row shares, so clan members on different builds hear each other.
    Optional = [typeof(IClanCreationCards), typeof(IInventoryRepository), typeof(IClanConnections)],
    OptionalMany =
    [
        typeof(IInboundMessageBinding<ClanServerAckReceived>), typeof(IInboundMessageBinding<ClanMainOpened>),
        typeof(IInboundMessageBinding<ClanAttendanceCheckedIn>), typeof(IMessageSender<ClanOpened>),
        typeof(IMessageSender<ClanAttendanceStateUpdated>), typeof(IMessageSender<ClanPassportIssued>),
        typeof(IInboundMessageBinding<ClanServerHeartbeatReceived>), typeof(IInboundMessageBinding<ClanServerLoginRequested>),
        typeof(IMessageSender<ClanServerHello>), typeof(IMessageSender<ClanServerLoginAnswered>),
        typeof(IInboundMessageBinding<ClanHomeRequested>), typeof(IInboundMessageBinding<ClanProfileRequested>), typeof(IInboundMessageBinding<ClanCreateRequested>),
        typeof(IMessageSender<ClanHomeSent>), typeof(IMessageSender<ClanProfileSent>), typeof(IMessageSender<ClanCreateAnswered>),
        typeof(IInboundMessageBinding<ClanRosterRequested>), typeof(IInboundMessageBinding<ClanMemberDetailsRequested>),
        typeof(IInboundMessageBinding<ClanMemberReported>), typeof(IMessageSender<ClanRosterSent>), typeof(IMessageSender<ClanMemberDetailsSent>),
        typeof(IInboundMessageBinding<ClanSearchRequested>), typeof(IMessageSender<ClanSearchSent>),
        typeof(IInboundMessageBinding<ClanApplyRequested>), typeof(IInboundMessageBinding<ClanJoinRequested>),
        typeof(IInboundMessageBinding<ClanCancelApplicationRequested>), typeof(IInboundMessageBinding<ClanApplicantsRequested>),
        typeof(IMessageSender<ClanApplyAnswered>), typeof(IMessageSender<ClanCancelApplicationAnswered>),
        typeof(IMessageSender<ClanApplicantsSent>),
        typeof(IInboundMessageBinding<ClanMemberChangeRequested>), typeof(IInboundMessageBinding<ClanLeaveRequested>),
        typeof(IMessageSender<ClanMemberChangeAnswered>), typeof(IMessageSender<ClanMemberChanged>), typeof(IMessageSender<ClanMemberLeft>),
        typeof(IInboundMessageBinding<ClanIntroductionEditRequested>), typeof(IInboundMessageBinding<ClanNoticeEditRequested>),
        typeof(IInboundMessageBinding<ClanRecruitmentEditRequested>), typeof(IInboundMessageBinding<ClanMarkEditRequested>),
        typeof(IMessageSender<ClanIntroductionEditAnswered>), typeof(IMessageSender<ClanIntroductionChanged>),
        typeof(IMessageSender<ClanNoticeEditAnswered>), typeof(IMessageSender<ClanNoticeChanged>),
        typeof(IMessageSender<ClanRecruitmentEditAnswered>), typeof(IMessageSender<ClanMarkChanged>),
        typeof(IInboundMessageBinding<ClanTransferRequested>), typeof(IInboundMessageBinding<ClanTransferAnswerRequested>),
        typeof(IInboundMessageBinding<ClanTransferConfirmed>), typeof(IMessageSender<ClanTransferRequestAnswered>),
        typeof(IMessageSender<ClanTransferOffered>), typeof(IMessageSender<ClanTransferAnswerAnswered>),
        typeof(IMessageSender<ClanTransferAnswered>), typeof(IMessageSender<ClanMasterChanged>),
        typeof(IInboundMessageBinding<ClanDisbandRequested>), typeof(IMessageSender<ClanDisbandAnswered>), typeof(IMessageSender<ClanDisbandChanged>),
        typeof(IInboundMessageBinding<ClanBoardRequested>), typeof(IInboundMessageBinding<ClanBoardWriteRequested>),
        typeof(IInboundMessageBinding<ClanBoardDeleteRequested>), typeof(IMessageSender<ClanBoardSent>),
        typeof(IMessageSender<ClanBoardWriteAnswered>), typeof(IMessageSender<ClanBoardDeleted>),
        typeof(IInboundMessageBinding<ClanChatRequested>), typeof(IInboundMessageBinding<ClanWhisperRequested>),
        typeof(IMessageSender<ClanChatReceived>), typeof(IMessageSender<ClanWhisperAnswered>), typeof(IMessageSender<ClanWhisperReceived>),
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
        var passport = RequireSender<ClanPassportIssued>(context, protocol);

        var passports = new ClanPassports(options.Authentication, TimeSpan.FromSeconds(options.TicketSeconds),
            options.MaxTrackedOpens, TimeProvider.System);
        context.Effect(passports);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => passports.RemoveConnection(e.Connection.Id)));

        var handlers = new Handlers(store, opens, passports, options.MaxSnapshotEntries, opened, attendance, passport, context.Logger);
        context.Effect(messages.For(context).On<ClanServerAckReceived>(protocol, handlers.ServerAckReceivedAsync));
        if (options.PassportOnLogin)
            context.Effect(context.Events.OnAsync<LoginMenuInstalled>(async (login, ct) =>
            {
                if (login.Protocol == protocol)
                    await handlers.ServerAckReceivedAsync(login.Session, new ClanServerAckReceived(), ct).ConfigureAwait(false);
            }));
        context.Effect(messages.For(context).On<ClanMainOpened>(protocol, handlers.MainOpenedAsync));
        context.Effect(messages.For(context).On<ClanAttendanceCheckedIn>(protocol, handlers.AttendanceCheckedInAsync));

        // The clan-server connection: mounted by the protocol row and bound by the dialect row only where the
        // build's opcode table has clan-connection rows, so a build without them runs the game-port part alone.
        var clanProtocol = OpcodeNames.ParseProtocol(protocol).Profile + "/clan";
        var hello = context.Services.GetAll<IMessageSender<ClanServerHello>>().SingleOrDefault(sender => sender.Protocol == clanProtocol);
        if (hello is null)
        {
            context.Logger.LogInformation(
                "Clan on {Protocol}: game-connection requests only; this build's opcode table has no clan-connection rows.", protocol);
            return ValueTask.CompletedTask;
        }
        RequireBinding<ClanServerHeartbeatReceived>(context, clanProtocol);
        RequireBinding<ClanServerLoginRequested>(context, clanProtocol);
        var connections = RequireService<IClanConnections>(context, "the clan.connections row");
        var delivery = new ClanDelivery(context, clanProtocol);
        context.Effect(() => connections.Release(delivery));
        var hub = new ClanHub(connections, delivery);
        var connection = new ClanConnectionHandlers(passports, hub, hello, RequireSender<ClanServerLoginAnswered>(context, clanProtocol), context.Logger);
        context.Effect(messages.For(context).On<ClanServerHeartbeatReceived>(clanProtocol, connection.HeartbeatAsync));
        context.Effect(messages.For(context).On<ClanServerLoginRequested>(clanProtocol, connection.LoginAsync));
        // The clan packets after login, where the build's dialect row binds them (its opcode table names them).
        if (context.Services.GetAll<IMessageSender<ClanHomeSent>>().Any(sender => sender.Protocol == clanProtocol))
        {
            var clans = context.Services.Get<IClanDirectory>();
            RequireBinding<ClanHomeRequested>(context, clanProtocol);
            RequireBinding<ClanProfileRequested>(context, clanProtocol);
            RequireBinding<ClanCreateRequested>(context, clanProtocol);
            var directory = new ClanDirectoryHandlers(clans, RequireService<IClanCreationCards>(context, "the clan.creation-cards row"),
                RequireService<IInventoryRepository>(context, "a persistence row"), RequireSender<ClanHomeSent>(context, clanProtocol),
                RequireSender<ClanProfileSent>(context, clanProtocol), RequireSender<ClanCreateAnswered>(context, clanProtocol), context.Logger);
            context.Effect(messages.For(context).On<ClanHomeRequested>(clanProtocol, directory.HomeAsync));
            context.Effect(messages.For(context).On<ClanProfileRequested>(clanProtocol, directory.ProfileAsync));
            context.Effect(messages.For(context).On<ClanCreateRequested>(clanProtocol, directory.CreateAsync));
            RequireBinding<ClanRosterRequested>(context, clanProtocol);
            RequireBinding<ClanMemberDetailsRequested>(context, clanProtocol);
            RequireBinding<ClanMemberReported>(context, clanProtocol);
            var rosters = new ClanRosterHandlers(clans, hub, RequireSender<ClanRosterSent>(context, clanProtocol),
                RequireSender<ClanMemberDetailsSent>(context, clanProtocol), context.Logger);
            context.Effect(messages.For(context).On<ClanRosterRequested>(clanProtocol, rosters.RosterAsync));
            context.Effect(messages.For(context).On<ClanMemberDetailsRequested>(clanProtocol, rosters.DetailsAsync));
            context.Effect(messages.For(context).On<ClanMemberReported>(clanProtocol, rosters.ReportAsync));
            RequireBinding<ClanSearchRequested>(context, clanProtocol);
            var search = new ClanSearchHandlers(clans, RequireSender<ClanSearchSent>(context, clanProtocol), context.Logger);
            context.Effect(messages.For(context).On<ClanSearchRequested>(clanProtocol, search.SearchAsync));
            RequireBinding<ClanApplyRequested>(context, clanProtocol);
            RequireBinding<ClanJoinRequested>(context, clanProtocol);
            RequireBinding<ClanCancelApplicationRequested>(context, clanProtocol);
            RequireBinding<ClanApplicantsRequested>(context, clanProtocol);
            var applications = new ClanApplicationHandlers(clans, RequireSender<ClanApplyAnswered>(context, clanProtocol),
                RequireSender<ClanCancelApplicationAnswered>(context, clanProtocol), RequireSender<ClanApplicantsSent>(context, clanProtocol),
                context.Logger);
            context.Effect(messages.For(context).On<ClanApplyRequested>(clanProtocol, applications.ApplyAsync));
            context.Effect(messages.For(context).On<ClanJoinRequested>(clanProtocol, applications.JoinAsync));
            context.Effect(messages.For(context).On<ClanCancelApplicationRequested>(clanProtocol, applications.CancelAsync));
            context.Effect(messages.For(context).On<ClanApplicantsRequested>(clanProtocol, applications.ApplicantsAsync));
            RequireBinding<ClanMemberChangeRequested>(context, clanProtocol);
            RequireBinding<ClanLeaveRequested>(context, clanProtocol);
            var changes = new ClanMemberHandlers(clans, hub, RequireSender<ClanMemberChangeAnswered>(context, clanProtocol),
                RequireSender<ClanMemberChanged>(context, clanProtocol), RequireSender<ClanMemberLeft>(context, clanProtocol), context.Logger);
            context.Effect(messages.For(context).On<ClanMemberChangeRequested>(clanProtocol, changes.ChangeAsync));
            context.Effect(messages.For(context).On<ClanLeaveRequested>(clanProtocol, changes.LeaveAsync));
            RequireBinding<ClanIntroductionEditRequested>(context, clanProtocol);
            RequireBinding<ClanNoticeEditRequested>(context, clanProtocol);
            RequireBinding<ClanRecruitmentEditRequested>(context, clanProtocol);
            RequireBinding<ClanMarkEditRequested>(context, clanProtocol);
            var edits = new ClanEditHandlers(clans, hub, new ClanEditHandlers.Senders(
                RequireSender<ClanIntroductionEditAnswered>(context, clanProtocol), RequireSender<ClanIntroductionChanged>(context, clanProtocol),
                RequireSender<ClanNoticeEditAnswered>(context, clanProtocol), RequireSender<ClanNoticeChanged>(context, clanProtocol),
                RequireSender<ClanRecruitmentEditAnswered>(context, clanProtocol), RequireSender<ClanMarkChanged>(context, clanProtocol)),
                context.Logger);
            context.Effect(messages.For(context).On<ClanIntroductionEditRequested>(clanProtocol, edits.IntroductionAsync));
            context.Effect(messages.For(context).On<ClanNoticeEditRequested>(clanProtocol, edits.NoticeAsync));
            context.Effect(messages.For(context).On<ClanRecruitmentEditRequested>(clanProtocol, edits.RecruitmentAsync));
            context.Effect(messages.For(context).On<ClanMarkEditRequested>(clanProtocol, edits.MarkAsync));
            RequireBinding<ClanTransferRequested>(context, clanProtocol);
            RequireBinding<ClanTransferAnswerRequested>(context, clanProtocol);
            RequireBinding<ClanTransferConfirmed>(context, clanProtocol);
            var transfers = new ClanTransferHandlers(clans, hub, new ClanTransferHandlers.Senders(
                RequireSender<ClanTransferRequestAnswered>(context, clanProtocol), RequireSender<ClanTransferOffered>(context, clanProtocol),
                RequireSender<ClanTransferAnswerAnswered>(context, clanProtocol), RequireSender<ClanTransferAnswered>(context, clanProtocol),
                RequireSender<ClanMasterChanged>(context, clanProtocol)), context.Logger);
            context.Effect(messages.For(context).On<ClanTransferRequested>(clanProtocol, transfers.RequestAsync));
            context.Effect(messages.For(context).On<ClanTransferAnswerRequested>(clanProtocol, transfers.AnswerAsync));
            context.Effect(messages.For(context).On<ClanTransferConfirmed>(clanProtocol, transfers.ConfirmAsync));
            RequireBinding<ClanDisbandRequested>(context, clanProtocol);
            var disbands = new ClanDisbandHandlers(clans, hub, RequireSender<ClanDisbandAnswered>(context, clanProtocol),
                RequireSender<ClanDisbandChanged>(context, clanProtocol), RequireSender<ClanHomeSent>(context, clanProtocol),
                TimeSpan.FromDays(options.DisbandDays), TimeProvider.System, context.Logger);
            context.Effect(messages.For(context).On<ClanDisbandRequested>(clanProtocol, disbands.DisbandAsync));
            context.Effect(disbands.Start(TimeSpan.FromSeconds(options.DisbandCheckSeconds)));
            RequireBinding<ClanBoardRequested>(context, clanProtocol);
            RequireBinding<ClanBoardWriteRequested>(context, clanProtocol);
            RequireBinding<ClanBoardDeleteRequested>(context, clanProtocol);
            var board = new ClanBoardHandlers(clans, RequireSender<ClanBoardSent>(context, clanProtocol),
                RequireSender<ClanBoardWriteAnswered>(context, clanProtocol), RequireSender<ClanBoardDeleted>(context, clanProtocol),
                options.BoardPageSize, context.Logger);
            context.Effect(messages.For(context).On<ClanBoardRequested>(clanProtocol, board.ListAsync));
            context.Effect(messages.For(context).On<ClanBoardWriteRequested>(clanProtocol, board.WriteAsync));
            context.Effect(messages.For(context).On<ClanBoardDeleteRequested>(clanProtocol, board.DeleteAsync));
            RequireBinding<ClanChatRequested>(context, clanProtocol);
            RequireBinding<ClanWhisperRequested>(context, clanProtocol);
            var chat = new ClanChatHandlers(clans, hub, RequireSender<ClanChatReceived>(context, clanProtocol),
                RequireSender<ClanWhisperAnswered>(context, clanProtocol), RequireSender<ClanWhisperReceived>(context, clanProtocol), context.Logger);
            context.Effect(messages.For(context).On<ClanChatRequested>(clanProtocol, chat.ChatAsync));
            context.Effect(messages.For(context).On<ClanWhisperRequested>(clanProtocol, chat.WhisperAsync));
        }
        context.Logger.LogInformation(
            "Clan on {Protocol} with the clan connection {ClanProtocol}; login authentication {Authentication}.",
            protocol, clanProtocol, options.Authentication ? "on" : "off");
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

    private static T RequireService<T>(IPluginContext context, string provider) where T : class =>
        context.Services.TryGet<T>(out var service) && service is not null
            ? service
            : throw new ConfigException($"Clan creation on the clan connection requires {typeof(T).Name} from {provider}.");

    private static IMessageSender<T> RequireSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Clan requires exactly one IMessageSender<{typeof(T).Name}> for '{protocol}'.");
    }

    private sealed class Handlers(
        IClanStore store,
        ClanOpenScope opens,
        ClanPassports passports,
        int maxSnapshotEntries,
        IMessageSender<ClanOpened> opened,
        IMessageSender<ClanAttendanceStateUpdated> attendance,
        IMessageSender<ClanPassportIssued> passport,
        ILogger logger)
    {
        public async ValueTask ServerAckReceivedAsync(ISession session, ClanServerAckReceived _, CancellationToken ct)
        {
            // The clan connection greets from the moment the client starts, so this request also arrives before
            // the game login. There is no account to state yet: nothing is sent, and the clan connection greets
            // again on its next heartbeat.
            if (!session.TryFeature<IPlayerSession>(out var player))
            {
                logger.LogDebug("Session {Id} asked for a clan passport before logging in; nothing sent.", session.Id);
                return;
            }
            var identity = player.Identity;
            var record = new ClanOpenRecord(identity.UserNum, ClanOpenKind.ClanServerAck);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            var ticket = passports.Issue(session, identity.UserNum);
            logger.LogInformation("Session {Id} clan passport issued for user={User} ({TicketBytes} ticket bytes).",
                session.Id, identity.UserNum, ticket.Length);
            await passport.SendAsync(session, new ClanPassportIssued(identity.UserNum, identity.Nickname, ticket), ct).ConfigureAwait(false);
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
                "Session {Id} C2S 2040 gcpReqClanMainOpen_ user={User} clanId={Clan} storedClans={Count}. Replying the empty mark list and guild info.",
                session.Id, userNum, membership.ClanId, clans.Count);
            await opened.SendAsync(session, new ClanOpened(), ct).ConfigureAwait(false);
        }

        public async ValueTask AttendanceCheckedInAsync(ISession session, ClanAttendanceCheckedIn _, CancellationToken ct)
        {
            var record = new ClanOpenRecord(TryGetUserNum(session), ClanOpenKind.Attendance);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} C2S 2041 gcpReqClanAttendanceCheckIn_ user={User}. Replying S2C 1508 with an empty attendance card list.",
                session.Id, record.UserNum);
            await attendance.SendAsync(session, new ClanAttendanceStateUpdated(), ct).ConfigureAwait(false);
        }
    }
}
