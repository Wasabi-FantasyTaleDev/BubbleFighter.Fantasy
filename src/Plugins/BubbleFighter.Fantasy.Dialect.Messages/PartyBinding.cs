using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The shared party packets (layouts in <c>packets/party.yaml</c>, numbers from the build's opcode table by name):
/// invitation, accept, answer, leave, kick and leader handover in; the answers, the invitation prompt, the party
/// list and the membership notices out. The invitation result family is selected by the build's table: the legacy
/// builds write <c>gspRepInvite</c> with the <see cref="PartyInviteResult"/> codes (kr-812 sub_81CDF0), the modern
/// family writes <c>gspInvitationDelivered</c> / <c>gspInvitationFailed</c> with its own codes (kr-1256 sub_9A7D50).
/// The leave, kick and leader requests are mounted only for builds whose tables name them; the modern family's rows
/// are not audited yet, so those three stay off there (S5). Visits (C2S gcpReqMoveToFriend) are mounted where the
/// build's table names the request; the prompt, the answers and the failure are the legacy gspRepMoveToFriend family
/// or the modern gspAskedVisitation family, again by the build's own names (packets/play-together.yaml).
/// </summary>
internal sealed class PartyBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<PartyInviteRequested>, IInboundMessageBinding<PartyInviteAccepted>,
      IInboundMessageBinding<PartyInviteAnswered>, IInboundMessageBinding<PartyInviteTokenRefused>,
      IInboundMessageBinding<PartyLeaveRequested>, IInboundMessageBinding<PartyKickRequested>,
      IInboundMessageBinding<PartyLeaderChangeRequested>,
      IMessageSender<PartyInviteReplied>, IMessageSender<PartyInviteNotified>, IMessageSender<PartyUpdated>,
      IMessageSender<PartyLeaderReplaced>, IMessageSender<PartyLeft>, IMessageSender<PartyMemberLeft>,
      IMessageSender<PartyMemberJoined>, IMessageSender<PartyKickReplied>, IMessageSender<PartyKicked>,
      IMessageSender<PartyMemberKicked>, IMessageSender<PartyLeaderChanged>,
      IInboundMessageBinding<PartyVisitRequested>, IInboundMessageBinding<PartyVisitAnswered>,
      IInboundMessageBinding<PartyVisitTokenAnswered>, IMessageSender<PartyVisitAsked>, IMessageSender<PartyVisitFailed>,
      IMessageSender<PartyVisitGranted>, IMessageSender<PartyVisitSucceeded>, IMessageSender<PartyInvitationMoveSucceeded>
{
    /// <summary>The C2S names the legacy family mounts; gcpReqMoveToFriendAccept is read where the build has it.</summary>
    internal static readonly string[] Requests =
    [
        "gcpReqInvite", "gcpReqMoveToInviter", "gcpReqLeaveParty",
        "gcpReqKickFromParty", "gcpReqChangePartyLeader",
    ];

    /// <summary>The S2C names the legacy binding writes.</summary>
    internal static readonly string[] Replies =
    [
        "gspRepInvite", "gspNotifyInvite", "gspUpdateParty", "gspUpdatePartyLeader", "gspNotifyPartyLeaveSuccess",
        "gspNotifyMemberLeave", "gspNotifyNewMemberJoin", "gspRepKickFromParty", "gspNotifyKickFromParty",
        "gspNotifyKickMember", "gspRepChangePartyLeader",
    ];

    /// <summary>The kind byte of the accept that answers an invitation prompt (kr-1256 sub_9A8DF0 sets it to 1).</summary>
    private const byte AcceptKind = 1;

    /// <summary>
    /// The kind byte of the move a granted visit makes: kr-1256 sub_9A9710 and cn-774 sub_BA95E0 answer
    /// gspVisitationAccepted with kind 0 at once.
    /// </summary>
    private const byte VisitMoveKind = 0;

    /// <summary>The legacy gspRepMoveToFriend result that prompts the visited player (kr-812 sub_81ED40 case 17).</summary>
    private const uint LegacyVisitPrompt = 17;

    /// <summary>The legacy gspRepMoveToFriend results (kr-812 sub_81ED40 texts).</summary>
    private static readonly IReadOnlyDictionary<PartyVisitFailure, uint> LegacyVisitCodes = new Dictionary<PartyVisitFailure, uint>
    {
        [PartyVisitFailure.Self] = 1,            // cannot visit yourself
        [PartyVisitFailure.Unregistered] = 2,    // %s is not a registered player
        [PartyVisitFailure.Offline] = 3,         // %s is not logged in
        [PartyVisitFailure.TargetPlaying] = 4,   // %s is playing another game
        [PartyVisitFailure.SameParty] = 5,       // %s is in the same party
        [PartyVisitFailure.PartyFull] = 7,       // the party is full
        [PartyVisitFailure.VisitorPlaying] = 10, // cannot visit %s
        [PartyVisitFailure.Unavailable] = 10,    // cannot visit %s
        [PartyVisitFailure.Declined] = 18,       // %s declined the visit
    };

    /// <summary>The modern gspVisitationFailed codes (kr-1256 sub_9AA990, party/VisitFailed.*).</summary>
    private static readonly IReadOnlyDictionary<PartyVisitFailure, uint> ModernVisitCodes = new Dictionary<PartyVisitFailure, uint>
    {
        [PartyVisitFailure.Self] = 1,            // Myself
        [PartyVisitFailure.SameParty] = 2,       // TargetAlreadyIn
        [PartyVisitFailure.VisitorPlaying] = 3,  // YouOnPlaying
        [PartyVisitFailure.TargetPlaying] = 8,   // TargetIngame
        [PartyVisitFailure.PartyFull] = 14,      // FullParty
        [PartyVisitFailure.Unavailable] = 20,    // AnywayTarget
        [PartyVisitFailure.Offline] = 21,        // TargetNotLogin
        [PartyVisitFailure.Unregistered] = 22,   // TargetNotRegistered
        [PartyVisitFailure.Declined] = 28,       // TargetRefused
    };

    /// <summary>The prompt name candidates, legacy first: kr-1256 and cn-774 call it gspAskedInvitation.</summary>
    private static readonly string[] NoticeNames = ["gspNotifyInvite", "gspAskedInvitation"];

    /// <summary>The modern invitation-result codes (kr-1256 sub_9A7D50 text switch; cn-774 sub_BA7E60 has the same set).</summary>
    private static readonly IReadOnlyDictionary<PartyInviteResult, uint> FailureCodes = new Dictionary<PartyInviteResult, uint>
    {
        [PartyInviteResult.Self] = 1,          // party/InviteFailed.Myself
        [PartyInviteResult.SameParty] = 2,     // party/InviteFailed.InviteeAlreadyIn
        [PartyInviteResult.Playing] = 3,       // party/InviteFailed.InviteeOnPlaying
        [PartyInviteResult.PartyFull] = 14,    // party/InviteFailed.FullParty
        [PartyInviteResult.Offline] = 24,      // party/InviteFailed.InviteeNotLogin (kr-1256 sub_9A7A30 clears the pending member)
        [PartyInviteResult.Unregistered] = 25, // party/InviteFailed.InviteeNotRegistered
        [PartyInviteResult.Refused] = 26,      // party/InviteFailed.InviteeRefused
    };

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts(tables, build);
        if (VisitFamily() == "direct")
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyVisitSucceeded>>(this));
        var modern = UsesModernResults();
        if (!modern)
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyInvitationMoveSucceeded>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyInviteRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyInviteAccepted>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyInviteReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyInviteNotified>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyLeaderReplaced>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyLeft>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyMemberLeft>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PartyMemberJoined>>(this));
        if (!modern)
        {
            // The legacy family's kick and leader results; the modern rows have no audited request bodies (S5).
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyKickReplied>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyKicked>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyMemberKicked>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyLeaderChanged>>(this));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyLeaveRequested>>(this));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyKickRequested>>(this));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyLeaderChangeRequested>>(this));
        }
        var router = context.Services.Get<IPacketRouter>();
        // The inviter's request exists where the build's table names it: kr-1256 has it, cn-774 and tw-454 do not.
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqInvite") is not null)
            Route<PartyInviteRequested>(context, router, messages, "gcpReqInvite", ReadInvite);
        else if (!modern)
            throw new ConfigException($"Build '{build}' mounts the legacy party binding without C2S gcpReqInvite.");
        Route<PartyInviteAccepted>(context, router, messages, "gcpReqMoveToInviter", ReadAccept);
        // cn-324 and cn-279 have no gcpReqMoveToFriendAccept class (their C2S 111 is gcpReqMatch): no answer packet.
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriendAccept") is not null)
        {
            if (HasField("PartyInviteAnswer", "token"))
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyInviteTokenRefused>>(this));
                Route<PartyInviteTokenRefused>(context, router, messages, "gcpReqMoveToFriendAccept", ReadTokenRefusal);
            }
            else
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyInviteAnswered>>(this));
                // The same packet answers a visit prompt where the build can visit (kr-812 sub_81ED40 case 17).
                if (VisitFamily() == "legacy") RouteMessage(context, router, messages, "gcpReqMoveToFriendAccept", ReadFriendAnswer);
                else Route<PartyInviteAnswered>(context, router, messages, "gcpReqMoveToFriendAccept", ReadAnswer);
            }
        }
        if (VisitFamily() is { } family)
        {
            ValidateVisitLayouts(family);
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyVisitRequested>>(this));
            if (family != "direct")
                context.Effect(context.Services.RegisterMany<IMessageSender<PartyVisitAsked>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyVisitFailed>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PartyVisitGranted>>(this));
            Route<PartyVisitRequested>(context, router, messages, "gcpReqMoveToFriend", ReadVisit);
            if (family == "legacy")
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyVisitAnswered>>(this));
            else if (family == "modern")
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PartyVisitTokenAnswered>>(this));
                Route<PartyVisitTokenAnswered>(context, router, messages, "gcpReqAcceptVisitation_", body =>
                    new(Convert.ToUInt32(PacketCodec.Read(tables, build, "VisitationAccept", body)["token"]), true));
                Route<PartyVisitTokenAnswered>(context, router, messages, "gcpReqRefuseVisitation_", body =>
                    new(Convert.ToUInt32(PacketCodec.Read(tables, build, "VisitationRefusal", body)["token"]), false));
            }
        }
        if (modern)
        {
            // The modern family's leave, kick and leader request bodies are not audited; the shared party row still
            // mounts, but those three stay off until the client's reader proves each body (S5 scope).
            context.Logger.LogInformation(
                "Party binding on '{Protocol}' for '{Build}': invitation and membership only; leave, kick and leader requests stay off until their bodies are audited.",
                protocol, build);
        }
        else
        {
            Route<PartyLeaveRequested>(context, router, messages, "gcpReqLeaveParty", body =>
                body.IsEmpty ? new PartyLeaveRequested() : throw new PacketFormatException("gcpReqLeaveParty must be empty.", 0));
            Route<PartyKickRequested>(context, router, messages, "gcpReqKickFromParty", body => new(Nickname(body)));
            Route<PartyLeaderChangeRequested>(context, router, messages, "gcpReqChangePartyLeader", body => new(Nickname(body)));
        }
    }

    /// <summary>Gets whether the build writes the modern invitation-result family, by its own S2C names.</summary>
    internal bool UsesModernResults()
    {
        var delivered = opcodes.Find(PacketDirection.ServerToClient, "gspInvitationDelivered");
        var failed = opcodes.Find(PacketDirection.ServerToClient, "gspInvitationFailed");
        if (delivered is null == (failed is null))
            return delivered is not null;
        throw new ConfigException($"Build '{build}' names only one of gspInvitationDelivered and gspInvitationFailed.");
    }

    public ValueTask SendAsync(ISession session, PartyInviteReplied message, CancellationToken ct) =>
        UsesModernResults()
            ? message.Result == PartyInviteResult.Sent
                ? Send(session, "gspInvitationDelivered", "PartyNickname", new() { ["nickname"] = message.Target }, ct)
                : Send(session, "gspInvitationFailed", "PartyInviteFailure", new()
                {
                    ["nickname"] = message.Target,
                    ["code"] = FailureCodes.TryGetValue(message.Result, out var code) ? code
                        : throw new InvalidOperationException($"No gspInvitationFailed code for {message.Result}."),
                }, ct)
            : Send(session, "gspRepInvite", "PartyResult", new() { ["result"] = (uint)message.Result, ["nickname"] = message.Target }, ct);

    // The address is the inviter's server; one server answers every accept, so it is left unset (0.0.0.0:0).
    public ValueTask SendAsync(ISession session, PartyInviteNotified message, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?>
        {
            ["inviter"] = message.Inviter,
            ["address"] = new Dictionary<string, object?> { ["ipv4_word"] = 0u, ["port"] = (ushort)0 },
        };
        if (HasField("PartyInviteNotice", "token")) fields["token"] = message.Token;
        // tw-454 sub_A86A70 and kr-812 sub_81DEA0 skip the prompt when this word is set; an invitation always asks.
        if (HasField("PartyInviteNotice", "auto_accept")) fields["auto_accept"] = false;
        if (HasField("PartyInviteNotice", "is_party")) fields["is_party"] = message.IsParty;
        return session.SendAsync(NoticeName(), PacketCodec.Write(tables, build, "PartyInviteNotice", fields), ct);
    }

    public ValueTask SendAsync(ISession session, PartyVisitAsked message, CancellationToken ct) =>
        VisitFamily() == "legacy"
            ? Send(session, "gspRepMoveToFriend", "MoveToFriendReply", new()
            {
                ["result"] = LegacyVisitPrompt, ["nickname"] = message.Visitor, ["unknown_36"] = 0u,
            }, ct)
            : Send(session, "gspAskedVisitation", "VisitationAsked", new()
            {
                ["token"] = message.Token, ["visitor"] = message.Visitor, ["unknown_flag"] = false,
            }, ct);

    // cn-324 sub_706A60 and kr-812 sub_81E690 consume result + nickname; result 0 completes the pending move.
    public ValueTask SendAsync(ISession session, PartyInvitationMoveSucceeded message, CancellationToken ct) =>
        Send(session, "gspRepMoveToInviter", "PartyResult", new()
        {
            ["result"] = 0u, ["nickname"] = message.Target,
        }, ct);

    // cn-324 sub_706F70 result 0 resets the pending social move through cn-324 sub_5D8B40.
    public ValueTask SendAsync(ISession session, PartyVisitSucceeded message, CancellationToken ct) =>
        Send(session, "gspRepMoveToFriend", "MoveToFriendReply", new()
        {
            ["result"] = 0u, ["nickname"] = message.Target, ["unknown_36"] = 0u,
        }, ct);

    public ValueTask SendAsync(ISession session, PartyVisitFailed message, CancellationToken ct) =>
        VisitFamily() is "legacy" or "direct"
            ? Send(session, "gspRepMoveToFriend", "MoveToFriendReply", new()
            {
                ["result"] = LegacyVisitCodes[message.Reason], ["nickname"] = message.Target, ["unknown_36"] = 0u,
            }, ct)
            : Send(session, "gspVisitationFailed", "VisitationFailed", new()
            {
                ["nickname"] = message.Target, ["code"] = ModernVisitCodes[message.Reason],
            }, ct);

    // kr-1256 and cn-774 have gspVisitationAccepted; kr-812 and tw-454 move the visitor with an invitation notice that
    // skips the prompt. Either way the client sends gcpReqMoveToInviter, which joins it to the visited player's party.
    public ValueTask SendAsync(ISession session, PartyVisitGranted message, CancellationToken ct)
    {
        var address = new Dictionary<string, object?> { ["ipv4_word"] = 0u, ["port"] = (ushort)0 };
        if (opcodes.Find(PacketDirection.ServerToClient, "gspVisitationAccepted") is not null)
            return Send(session, "gspVisitationAccepted", "VisitationAccepted", new() { ["nickname"] = message.Host, ["address"] = address }, ct);
        if (!HasField("PartyInviteNotice", "auto_accept"))
            throw new ConfigException($"Build '{build}' can neither accept a visit nor skip its invitation prompt.");
        var fields = new Dictionary<string, object?> { ["inviter"] = message.Host, ["address"] = address, ["auto_accept"] = true };
        if (HasField("PartyInviteNotice", "is_party")) fields["is_party"] = true;
        return session.SendAsync(NoticeName(), PacketCodec.Write(tables, build, "PartyInviteNotice", fields), ct);
    }

    public ValueTask SendAsync(ISession session, PartyUpdated message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspUpdateParty"), WriteParty(tables, build, message), ct);

    public ValueTask SendAsync(ISession session, PartyLeaderReplaced message, CancellationToken ct) =>
        Send(session, "gspUpdatePartyLeader", "PartyNicknamePair", new() { ["first"] = message.Left, ["second"] = message.Leader }, ct);

    public ValueTask SendAsync(ISession session, PartyLeft message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspNotifyPartyLeaveSuccess"), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, PartyMemberLeft message, CancellationToken ct) =>
        Send(session, "gspNotifyMemberLeave", "PartyNickname", new() { ["nickname"] = message.Nickname }, ct);

    public ValueTask SendAsync(ISession session, PartyMemberJoined message, CancellationToken ct) =>
        Send(session, "gspNotifyNewMemberJoin", "PartyNickname", new() { ["nickname"] = message.Nickname }, ct);

    public ValueTask SendAsync(ISession session, PartyKickReplied message, CancellationToken ct) =>
        Send(session, "gspRepKickFromParty", "PartyResult", new() { ["result"] = (uint)message.Result, ["nickname"] = message.Target }, ct);

    public ValueTask SendAsync(ISession session, PartyKicked message, CancellationToken ct) =>
        Send(session, "gspNotifyKickFromParty", "PartyNickname", new() { ["nickname"] = message.Leader }, ct);

    public ValueTask SendAsync(ISession session, PartyMemberKicked message, CancellationToken ct) =>
        Send(session, "gspNotifyKickMember", "PartyNicknamePair", new() { ["first"] = message.Leader, ["second"] = message.Kicked }, ct);

    public ValueTask SendAsync(ISession session, PartyLeaderChanged message, CancellationToken ct) =>
        Send(session, "gspRepChangePartyLeader", "PartyLeaderChangeReply", new()
        {
            ["previous"] = message.Previous, ["leader"] = message.Leader, ["result"] = (uint)message.Result,
        }, ct);

    /// <summary>The S2C gspUpdateParty body for a party, leader first, with the idle state the main menu gates on.</summary>
    internal static byte[] WriteParty(PacketTables tables, string build, PartySnapshot party) =>
        WriteParty(tables, build, new PartyUpdated(party));

    /// <summary>
    /// The S2C gspUpdateParty body for a party list, optionally carrying the recipient's committed appearance in the
    /// slot kr-1256 reads as a keyed profile map (handler kr-1256 sub_9064B0 applies only the supplied entries).
    /// </summary>
    internal static byte[] WriteParty(PacketTables tables, string build, PartyUpdated message)
    {
        var fields = new Dictionary<string, object?>
        {
            // kr-812 sub_812AD0 lets the main menu leave for the plaza only in state 2; kr-1256 sub_99D510 reads the
            // same word at the party manager +4.
            ["state"] = 2,
            ["members"] = message.Party.Members.Select(member => new Dictionary<string, object?>
            {
                ["user_number"] = member.UserNum, ["nickname"] = member.Nickname, ["level"] = member.Level,
            }).ToArray(),
        };
        if (message.Appearance is { } appearance)
        {
            if (!message.Party.Members.Any(member => member.UserNum == appearance.UserNum))
                throw new InvalidOperationException("The scene recipient is absent from its party membership.");
            if (appearance.CharacterItemId is < 300000 or > 301999)
                throw new ArgumentException("A party appearance requires a resolved native character item.", nameof(message));
            fields["profile_entries"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["user_number"] = appearance.UserNum,
                    ["character_id"] = appearance.CharacterItemId,
                    ["equipped_items"] = appearance.EquippedItemIds.Where(id => id != appearance.CharacterItemId).ToArray(),
                },
            };
        }
        return PacketCodec.Write(tables, build, "SoloPartySnapshot", fields);
    }

    // The trailing word of the modern invite is not the legacy is_party flag (packets/party.yaml); the party UI is
    // the only sender of this request, so the typed message is always a party invitation.
    internal PartyInviteRequested ReadInvite(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "PartyInviteRequest", body);
        return fields.TryGetValue("is_party", out var isParty)
            ? new((bool)isParty!, (string)fields["target"]!)
            : new(true, (string)fields["target"]!);
    }

    /// <summary>
    /// Reads the accept. Where the build's body carries a kind byte, kind 1 answers an invitation prompt and kind 0 is
    /// the move of a granted visit (kr-1256 sub_9A9710); both join the inviting party, whose pending invitation the party
    /// row checks. Kind 2 (kr-1256 sub_9396F0) answers another prompt and is dropped.
    /// </summary>
    internal PartyInviteAccepted ReadAccept(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "PartyInviterMoveRequest", body);
        if (fields.TryGetValue("kind", out var kind) && Convert.ToByte(kind) is not (AcceptKind or VisitMoveKind))
            throw new NotSupportedException($"C2S gcpReqMoveToInviter kind {kind} is not the accept of a party invitation.");
        return fields.TryGetValue("is_party", out var isParty)
            ? new((bool)isParty!, (string)fields["inviter"]!)
            : new(true, (string)fields["inviter"]!);
    }

    internal PartyInviteTokenRefused ReadTokenRefusal(ReadOnlySpan<byte> body) =>
        new(Convert.ToUInt32(PacketCodec.Read(tables, build, "PartyInviteAnswer", body)["token"]));

    internal PartyInviteAnswered ReadAnswer(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "PartyInviteAnswer", body);
        return fields.TryGetValue("answer", out var answer)
            ? new((string)fields["inviter"]!, checked((int)Convert.ToUInt32(answer)))
            : new((string)fields["inviter"]!, 3); // tw-454 names the inviter and carries no answer code.
    }

    /// <summary>Reads the visit request: one nickname in every family.</summary>
    internal PartyVisitRequested ReadVisit(ReadOnlySpan<byte> body) =>
        new((string)PacketCodec.Read(tables, build, "MoveToFriendRequest", body)["target"]!, VisitFamily() != "direct");

    /// <summary>
    /// Reads the legacy gcpReqMoveToFriendAccept, which answers both prompts: answer 3 refuses an invitation
    /// (kr-812 sub_81DEA0), 1 accepts a visit and 0 or 2 declines it (kr-812 sub_81ED40 case 17).
    /// </summary>
    internal IMessage ReadFriendAnswer(ReadOnlySpan<byte> body)
    {
        var answer = ReadAnswer(body);
        return answer.Answer switch
        {
            3 => answer,
            1 => new PartyVisitAnswered(answer.Inviter, true),
            0 or 2 => new PartyVisitAnswered(answer.Inviter, false),
            _ => throw new NotSupportedException($"C2S gcpReqMoveToFriendAccept answer {answer.Answer} is not known."),
        };
    }

    /// <summary>Gets the build's visit family by its own request/reply names, including direct visits without approval.</summary>
    internal string? VisitFamily()
    {
        if (opcodes.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriend") is null) return null;
        if (opcodes.Find(PacketDirection.ServerToClient, "gspAskedVisitation") is not null) return "modern";
        if (opcodes.Find(PacketDirection.ServerToClient, "gspRepMoveToFriend") is not null)
            // cn-324 sub_706F70 has only direct result/error cases; it has no case 17 prompt or answer packet.
            return opcodes.Find(PacketDirection.ClientToServer, "gcpReqMoveToFriendAccept") is null ? "direct" : "legacy";
        throw new ConfigException($"Build '{build}' names C2S gcpReqMoveToFriend but no visit prompt.");
    }

    private void ValidateVisitLayouts(string family)
    {
        PacketLayoutContract.RequireSignature(tables, build, "MoveToFriendRequest", "Party", "target:wstr");
        if (family is "legacy" or "direct")
        {
            PacketLayoutContract.RequireSignature(tables, build, "MoveToFriendReply", "Party", "result:u32,nickname:wstr,unknown_36:u32");
            return;
        }
        PacketLayoutContract.RequireSignature(tables, build, "VisitationAsked", "Party", "token:u32,visitor:wstr,unknown_flag:bool");
        PacketLayoutContract.RequireSignature(tables, build, "VisitationAccept", "Party", "token:u32");
        PacketLayoutContract.RequireSignature(tables, build, "VisitationRefusal", "Party", "token:u32,reason:u32");
        PacketLayoutContract.RequireSignature(tables, build, "VisitationFailed", "Party", "nickname:wstr,code:u32");
        foreach (var name in new[] { "gcpReqAcceptVisitation_", "gcpReqRefuseVisitation_" })
            _ = opcodes.C2S(name);
        _ = opcodes.S2C("gspVisitationFailed");
        if (opcodes.Find(PacketDirection.ServerToClient, "gspVisitationAccepted") is not null)
            PacketLayoutContract.RequireSignature(tables, build, "VisitationAccepted", "Party", "nickname:wstr,address:RelayEndpoint");
    }

    private string Nickname(ReadOnlySpan<byte> body) => (string)PacketCodec.Read(tables, build, "PartyNickname", body)["nickname"]!;

    private ushort NoticeName() =>
        NoticeNames.Select(name => opcodes.Find(PacketDirection.ServerToClient, name)).FirstOrDefault(opcode => opcode is not null)
        ?? throw new ConfigException($"Build '{build}' names no invitation prompt (gspNotifyInvite or gspAskedInvitation).");

    private bool HasField(string structure, string field) => tables.Resolve(build, structure).Any(item => item.Name == field);

    private ValueTask Send(ISession session, string name, string structure, Dictionary<string, object?> fields, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C(name), PacketCodec.Write(tables, build, structure, fields), ct);

    private delegate T Reader<out T>(ReadOnlySpan<byte> body);

    private void Route<T>(IPluginContext context, IPacketRouter router, IMessageRouter messages, string name, Reader<T> read)
        where T : IMessage => RouteMessage(context, router, messages, name, body => read(body));

    private void RouteMessage(IPluginContext context, IPacketRouter router, IMessageRouter messages, string name, Reader<IMessage> read) =>
        context.Effect(router.Register(protocol, opcodes.C2S(name), async (session, packet, ct) =>
        {
            IMessage message;
            try
            {
                message = read(packet.Payload.Span);
            }
            catch (NotSupportedException ex)
            {
                context.Logger.LogWarning("Session {Id} C2S {Name} ignored: {Reason}", session.Id, name, ex.Message);
                return;
            }
            // The router selects handlers by the static message type, so a packet that carries either answer names it.
            var handled = message switch
            {
                PartyInviteAnswered answer => await messages.DispatchAsync(protocol, session, answer, ct).ConfigureAwait(false),
                PartyVisitAnswered answer => await messages.DispatchAsync(protocol, session, answer, ct).ConfigureAwait(false),
                PartyInviteRequested request => await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false),
                PartyInviteAccepted accept => await messages.DispatchAsync(protocol, session, accept, ct).ConfigureAwait(false),
                PartyInviteTokenRefused refusal => await messages.DispatchAsync(protocol, session, refusal, ct).ConfigureAwait(false),
                PartyVisitRequested request => await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false),
                PartyVisitTokenAnswered answer => await messages.DispatchAsync(protocol, session, answer, ct).ConfigureAwait(false),
                PartyLeaveRequested leave => await messages.DispatchAsync(protocol, session, leave, ct).ConfigureAwait(false),
                PartyKickRequested kick => await messages.DispatchAsync(protocol, session, kick, ct).ConfigureAwait(false),
                PartyLeaderChangeRequested change => await messages.DispatchAsync(protocol, session, change, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"{name} produced an unknown party message {message.GetType().Name}."),
            };
            if (!handled)
                throw new InvalidOperationException($"{name} has no typed party handler.");
        }));

    private static void ValidateLayouts(PacketTables tables, string build)
    {
        // The shapes a build may state; the binding casts and sizes exactly these fields.
        PacketLayoutContract.RequireSignature(tables, build, "PartyNickname", "Party", "nickname:wstr");
        PacketLayoutContract.RequireSignature(tables, build, "PartyNicknamePair", "Party", "first:wstr,second:wstr");
        // The shared structures have a legacy shape and, for the modern family, a table-driven variant.
        PacketLayoutContract.RequireSignature(tables, build, "PartyInviteRequest", "Party",
            "is_party:bool,target:wstr", "target:wstr,unknown_flag:bool");
        PacketLayoutContract.RequireSignature(tables, build, "PartyInviterMoveRequest", "Party",
            "is_party:bool,inviter:wstr,address:RelayEndpoint",
            "inviter:wstr,address:RelayEndpoint,kind:u8", "inviter:wstr,address:RelayEndpoint");
        PacketLayoutContract.RequireSignature(tables, build, "PartyInviteAnswer", "Party",
            "inviter:wstr,answer:u32", "token:u32", "inviter:wstr");
        PacketLayoutContract.RequireSignature(tables, build, "PartyInviteNotice", "Party",
            "is_party:bool,inviter:wstr,address:RelayEndpoint,auto_accept:bool",
            "inviter:wstr,token:u32,address:RelayEndpoint,unknown_36:u32,unknown_40:u32",
            "inviter:wstr,address:RelayEndpoint,auto_accept:bool");
    }
}
