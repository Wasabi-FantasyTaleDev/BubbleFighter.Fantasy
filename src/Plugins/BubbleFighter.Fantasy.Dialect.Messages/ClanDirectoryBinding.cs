using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The clan-server connection's clan packets after login (layouts in <c>packets/clan-server.yaml</c>, numbers from
/// the build's <c>connection: clan</c> opcode rows by name). The dispatcher that consumes the server frames is
/// kr-1256 sub_525170; each layout names its own reader. Every packet of this connection follows one of the three
/// shapes here: an empty request, a request with fields, and a reply written from a field map. No frame is larger
/// than the build's clan packet object (<c>clan_packet_bytes</c> in packets/builds.yaml): a board page or an applicant
/// list holds the entries that fit, and any other frame that would not fit is refused before it is sent.
/// </summary>
internal sealed class ClanDirectoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build, int packetBytes)
    : IInboundMessageBinding<ClanHomeRequested>, IInboundMessageBinding<ClanProfileRequested>,
      IInboundMessageBinding<ClanCreateRequested>, IInboundMessageBinding<ClanRosterRequested>,
      IInboundMessageBinding<ClanMemberDetailsRequested>, IInboundMessageBinding<ClanMemberReported>,
      IInboundMessageBinding<ClanSearchRequested>, IInboundMessageBinding<ClanApplyRequested>,
      IInboundMessageBinding<ClanJoinRequested>, IInboundMessageBinding<ClanCancelApplicationRequested>,
      IInboundMessageBinding<ClanApplicantsRequested>, IInboundMessageBinding<ClanMemberChangeRequested>,
      IInboundMessageBinding<ClanLeaveRequested>, IInboundMessageBinding<ClanIntroductionEditRequested>,
      IInboundMessageBinding<ClanNoticeEditRequested>, IInboundMessageBinding<ClanRecruitmentEditRequested>,
      IInboundMessageBinding<ClanMarkEditRequested>, IInboundMessageBinding<ClanTransferRequested>,
      IInboundMessageBinding<ClanTransferAnswerRequested>, IInboundMessageBinding<ClanTransferConfirmed>,
      IInboundMessageBinding<ClanDisbandRequested>, IInboundMessageBinding<ClanBoardRequested>,
      IInboundMessageBinding<ClanBoardWriteRequested>, IInboundMessageBinding<ClanBoardDeleteRequested>,
      IInboundMessageBinding<ClanChatRequested>, IInboundMessageBinding<ClanWhisperRequested>,
      IMessageSender<ClanHomeSent>, IMessageSender<ClanProfileSent>, IMessageSender<ClanCreateAnswered>,
      IMessageSender<ClanRosterSent>, IMessageSender<ClanMemberDetailsSent>, IMessageSender<ClanSearchSent>,
      IMessageSender<ClanApplyAnswered>, IMessageSender<ClanCancelApplicationAnswered>, IMessageSender<ClanApplicantsSent>,
      IMessageSender<ClanMemberChangeAnswered>, IMessageSender<ClanMemberChanged>, IMessageSender<ClanMemberLeft>,
      IMessageSender<ClanIntroductionEditAnswered>, IMessageSender<ClanIntroductionChanged>, IMessageSender<ClanNoticeEditAnswered>,
      IMessageSender<ClanNoticeChanged>, IMessageSender<ClanRecruitmentEditAnswered>, IMessageSender<ClanMarkChanged>,
      IMessageSender<ClanTransferRequestAnswered>, IMessageSender<ClanTransferOffered>, IMessageSender<ClanTransferAnswerAnswered>,
      IMessageSender<ClanTransferAnswered>, IMessageSender<ClanMasterChanged>,
      IMessageSender<ClanDisbandAnswered>, IMessageSender<ClanDisbandChanged>,
      IMessageSender<ClanBoardSent>, IMessageSender<ClanBoardWriteAnswered>, IMessageSender<ClanBoardDeleted>,
      IMessageSender<ClanChatReceived>, IMessageSender<ClanWhisperAnswered>, IMessageSender<ClanWhisperReceived>
{
    /// <summary>The request every build with clan packets has; its opcode row is what mounts this binding.</summary>
    internal const string Anchor = "ClanHomeRequest";

    /// <summary>
    /// The date the client treats as "no disband pending": it compares the home's date with 2100-01-01 and marks a
    /// seated member's clan as disbanding only when the date is earlier (kr-1256 sub_51A3A0).
    /// </summary>
    internal static readonly DateTime NoDisband = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime OleEpoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc);

    // The clan frame header (length and opcode words) shares the client's packet object with the body.
    private const int FrameHeaderBytes = 4;

    /// <summary>Reads the build's clan packet size; a build with the clan connection must state it.</summary>
    internal static int PacketBytes(PacketTables tables, string build) =>
        tables.ValuesOf(build).TryGetValue("clan_packet_bytes", out var value) && value is not null
            && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            && bytes > FrameHeaderBytes
            ? bytes
            : throw new ConfigException($"The clan connection of '{build}' needs clan_packet_bytes (the client's clan packet size) in packets/builds.yaml.");

    private int MaxBody => packetBytes - FrameHeaderBytes;

    // Every clan frame goes out here, so none exceeds what the client's packet object can hold.
    private ValueTask Send(ISession session, string name, byte[] body, CancellationToken ct) =>
        body.Length <= MaxBody ? session.SendAsync(opcodes.S2C(name), body, ct)
            : throw new InvalidOperationException($"Clan frame {name} of {body.Length} bytes exceeds {build}'s {packetBytes}-byte clan packet.");

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanHomeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanProfileRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanCreateRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanHomeSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanProfileSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanCreateAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanRosterRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanMemberDetailsRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanMemberReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanRosterSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMemberDetailsSent>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanSearchRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanSearchSent>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanApplyRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanJoinRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanCancelApplicationRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanApplicantsRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanApplyAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanCancelApplicationAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanApplicantsSent>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanMemberChangeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanLeaveRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMemberChangeAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMemberChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMemberLeft>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanIntroductionEditRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanNoticeEditRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanRecruitmentEditRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanMarkEditRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanIntroductionEditAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanIntroductionChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanNoticeEditAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanNoticeChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanRecruitmentEditAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMarkChanged>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanTransferRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanTransferAnswerRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanTransferConfirmed>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanTransferRequestAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanTransferOffered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanTransferAnswerAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanTransferAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanMasterChanged>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanDisbandRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanDisbandAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanDisbandChanged>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanBoardRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanBoardWriteRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanBoardDeleteRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanBoardSent>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanBoardWriteAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanBoardDeleted>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanChatRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanWhisperRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanChatReceived>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanWhisperAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanWhisperReceived>>(this));
        var router = context.Services.Get<IPacketRouter>();
        Empty(context, router, messages, "ClanHomeRequest", static () => new ClanHomeRequested());
        Empty(context, router, messages, "ClanProfileRequest", static () => new ClanProfileRequested());
        context.Effect(router.Register(protocol, opcodes.C2S("ClanCreate"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadCreate(tables, build, packet.Payload.Span), "ClanCreate", ct).ConfigureAwait(false)));
        Empty(context, router, messages, "ClanRosterRequest", static () => new ClanRosterRequested());
        context.Effect(router.Register(protocol, opcodes.C2S("ClanMemberDetailsRequest"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadMemberDetailsRequest(tables, build, packet.Payload.Span), "ClanMemberDetailsRequest", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanMemberReport"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadMemberReport(tables, build, packet.Payload.Span), "ClanMemberReport", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanSearch"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadSearch(tables, build, packet.Payload.Span), "ClanSearch", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanApply"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadApply(tables, build, packet.Payload.Span), "ClanApply", ct).ConfigureAwait(false)));
        // kr-812 has no join request: every application waits for the master (kr-812 writers sub_4994A0 and on).
        if (Has(PacketDirection.ClientToServer, "ClanJoin"))
            context.Effect(router.Register(protocol, opcodes.C2S("ClanJoin"), async (session, packet, ct) =>
                await DispatchAsync(messages, session, ReadJoin(tables, build, packet.Payload.Span), "ClanJoin", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanCancelApplication"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadCancelApplication(tables, build, packet.Payload.Span), "ClanCancelApplication", ct).ConfigureAwait(false)));
        Empty(context, router, messages, "ClanApplicantsRequest", static () => new ClanApplicantsRequested());
        context.Effect(router.Register(protocol, opcodes.C2S("ClanMemberChange"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadMemberChange(tables, build, packet.Payload.Span), "ClanMemberChange", ct).ConfigureAwait(false)));
        Empty(context, router, messages, "ClanLeave", static () => new ClanLeaveRequested());
        context.Effect(router.Register(protocol, opcodes.C2S("ClanIntroductionEdit"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, new ClanIntroductionEditRequested(ReadText(tables, build, "ClanServerIntroductionEdit", "introduction", packet.Payload.Span)),
                "ClanIntroductionEdit", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanNoticeEdit"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, new ClanNoticeEditRequested(ReadText(tables, build, "ClanServerNoticeEdit", "notice", packet.Payload.Span)),
                "ClanNoticeEdit", ct).ConfigureAwait(false)));
        // kr-812 has no recruitment setting, and it changes the mark on the game connection (gcpReqChangeClanMark), so
        // these two requests exist only where the build's table has their rows.
        if (Has(PacketDirection.ClientToServer, "ClanRecruitmentEdit"))
            context.Effect(router.Register(protocol, opcodes.C2S("ClanRecruitmentEdit"), async (session, packet, ct) =>
            {
                var (clanId, recruitment) = ReadPair(tables, build, "ClanServerRecruitmentEdit", "recruitment", packet.Payload.Span);
                await DispatchAsync(messages, session, new ClanRecruitmentEditRequested(clanId, recruitment), "ClanRecruitmentEdit", ct).ConfigureAwait(false);
            }));
        if (Has(PacketDirection.ClientToServer, "ClanMarkEdit"))
            context.Effect(router.Register(protocol, opcodes.C2S("ClanMarkEdit"), async (session, packet, ct) =>
            {
                var (clanId, mark) = ReadPair(tables, build, "ClanServerMarkEdit", "mark", packet.Payload.Span);
                await DispatchAsync(messages, session, new ClanMarkEditRequested(clanId, mark), "ClanMarkEdit", ct).ConfigureAwait(false);
            }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanTransferRequest"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadTransferRequest(tables, build, packet.Payload.Span), "ClanTransferRequest", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanTransferAnswer"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadTransferAnswer(tables, build, packet.Payload.Span), "ClanTransferAnswer", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanTransferConfirm"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadTransferConfirm(tables, build, packet.Payload.Span), "ClanTransferConfirm", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanDisband"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, new ClanDisbandRequested(Word(PacketCodec.Read(tables, build, "ClanServerDisband", packet.Payload.Span)["kind"])),
                "ClanDisband", ct).ConfigureAwait(false)));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanBoardRequest"), async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "ClanServerBoardRequest", packet.Payload.Span);
            await DispatchAsync(messages, session, new ClanBoardRequested(Word(fields["mode"]), Word(fields["cursor"])), "ClanBoardRequest", ct).ConfigureAwait(false);
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanBoardWrite"), async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "ClanServerBoardWrite", packet.Payload.Span);
            await DispatchAsync(messages, session, new ClanBoardWriteRequested(Word(fields["unknown_0"]), (string)fields["body"]!), "ClanBoardWrite", ct).ConfigureAwait(false);
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanBoardDelete"), async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "ClanServerBoardDelete", packet.Payload.Span);
            await DispatchAsync(messages, session, new ClanBoardDeleteRequested(Word(fields["user_number"]), Word(fields["post_id"])), "ClanBoardDelete", ct)
                .ConfigureAwait(false);
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanChat"), async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "ClanServerChat", packet.Payload.Span);
            await DispatchAsync(messages, session, new ClanChatRequested(Word(fields["unknown_0"]), (string)fields["nickname"]!, (string)fields["text"]!),
                "ClanChat", ct).ConfigureAwait(false);
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanWhisper"), async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "ClanServerWhisper", packet.Payload.Span);
            await DispatchAsync(messages, session, new ClanWhisperRequested(Word(fields["kind"]), (string)fields["target"]!, (string)fields["text"]!,
                fields.TryGetValue("unknown_3", out var unknown3) ? Word(unknown3) : 0), "ClanWhisper", ct).ConfigureAwait(false);
        }));
    }

    public ValueTask SendAsync(ISession session, ClanHomeSent message, CancellationToken ct) =>
        Send(session, "ClanHome", WriteHome(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanProfileSent message, CancellationToken ct) =>
        Send(session, "ClanProfile", WriteProfile(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanCreateAnswered message, CancellationToken ct) =>
        Send(session, "ClanCreateResult", WriteCreateResult(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanRosterSent message, CancellationToken ct) =>
        Send(session, "ClanRoster", WriteRoster(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanMemberDetailsSent message, CancellationToken ct) =>
        Send(session, "ClanMemberDetails", WriteMemberDetails(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanSearchSent message, CancellationToken ct) =>
        Send(session, "ClanSearchResult", WriteSearchResult(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanApplyAnswered message, CancellationToken ct) =>
        Send(session, "ClanApplyResult", WriteApplyResult(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanCancelApplicationAnswered message, CancellationToken ct) =>
        Send(session, "ClanCancelApplicationResult", WriteCancelApplicationResult(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanApplicantsSent message, CancellationToken ct) =>
        Send(session, "ClanApplicants", WriteApplicants(tables, build, message, MaxBody), ct);

    public ValueTask SendAsync(ISession session, ClanMemberChangeAnswered message, CancellationToken ct) =>
        Send(session, "ClanMemberChangeResult", WriteMemberChangeResult(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanMemberChanged message, CancellationToken ct) =>
        Send(session, "ClanMemberChanged", WriteMemberChanged(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanMemberLeft message, CancellationToken ct) =>
        Send(session, "ClanMemberLeft", WriteMemberLeft(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, ClanIntroductionEditAnswered message, CancellationToken ct) =>
        Send(session, "ClanIntroductionEditResult", Write(tables, build, "ClanServerIntroductionEditResult",
            ("result", message.Result), ("introduction", message.Introduction)), ct);

    public ValueTask SendAsync(ISession session, ClanIntroductionChanged message, CancellationToken ct) =>
        Send(session, "ClanIntroductionChanged", Write(tables, build, "ClanServerIntroductionChanged",
            ("unknown_0", message.Unknown0), ("introduction", message.Introduction)), ct);

    public ValueTask SendAsync(ISession session, ClanNoticeEditAnswered message, CancellationToken ct) =>
        Send(session, "ClanNoticeEditResult", Write(tables, build, "ClanServerNoticeEditResult", ("result", message.Result)), ct);

    public ValueTask SendAsync(ISession session, ClanNoticeChanged message, CancellationToken ct) =>
        Send(session, "ClanNoticeChanged", Write(tables, build, "ClanServerNoticeChanged", ("notice", message.Notice)), ct);

    public ValueTask SendAsync(ISession session, ClanRecruitmentEditAnswered message, CancellationToken ct) =>
        Send(session, "ClanRecruitmentEditResult", Write(tables, build, "ClanServerRecruitmentEditResult",
            ("unknown_0", message.Unknown0), ("result", message.Result), ("recruitment", message.Recruitment)), ct);

    public ValueTask SendAsync(ISession session, ClanMarkChanged message, CancellationToken ct) =>
        Send(session, "ClanMarkChanged", Write(tables, build, "ClanServerMarkChanged", ("mark", message.Mark)), ct);

    public ValueTask SendAsync(ISession session, ClanTransferRequestAnswered message, CancellationToken ct) =>
        Send(session, "ClanTransferRequestResult", Write(tables, build, "ClanServerTransferRequestResult",
            ("sent", message.Sent), ("result", message.Result)), ct);

    public ValueTask SendAsync(ISession session, ClanTransferOffered message, CancellationToken ct) =>
        Send(session, "ClanTransferOffer", Write(tables, build, "ClanServerTransferOffer",
            ("nickname", message.Nickname), ("unknown_1", message.Unknown1)), ct);

    public ValueTask SendAsync(ISession session, ClanTransferAnswerAnswered message, CancellationToken ct) =>
        Send(session, "ClanTransferAnswerResult", Write(tables, build, "ClanServerTransferAnswerResult", ("result", message.Result)), ct);

    public ValueTask SendAsync(ISession session, ClanTransferAnswered message, CancellationToken ct) =>
        Send(session, "ClanTransferAnswered", Write(tables, build, "ClanServerTransferAnswered",
            ("accepted", message.Accepted ? 1 : 0), ("user_number", message.UserNum), ("nickname", message.Nickname)), ct);

    public ValueTask SendAsync(ISession session, ClanMasterChanged message, CancellationToken ct) =>
        Send(session, "ClanMasterChanged", Write(tables, build, "ClanServerMasterChanged",
            ("unknown_0", message.Unknown0), ("old_master_id", checked((int)message.OldMasterUserNum)), ("old_master_name", message.OldMasterNickname),
            ("new_master_id", checked((int)message.NewMasterUserNum)), ("new_master_name", message.NewMasterNickname)), ct);

    public ValueTask SendAsync(ISession session, ClanDisbandAnswered message, CancellationToken ct) =>
        Send(session, "ClanDisbandResult", Write(tables, build, "ClanServerDisbandResult", ("result", message.Result), ("kind", message.Kind)), ct);

    public ValueTask SendAsync(ISession session, ClanDisbandChanged message, CancellationToken ct) =>
        Send(session, "ClanDisbandChanged", Write(tables, build, "ClanServerDisbandChanged", ("unknown_0", message.Unknown0), ("kind", message.Kind)), ct);

    public ValueTask SendAsync(ISession session, ClanBoardSent message, CancellationToken ct) =>
        Send(session, "ClanBoard", WriteBoard(tables, build, message, MaxBody), ct);

    public ValueTask SendAsync(ISession session, ClanBoardWriteAnswered message, CancellationToken ct) =>
        Send(session, "ClanBoardWriteResult", Write(tables, build, "ClanServerBoardWriteResult", ("result", message.Result)), ct);

    public ValueTask SendAsync(ISession session, ClanBoardDeleted message, CancellationToken ct) =>
        Send(session, "ClanBoardDeleted", PacketCodec.Write(tables, build, "ClanServerBoardDeleted",
            new Dictionary<string, object?>(StringComparer.Ordinal)), ct);

    /// <summary>
    /// Writes a board page (reader kr-1256 sub_5285B0): the header with the count and the board's newest and oldest
    /// post ids, then each post. The count is separated from the posts by the header's other fields, so the two
    /// layouts are written one after the other.
    /// </summary>
    /// <summary>
    /// Writes a board page: the header, then the posts that fit in <paramref name="maxBody"/> bytes in page order. The
    /// client asks for the next page by post id (kr-1256 sub_584250), so a page cut short loses no post.
    /// </summary>
    internal static byte[] WriteBoard(PacketTables tables, string build, ClanBoardSent board, int maxBody = int.MaxValue)
    {
        static int Header(PacketTables tables, string build) => Write(tables, build, "ClanServerBoard", ("count", 0),
            ("newest_post_id", 0), ("oldest_post_id", 0)).Length;
        var posts = new List<byte>();
        var count = 0;
        var room = maxBody - Header(tables, build);
        foreach (var post in board.Posts)
        {
            var bytes = Write(tables, build, "ClanServerBoardPost", ("unknown_0", 0), ("author", post.Nickname), ("unknown_2", 0),
                ("post_id", checked((int)post.PostId)), ("shown", 1), ("date", ToOleDate(post.CreatedAt)), ("body", post.Body));
            if (posts.Count + bytes.Length > room)
                break;
            posts.AddRange(bytes);
            count++;
        }
        var body = new List<byte>(Write(tables, build, "ClanServerBoard", ("count", count),
            ("newest_post_id", checked((int)board.NewestPostId)), ("oldest_post_id", checked((int)board.OldestPostId))));
        body.AddRange(posts);
        return [.. body];
    }

    public ValueTask SendAsync(ISession session, ClanChatReceived message, CancellationToken ct) =>
        Send(session, "ClanChatMessage", Write(tables, build, "ClanServerChatMessage", ("nickname", message.Nickname), ("text", message.Text)), ct);

    public ValueTask SendAsync(ISession session, ClanWhisperAnswered message, CancellationToken ct) =>
        Send(session, "ClanWhisperResult", Write(tables, build, "ClanServerWhisperResult", ("result", message.Result),
            ("kind", message.Kind), ("from", message.From), ("to", message.To), ("text", message.Text)), ct);

    public ValueTask SendAsync(ISession session, ClanWhisperReceived message, CancellationToken ct) =>
        Send(session, "ClanWhisperMessage", Write(tables, build, "ClanServerWhisperMessage", ("kind", message.Kind),
            ("from", message.From), ("to", message.To), ("text", message.Text), ("unknown_4", message.Unknown4)), ct);

    /// <summary>Reads a transfer request (writer kr-1256 sub_527F60): the member offered the clan.</summary>
    internal static ClanTransferRequested ReadTransferRequest(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        new(Word(PacketCodec.Read(tables, build, "ClanServerTransferRequest", body)["user_number"]));

    /// <summary>Reads a transfer answer (written by kr-1256 sub_52F620): the accept byte and the offer's word.</summary>
    internal static ClanTransferAnswerRequested ReadTransferAnswer(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerTransferAnswer", body);
        return new(Word(fields["accepted"]) != 0, Word(fields["unknown_1"]));
    }

    /// <summary>Reads a transfer confirmation (writer kr-1256 sub_528070): the new master's id and name.</summary>
    internal static ClanTransferConfirmed ReadTransferConfirm(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerTransferConfirm", body);
        return new(Word(fields["user_number"]), (string)fields["nickname"]!);
    }

    /// <summary>Reads the one string of a text edit (writers kr-1256 sub_527D30 and kr-1256 sub_527CA0).</summary>
    internal static string ReadText(PacketTables tables, string build, string structure, string field, ReadOnlySpan<byte> body) =>
        (string)PacketCodec.Read(tables, build, structure, body)[field]!;

    /// <summary>Reads the clan id and the chosen word of a recruitment or mark edit (writers kr-1256 sub_528460 and kr-1256 sub_530C50).</summary>
    internal static (int ClanId, int Value) ReadPair(PacketTables tables, string build, string structure, string field, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, structure, body);
        return (Word(fields["clan_id"]), Word(fields[field]));
    }

    /// <summary>Writes a reply whose fields are all given by name.</summary>
    internal static byte[] Write(PacketTables tables, string build, string structure, params (string Name, object? Value)[] fields)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            map.Add(name, value);
        return PacketCodec.Write(tables, build, structure, map);
    }

    /// <summary>Reads a member change (writer kr-1256 sub_527B80): the row's id and name, its listed role and the new one.</summary>
    internal static ClanMemberChangeRequested ReadMemberChange(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerMemberChange", body);
        return new(Word(fields["user_number"]), (string)fields["nickname"]!, Word(fields["old_role"]), Word(fields["new_role"]));
    }

    /// <summary>Writes the member change result (reader kr-1256 sub_52D090): result, name, role and the joined flag.</summary>
    internal static byte[] WriteMemberChangeResult(PacketTables tables, string build, ClanMemberChangeAnswered answer) =>
        PacketCodec.Write(tables, build, "ClanServerMemberChangeResult", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = answer.Result,
            ["nickname"] = answer.Nickname,
            ["role"] = answer.Role,
            ["joined"] = answer.Joined,
        });

    /// <summary>Writes a member change notice (reader kr-1256 sub_52CE70).</summary>
    internal static byte[] WriteMemberChanged(PacketTables tables, string build, ClanMemberChanged notice) =>
        PacketCodec.Write(tables, build, "ClanServerMemberChanged", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clan_name"] = notice.ClanName,
            ["user_number"] = checked((int)notice.UserNum),
            ["nickname"] = notice.Nickname,
            ["role"] = notice.Role,
            ["joined"] = notice.Joined,
        });

    /// <summary>Writes a leave notice (reader kr-1256 sub_52E640).</summary>
    internal static byte[] WriteMemberLeft(PacketTables tables, string build, ClanMemberLeft notice) =>
        PacketCodec.Write(tables, build, "ClanServerMemberLeft", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clan_name"] = notice.ClanName,
            ["user_number"] = checked((int)notice.UserNum),
            ["nickname"] = notice.Nickname,
            ["unknown_3"] = notice.Unknown3,
            ["event"] = notice.Event,
        });

    /// <summary>Reads an application (writer kr-1256 sub_527010): the clan id and name of the selected search row.</summary>
    internal static ClanApplyRequested ReadApply(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerApply", body);
        return new(Word(fields["clan_id"]), (string)fields["clan_name"]!);
    }

    /// <summary>Reads a join request (writer kr-1256 sub_527130): the clan id and name and the sender's nickname.</summary>
    internal static ClanJoinRequested ReadJoin(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerJoin", body);
        return new(Word(fields["clan_id"]), (string)fields["clan_name"]!, (string)fields["nickname"]!);
    }

    /// <summary>Reads an application withdrawal (writer kr-1256 sub_527250): the clan id applied to.</summary>
    internal static ClanCancelApplicationRequested ReadCancelApplication(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        new(Word(PacketCodec.Read(tables, build, "ClanServerCancelApplication", body)["clan_id"]));

    /// <summary>Writes the application result (reader kr-1256 sub_52E2A0): result, role, clan id and clan name.</summary>
    internal static byte[] WriteApplyResult(PacketTables tables, string build, ClanApplyAnswered answer) =>
        PacketCodec.Write(tables, build, "ClanServerApplyResult", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = answer.Result,
            ["role"] = answer.Role,
            ["clan_id"] = answer.ClanId,
            ["clan_name"] = answer.ClanName,
        });

    /// <summary>Writes the withdrawal result (kr-1256 sub_525170 case 0x428): two words.</summary>
    internal static byte[] WriteCancelApplicationResult(PacketTables tables, string build, ClanCancelApplicationAnswered answer) =>
        PacketCodec.Write(tables, build, "ClanServerCancelApplicationResult", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = answer.Result,
            ["unknown_4"] = answer.Unknown4,
        });

    /// <summary>
    /// Writes the applicant list (reader kr-1256 sub_52C750): per applicant the id, name and role; the record's other
    /// words have no reader and are 0.
    /// </summary>
    /// <summary>
    /// Writes the applicant list, the oldest applicants that fit in <paramref name="maxBody"/> bytes. The others stay
    /// applicants and are listed once earlier ones are seated or refused.
    /// </summary>
    internal static byte[] WriteApplicants(PacketTables tables, string build, ClanApplicantsSent applicants, int maxBody = int.MaxValue)
    {
        for (var count = applicants.Applicants.Count; ; count--)
        {
            var body = WriteApplicantRows(tables, build, applicants.Applicants.Take(count));
            if (body.Length <= maxBody || count == 0)
                return body;
        }
    }

    private static byte[] WriteApplicantRows(PacketTables tables, string build, IEnumerable<ClanApplicantRow> rows) =>
        PacketCodec.Write(tables, build, "ClanServerApplicants", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicants"] = rows.Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user_number"] = checked((int)row.UserNum),
                ["nickname"] = row.Nickname,
                ["unknown_28"] = 0,
                ["role"] = row.Role,
                ["unknown_56"] = 0L,
                ["unknown_40"] = 0, ["unknown_44"] = 0, ["unknown_48"] = 0, ["unknown_52"] = 0,
            }).ToArray(),
        });

    /// <summary>Reads the search request (writer kr-1256 sub_526020): the mode, the page and the text.</summary>
    internal static ClanSearchRequested ReadSearch(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerSearch", body);
        return new(Word(fields["mode"]), Word(fields["page"]), (string)fields["text"]!);
    }

    /// <summary>
    /// Writes the search page (reader kr-1256 sub_5290A0): the header with the row count, then each row. The
    /// count is separated from the rows by the header's other fields, so the two layouts are written one after the
    /// other rather than as one list.
    /// </summary>
    internal static byte[] WriteSearchResult(PacketTables tables, string build, ClanSearchSent page)
    {
        var body = new List<byte>(PacketCodec.Write(tables, build, "ClanServerSearchResult", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["count"] = page.Clans.Count,
            ["unknown_4"] = page.Mode,
            ["page"] = page.Page,
            ["text"] = page.Text,
        }));
        foreach (var row in page.Clans)
        {
            var clan = row.Clan;
            body.AddRange(PacketCodec.Write(tables, build, "ClanServerSearchEntry", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["clan_id"] = checked((int)clan.ClanId),
                ["clan_name"] = clan.Name,
                ["unknown_88"] = checked((int)clan.MasterUserNum),
                ["master_name"] = row.MasterName,
                ["level"] = clan.Level,
                ["points"] = clan.Points,
                ["founded_date"] = ToOleDate(clan.CreatedAt),
                ["unknown_56"] = 0d,
                ["recruitment"] = clan.Recruitment,
                ["member_count"] = clan.MemberCount,
                ["unknown_10"] = 0,
                ["introduction"] = clan.Introduction,
                ["disband_date"] = ToOleDate(clan.DisbandAt ?? NoDisband),
                ["last_login_date"] = ToOleDate(row.LastLoginAt),
                ["mark"] = clan.Mark,
            }));
        }
        return [.. body];
    }

    /// <summary>Reads the detailed roster request (writer kr-1256 sub_527910): one word.</summary>
    internal static ClanMemberDetailsRequested ReadMemberDetailsRequest(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        new(Word(PacketCodec.Read(tables, build, "ClanServerMemberDetailsRequest", body)["unknown_0"]));

    /// <summary>Reads the member report (writer kr-1256 sub_527AB0): two words.</summary>
    internal static ClanMemberReported ReadMemberReport(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerMemberReport", body);
        return new(Word(fields["unknown_0"]), Word(fields["unknown_4"]));
    }

    /// <summary>
    /// Writes the roster (reader kr-1256 sub_52B4B0). The first two words are the member's own last report in the
    /// record order the client keeps them in (+36 then +28), so a member's row reads like the client's own row.
    /// </summary>
    internal static byte[] WriteRoster(PacketTables tables, string build, ClanRosterSent roster) =>
        PacketCodec.Write(tables, build, "ClanServerRoster", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["members"] = roster.Members.Select(member => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user_number"] = checked((int)member.UserNum),
                ["nickname"] = member.Nickname,
                ["unknown_36"] = member.Reported?.Unknown4 ?? 0,
                ["unknown_28"] = member.Reported?.Unknown0 ?? 0,
                ["role"] = member.Role,
            }).ToArray(),
        });

    /// <summary>
    /// Writes the detailed roster (reader kr-1256 sub_52C010): a skipped word, then per member the id, name, the
    /// report's first word and the role; the record's remaining words are 0, as kr-1256 sub_5244E0 leaves them.
    /// </summary>
    internal static byte[] WriteMemberDetails(PacketTables tables, string build, ClanMemberDetailsSent details) =>
        PacketCodec.Write(tables, build, "ClanServerMemberDetails", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unknown_0"] = 0,
            ["members"] = details.Members.Select(member => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user_number"] = checked((int)member.UserNum),
                ["nickname"] = member.Nickname,
                ["unknown_28"] = member.Reported?.Unknown0 ?? 0,
                ["role"] = member.Role,
                ["unknown_40"] = 0, ["unknown_44"] = 0, ["unknown_48"] = 0L, ["unknown_56"] = 0L, ["unknown_64"] = 0L,
                ["unknown_72"] = 0, ["unknown_76"] = 0, ["unknown_80"] = 0, ["unknown_84"] = 0,
            }).ToArray(),
        });

    /// <summary>
    /// Reads the create request (writers kr-1256 sub_5275B0, kr-812 sub_499760): name, word, introduction, card item and,
    /// in kr-1256, the card's capacity.
    /// </summary>
    internal static ClanCreateRequested ReadCreate(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerCreate", body);
        return new ClanCreateRequested((string)fields["name"]!, Word(fields["unknown_1"]), (string)fields["introduction"]!,
            Word(fields["item"]), fields.TryGetValue("capacity", out var capacity) ? Word(capacity) : null);
    }

    /// <summary>Writes the home (reader kr-1256 sub_526330): flag byte, clan id, role, name and the disband date.</summary>
    internal static byte[] WriteHome(PacketTables tables, string build, ClanHomeSent home) =>
        PacketCodec.Write(tables, build, "ClanServerHome", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile_follows"] = home.ProfileFollows ? 1 : 0,
            ["clan_id"] = checked((int)home.ClanId),
            ["role"] = home.Role,
            ["clan_name"] = home.ClanName,
            ["disband_date"] = ToOleDate(home.DisbandAt ?? NoDisband),
        });

    /// <summary>Writes the profile in the reader's order (kr-1256 sub_5268A0).</summary>
    internal static byte[] WriteProfile(PacketTables tables, string build, ClanProfileSent profile)
    {
        var clan = profile.Clan;
        return PacketCodec.Write(tables, build, "ClanServerProfile", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clan_name"] = clan.Name,
            ["master_id"] = checked((int)clan.MasterUserNum),
            ["master_name"] = profile.MasterName,
            ["deputy_id"] = checked((int)profile.DeputyUserNum),
            ["deputy_name"] = profile.DeputyName,
            ["level"] = clan.Level,
            ["points"] = clan.Points,
            // The 8-byte values are stored unread (kr-1256 sub_51A5F0 +144/+152, kr-812 sub_4905D0 +136). They are
            // written as the dates the search row carries in the same places; see packets/clan-server.yaml. kr-812's
            // +152 is a word, not a date, and carries 0.
            ["unknown_136"] = ToOleDate(clan.CreatedAt),
            ["unknown_144"] = ToOleDate(clan.CreatedAt),
            ["unknown_152"] = PacketLayoutContract.Signature(tables, build, "ClanServerProfile").Contains("unknown_152:f64", StringComparison.Ordinal)
                ? ToOleDate(clan.DisbandAt ?? NoDisband)
                : 0,
            ["recruitment"] = clan.Recruitment,
            ["member_count"] = clan.MemberCount,
            ["unknown_172"] = 0,
            ["notice"] = clan.Notice,
            ["introduction"] = clan.Introduction,
            ["mark"] = clan.Mark,
            ["capacity"] = clan.Capacity,
        });
    }

    /// <summary>Writes the create result (readers kr-1256 sub_525170 case 0x404, three words; kr-812 sub_49A7C0, two).</summary>
    internal static byte[] WriteCreateResult(PacketTables tables, string build, ClanCreateAnswered answer) =>
        PacketCodec.Write(tables, build, "ClanServerCreateResult", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = answer.Result,
            ["item"] = answer.Item,
            ["unknown_8"] = answer.Unknown8,
        });

    /// <summary>
    /// A date as the client's 8-byte date: days since 1899-12-30 as a double, the form kr-1256 sub_51A3A0 converts
    /// to a SYSTEMTIME before comparing.
    /// </summary>
    internal static double ToOleDate(DateTime utc) => (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - OleEpoch).TotalDays;

    // Whether the build's opcode table has the named clan-connection row (rows a build may lack: join, recruitment, mark edit).
    private bool Has(PacketDirection direction, string name) => opcodes.Find(direction, name) is not null;

    // The codec returns a u32 as whichever integer type holds it; the client's words are signed (results, codes).
    private static int Word(object? value) => unchecked((int)Convert.ToInt64(value, CultureInfo.InvariantCulture));

    private void Empty<T>(IPluginContext context, IPacketRouter router, IMessageRouter messages, string name, Func<T> create)
        where T : IMessage =>
        context.Effect(router.Register(protocol, opcodes.C2S(name), async (session, packet, ct) =>
        {
            if (!packet.Payload.Span.IsEmpty)
                throw new PacketFormatException($"{name} must be empty.", 0);
            await DispatchAsync(messages, session, create(), name, ct).ConfigureAwait(false);
        }));

    private async ValueTask DispatchAsync<T>(IMessageRouter messages, ISession session, T message, string name, CancellationToken ct)
        where T : IMessage
    {
        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{name} has no typed clan handler.");
    }

    private void ValidateLayouts()
    {
        const string consumer = "Clan connection";
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerHomeRequest", consumer);
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerProfileRequest", consumer);
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerHome", consumer,
            "profile_follows:u8,clan_id:u32,role:u32,clan_name:wcstr,disband_date:f64");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerProfile", consumer,
            "clan_name:wcstr,master_id:u32,master_name:wcstr,deputy_id:u32,deputy_name:wcstr,level:u32,points:u32,unknown_136:f64,"
            + "member_count:u32,unknown_152:u32,notice:wcstr,introduction:wcstr,mark:u32,capacity:u32",
            "clan_name:wcstr,master_id:u32,master_name:wcstr,deputy_id:u32,deputy_name:wcstr,level:u32,points:u32,unknown_136:f64,"
            + "member_count:u32,unknown_152:u32,notice:wcstr,introduction:wcstr",
            "clan_name:wcstr,master_id:u32,master_name:wcstr,deputy_id:u32,deputy_name:wcstr,level:u32,points:u32,unknown_144:f64,"
            + "unknown_152:f64,recruitment:u32,member_count:u32,unknown_172:u32,notice:wcstr,introduction:wcstr,mark:u32,capacity:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerCreate", consumer,
            "name:wcstr,unknown_1:u32,introduction:wcstr,item:u32",
            "name:wcstr,unknown_1:u32,introduction:wcstr,item:u32,capacity:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerCreateResult", consumer,
            "result:u32,item:u32", "result:u32,item:u32,unknown_8:u32");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerRosterRequest", consumer);
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerRosterMember", consumer,
            "user_number:u32,nickname:wcstr,unknown_36:u32,unknown_28:u32,role:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberDetailsRequest", consumer, "unknown_0:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberDetail", consumer,
            "user_number:u32,nickname:wcstr,unknown_28:u32,role:u32,unknown_40:u32,unknown_44:u32,unknown_48:u64,unknown_56:u64,"
            + "unknown_64:u64,unknown_72:u32,unknown_76:u32,unknown_80:u32,unknown_84:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberReport", consumer, "unknown_0:u32,unknown_4:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerSearch", consumer, "mode:u32,page:u32,text:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerSearchResult", consumer, "count:u32,unknown_4:u32,page:u32,text:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerSearchEntry", consumer,
            "clan_id:u32,clan_name:wcstr,unknown_88:u32,master_name:wcstr,level:u32,points:u32,founded_date:f64,"
            + "member_count:u32,unknown_10:u32,introduction:wcstr,disband_date:f64,mark:u32",
            "clan_id:u32,clan_name:wcstr,unknown_88:u32,master_name:wcstr,level:u32,points:u32,founded_date:f64,"
            + "member_count:u32,unknown_10:u32,introduction:wcstr,disband_date:f64",
            "clan_id:u32,clan_name:wcstr,unknown_88:u32,master_name:wcstr,level:u32,points:u32,founded_date:f64,unknown_56:f64,"
            + "recruitment:u32,member_count:u32,unknown_10:u32,introduction:wcstr,disband_date:f64,last_login_date:f64,mark:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerApply", consumer, "clan_id:u32,clan_name:wcstr");
        if (Has(PacketDirection.ClientToServer, "ClanJoin"))
            PacketLayoutContract.RequireSignature(tables, build, "ClanServerJoin", consumer, "clan_id:u32,clan_name:wcstr,nickname:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerApplyResult", consumer, "result:u32,role:u32,clan_id:u32,clan_name:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerCancelApplication", consumer, "clan_id:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerCancelApplicationResult", consumer, "result:u32,unknown_4:u32");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerApplicantsRequest", consumer);
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerApplicant", consumer,
            "user_number:u32,nickname:wcstr,unknown_28:u32,role:u32,unknown_56:u64,unknown_40:u32,unknown_44:u32,unknown_48:u32,unknown_52:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberChange", consumer, "user_number:u32,nickname:wcstr,old_role:u32,new_role:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberChangeResult", consumer, "result:u32,nickname:wcstr,role:u32,joined:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberChanged", consumer,
            "clan_name:wcstr,user_number:u32,nickname:wcstr,role:u32,joined:u32");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerLeave", consumer);
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMemberLeft", consumer,
            "clan_name:wcstr,user_number:u32,nickname:wcstr,unknown_3:u32,event:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerIntroductionEdit", consumer, "introduction:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerIntroductionEditResult", consumer, "result:u32,introduction:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerIntroductionChanged", consumer, "unknown_0:u32,introduction:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerNoticeEdit", consumer, "notice:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerNoticeEditResult", consumer, "result:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerNoticeChanged", consumer, "notice:wcstr");
        // The recruitment request and its answer come as a pair: a build has both rows or neither.
        var recruitment = Has(PacketDirection.ClientToServer, "ClanRecruitmentEdit");
        if (recruitment != Has(PacketDirection.ServerToClient, "ClanRecruitmentEditResult"))
            throw new ConfigException($"Opcode table '{opcodes.Profile.Id}' must state both ClanRecruitmentEdit and ClanRecruitmentEditResult or neither.");
        if (recruitment)
        {
            PacketLayoutContract.RequireSignature(tables, build, "ClanServerRecruitmentEdit", consumer, "clan_id:u32,recruitment:u32");
            PacketLayoutContract.RequireSignature(tables, build, "ClanServerRecruitmentEditResult", consumer, "unknown_0:u32,result:u32,recruitment:u32");
        }
        if (Has(PacketDirection.ClientToServer, "ClanMarkEdit"))
        {
            // The mark broadcast answers this request only (cn-404 and cn-408 have neither row).
            PacketLayoutContract.RequireSignature(tables, build, "ClanServerMarkEdit", consumer, "clan_id:u32,mark:u32");
            _ = opcodes.S2C("ClanMarkChanged");
        }
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMarkChanged", consumer, "mark:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferRequest", consumer, "user_number:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerDisband", consumer, "kind:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoardRequest", consumer, "mode:u32,cursor:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoard", consumer, "count:u32,newest_post_id:u32,oldest_post_id:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoardPost", consumer,
            "unknown_0:u32,author:wcstr,unknown_2:u32,post_id:u32,shown:u16,date:f64,body:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoardWrite", consumer, "unknown_0:u32,body:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoardWriteResult", consumer, "result:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerBoardDelete", consumer, "user_number:u32,post_id:u32");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerBoardDeleted", consumer);
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerChat", consumer, "unknown_0:u32,nickname:wcstr,text:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerChatMessage", consumer,
            "unknown_0:u32,nickname:wcstr,text:wcstr", "nickname:wcstr,text:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerWhisper", consumer,
            "kind:u32,target:wcstr,text:wcstr", "kind:u32,target:wcstr,text:wcstr,unknown_3:u8");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerWhisperResult", consumer, "result:u32,kind:u32,from:wcstr,to:wcstr,text:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerWhisperMessage", consumer,
            "kind:u32,from:wcstr,to:wcstr,text:wcstr", "kind:u32,from:wcstr,to:wcstr,text:wcstr,unknown_4:u8");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerDisbandResult", consumer, "result:u32,kind:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerDisbandChanged", consumer, "unknown_0:u32,kind:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferRequestResult", consumer, "sent:u8,result:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferOffer", consumer, "nickname:wcstr,unknown_1:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferAnswer", consumer, "accepted:u8,unknown_1:u32");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferAnswerResult", consumer, "result:u8");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferAnswered", consumer, "accepted:u8,user_number:u32,nickname:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerTransferConfirm", consumer, "user_number:u32,nickname:wcstr");
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerMasterChanged", consumer,
            "unknown_0:u32,old_master_id:u32,old_master_name:wcstr,new_master_id:u32,new_master_name:wcstr");
        foreach (var name in (string[])["ClanHome", "ClanProfile", "ClanCreateResult", "ClanRoster", "ClanMemberDetails", "ClanSearchResult",
            "ClanApplyResult", "ClanCancelApplicationResult", "ClanApplicants", "ClanMemberChangeResult", "ClanMemberChanged", "ClanMemberLeft",
            "ClanIntroductionEditResult", "ClanIntroductionChanged", "ClanNoticeEditResult", "ClanNoticeChanged",
            "ClanTransferRequestResult", "ClanTransferOffer", "ClanTransferAnswerResult", "ClanTransferAnswered",
            "ClanMasterChanged", "ClanDisbandResult", "ClanDisbandChanged", "ClanBoard", "ClanBoardWriteResult", "ClanBoardDeleted",
            "ClanChatMessage", "ClanWhisperResult", "ClanWhisperMessage"])
            _ = opcodes.S2C(name);
        foreach (var name in (string[])["ClanHomeRequest", "ClanProfileRequest", "ClanCreate", "ClanRosterRequest",
            "ClanMemberDetailsRequest", "ClanMemberReport", "ClanSearch", "ClanApply", "ClanCancelApplication",
            "ClanApplicantsRequest", "ClanMemberChange", "ClanLeave", "ClanIntroductionEdit", "ClanNoticeEdit", "ClanTransferRequest",
            "ClanTransferAnswer", "ClanTransferConfirm", "ClanDisband", "ClanBoardRequest",
            "ClanBoardWrite", "ClanBoardDelete", "ClanChat", "ClanWhisper"])
            _ = opcodes.C2S(name);
    }
}
