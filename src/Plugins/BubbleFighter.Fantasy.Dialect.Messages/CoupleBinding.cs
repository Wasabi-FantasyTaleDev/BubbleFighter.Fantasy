using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The couple requests and replies of <c>packets/couple.yaml</c> for a build with the <c>couple</c> capability. The
/// numeric proposal results and breakup statuses are the build's <c>couple_proposal_results</c> and
/// <c>couple_breakup_statuses</c> in <c>packets/builds.yaml</c>; every body is written by its field names.
/// </summary>
internal sealed class CoupleBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<CoupleProposed>, IInboundMessageBinding<CoupleProposalLapsed>,
      IInboundMessageBinding<CoupleAnswered>, IInboundMessageBinding<CoupleBreakUpRequested>,
      IInboundMessageBinding<CoupleRingChangeRequested>,
      IInboundMessageBinding<CoupleInviterStatusRequested>, IInboundMessageBinding<CoupleInitialInviteRequested>,
      IMessageSender<CoupleProposalDelivered>, IMessageSender<CoupleProposalReplied>, IMessageSender<CoupleAnswerPublished>,
      IMessageSender<CoupleBreakUpReplied>, IMessageSender<CouplePartnerPublished>, IMessageSender<CoupleRingChangeReplied>,
      IMessageSender<CoupleProfilePublished>, IMessageSender<CoupleInviterStatusPublished>, IMessageSender<CoupleConfirmationReady>
{
    private readonly FighterInfoBinding _fighters = new(protocol, opcodes, tables, build);
    private IReadOnlyDictionary<CoupleProposalResult, uint> _results = new Dictionary<CoupleProposalResult, uint>();
    private readonly IReadOnlyDictionary<CoupleBreakUpStatus, uint> _statuses =
        ReadCodes<CoupleBreakUpStatus>(tables, build, "couple_breakup_statuses");

    /// <summary>Gets the game connection this binding serves.</summary>
    public string Protocol => protocol;

    /// <summary>Checks the layouts and codes, then registers the request routes and the reply senders.</summary>
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        var ring = opcodes.Find(PacketDirection.ClientToServer, "gcpReqChangeCouplering") is not null;
        RequireLayouts(tables, build, ring);
        _results = ReadCodes<CoupleProposalResult>(tables, build, "couple_proposal_results");
        foreach (var name in new[] { "gspResCoupleInvite", "gspResCoupleInviteResult", "gspResCoupleInviteAnswer",
                     "gspResCoupleBreakUp", "gspResCoupleNotifyPartner" })
            _ = opcodes.S2C(name);
        if (ring) _ = opcodes.S2C("gspResChangeCouplering");
        var router = context.Services.Get<IPacketRouter>();
        void Route<T>(string name, Func<ReadOnlySpan<byte>, T> read) where T : IMessage =>
            context.Effect(router.Register(protocol, opcodes.C2S(name), async (session, packet, ct) =>
            {
                var request = read(packet.Payload.Span);
                if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"{name} has no typed handler.");
            }));
        Route("gcpReqCoupleConfirmInvite", body => ReadProposal(tables, build, body));
        Route("gcpReqCoupleInviteExpire", body =>
        {
            _ = PacketCodec.ReadScalars(tables, build, "CoupleInviteExpire", body);
            return new CoupleProposalLapsed(true);
        });
        Route("gcpReqCoupleInviteAnswer", body => ReadAnswer(tables, build, body));
        Route("gcpReqCoupleBreakUp", body => new CoupleBreakUpRequested(
            Text(PacketCodec.ReadScalars(tables, build, "CoupleBreakUpRequest", body), "partner")));
        if (ring)
            Route("gcpReqChangeCouplering", body => new CoupleRingChangeRequested(
                Number(PacketCodec.ReadScalars(tables, build, "CoupleChangeRingRequest", body), "ring_item")));
        var preflight = opcodes.Find(PacketDirection.ClientToServer, "gcpReqCoupleInviterStatus") is not null;
        if (preflight)
        {
            _ = opcodes.S2C("gspResCoupleInviterStatus");
            _ = ReadCodes<CoupleInviterStatus>(tables, build, "couple_inviter_statuses");
            _ = ConfirmationCode(tables, build);
            PacketLayoutContract.RequireFields(tables, build, "CoupleInviterStatusReply", "Couple eligibility", ("status", "u32"));
            PacketLayoutContract.RequireFields(tables, build, "CoupleInitialInviteRequest", "Couple target", ("selected", "bool"), ("target", "wstr"));
            Route("gcpReqCoupleInviterStatus", body =>
            {
                _ = PacketCodec.Read(tables, build, "CoupleInviterStatusRequest", body);
                return new CoupleInviterStatusRequested();
            });
            Route("gcpReqCoupleInvite", body => ReadInitialInvite(tables, build, body));
        }
        var services = context.Services;
        if (preflight)
        {
            context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleInviterStatusRequested>>(this));
            context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleInitialInviteRequested>>(this));
            context.Effect(services.RegisterMany<IMessageSender<CoupleInviterStatusPublished>>(this));
            context.Effect(services.RegisterMany<IMessageSender<CoupleConfirmationReady>>(this));
        }
        if (opcodes.Find(PacketDirection.ServerToClient, "gspUpdateMyCoupledInfo") is not null)
        {
            CoupleProfileWire.RequireLayouts(tables, build);
            context.Effect(services.RegisterMany<IMessageSender<CoupleProfilePublished>>(this));
        }
        context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleProposed>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleProposalLapsed>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleAnswered>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleBreakUpRequested>>(this));
        if (ring) context.Effect(services.RegisterMany<IInboundMessageBinding<CoupleRingChangeRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<CoupleProposalDelivered>>(this));
        context.Effect(services.RegisterMany<IMessageSender<CoupleProposalReplied>>(this));
        context.Effect(services.RegisterMany<IMessageSender<CoupleAnswerPublished>>(this));
        context.Effect(services.RegisterMany<IMessageSender<CoupleBreakUpReplied>>(this));
        context.Effect(services.RegisterMany<IMessageSender<CouplePartnerPublished>>(this));
        if (ring) context.Effect(services.RegisterMany<IMessageSender<CoupleRingChangeReplied>>(this));
    }

    /// <summary>Requires every couple structure this binding reads or writes, with the field types it uses.</summary>
    internal static void RequireLayouts(PacketTables tables, string build, bool ring = true)
    {
        PacketLayoutContract.RequireFields(tables, build, "CoupleConfirmInviteRequest", "Couple proposal",
            ("confirmed", "bool"), ("target", "wstr"), ("ring_item", "u32"), ("message", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "CoupleInviteAnswerRequest", "Couple answer",
            ("accepted", "bool"), ("inviter", "wstr"), ("recipient", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "CoupleBreakUpRequest", "Couple breakup", ("partner", "wstr"));
        if (ring) PacketLayoutContract.RequireFields(tables, build, "CoupleChangeRingRequest", "Couple ring", ("ring_item", "u32"));
        if (tables.Resolve(build, "CoupleInviteExpire").Count == 0)
            throw new ConfigException($"Build '{build}' has no verified couple proposal expiry layout.");
        PacketLayoutContract.RequireFields(tables, build, "CoupleInvite", "Couple invitation",
            ("shown", "bool"), ("inviter", "wstr"), ("recipient", "wstr"), ("message", "wstr"), ("fighter_info", "FighterInfo"));
        PacketLayoutContract.RequireFields(tables, build, "CoupleInviteResult", "Couple proposal result",
            ("result", "u32"), ("name", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "CoupleInviteAnswerReply", "Couple answer",
            ("accepted", "bool"), ("inviter", "wstr"), ("recipient", "wstr"));
        var breakup = tables.Resolve(build, "CoupleBreakUpReply");
        if (breakup.SingleOrDefault(field => field.Name == "status")?.Type is not ("u32" or "bool"))
            throw new ConfigException($"Build '{build}' needs a numeric or boolean couple breakup status.");
        foreach (var field in breakup.Where(field => field.Name != "status"))
            if (field.Type != "wstr")
                throw new ConfigException($"Build '{build}' has an unverified couple breakup field '{field.Name}'.");
        PacketLayoutContract.RequireFields(tables, build, "CoupleNotifyPartner", "Couple partner",
            ("partner_online", "bool"), ("partner_user_number", "u32"), ("partner_name", "wstr"));
        if (ring) PacketLayoutContract.RequireFields(tables, build, "CoupleChangeRingReply", "Couple ring", ("ring_result", "u32"), ("ring_item", "u32"));
    }

    /// <summary>Reads a numeric code of every enum member from the build's values; a missing one fails the mount.</summary>
    internal static IReadOnlyDictionary<TEnum, uint> ReadCodes<TEnum>(PacketTables tables, string build, string key)
        where TEnum : struct, Enum
    {
        if (tables.ValuesOf(build).GetValueOrDefault(key) is not IReadOnlyDictionary<string, object?> values)
            throw new ConfigException($"Build '{build}' needs {key} for its couple packets.");
        var codes = new Dictionary<TEnum, uint>();
        foreach (var member in Enum.GetValues<TEnum>())
        {
            var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(member.ToString());
            if (!values.TryGetValue(name, out var raw) || !uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new ConfigException($"Build '{build}' {key}.{name} must be an unsigned code.");
            codes.Add(member, code);
        }
        return codes;
    }

    /// <summary>Reads a proposal.</summary>
    internal static CoupleProposed ReadProposal(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        // Some clients append their equipment; validate the full body but use only the server-owned appearance.
        var values = PacketCodec.Read(tables, build, "CoupleConfirmInviteRequest", body);
        return new(Convert.ToBoolean(values["confirmed"], CultureInfo.InvariantCulture), Text(values, "target"),
            Number(values, "ring_item"), Text(values, "message"));
    }

    /// <summary>Reads an answer.</summary>
    internal static CoupleAnswered ReadAnswer(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "CoupleInviteAnswerRequest", body);
        return new(Convert.ToBoolean(values["accepted"], CultureInfo.InvariantCulture), Text(values, "inviter"), Text(values, "recipient"));
    }

    /// <summary>Reads the native target check before the ring-and-message confirmation.</summary>
    internal static CoupleInitialInviteRequested ReadInitialInvite(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "CoupleInitialInviteRequest", body);
        return new(Convert.ToBoolean(values["selected"], CultureInfo.InvariantCulture), Text(values, "target"));
    }

    /// <summary>Writes the build's own selector eligibility result.</summary>
    internal byte[] WriteInviterStatus(CoupleInviterStatus status) =>
        PacketCodec.Write(tables, build, "CoupleInviterStatusReply", new Dictionary<string, object?>
        {
            ["status"] = ReadCodes<CoupleInviterStatus>(tables, build, "couple_inviter_statuses")[status],
        });

    /// <summary>Writes the result which opens final confirmation, with the canonical target name.</summary>
    internal byte[] WriteConfirmation(string target) =>
        PacketCodec.Write(tables, build, "CoupleInviteResult", new Dictionary<string, object?>
        {
            ["result"] = ConfirmationCode(tables, build), ["name"] = target,
        });

    private static uint ConfirmationCode(PacketTables tables, string build) =>
        tables.ValuesOf(build).TryGetValue("couple_confirmation_result", out var value)
        && uint.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code : throw new ConfigException($"Build '{build}' needs a native couple_confirmation_result.");

    /// <summary>Writes an invitation with the proposer's equipment block and fighter record.</summary>
    internal byte[] WriteInvitation(CoupleProposalDelivered message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var appearance = message.Appearance;
        var equipmentField = tables.Resolve(build, "CoupleInvite").Single(field => field.Name == "equipped_items");
        var equipment = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = message.Inviter.UserNum, ["character_item"] = appearance.CharacterItemId,
            ["character_id"] = appearance.CharacterItemId, ["equipped_items"] = appearance.EquippedItemIds,
        };
        ItemAttributeWire.Apply(tables, build, equipmentField.Type, equipment, appearance.ItemAttributes);
        return PacketCodec.Write(tables, build, "CoupleInvite", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // The handler opens the proposal box only when this flag and the names are set (kr-1256 sub_9BEEF0).
            ["shown"] = true, ["inviter"] = message.Inviter.Nickname, ["recipient"] = message.Recipient,
            ["message"] = message.Message, ["equipped_items"] = equipment,
            ["fighter_info"] = _fighters.FighterRecord(message.Inviter, appearance),
        });
    }

    /// <summary>Writes a breakup reply: the build's status code and, where the build has them, the names.</summary>
    internal byte[] WriteBreakUp(CoupleBreakUpReplied message) =>
        PacketCodec.Write(tables, build, "CoupleBreakUpReply", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = _statuses[message.Status],
            // kr-1256 sub_9C15C0 reads one name: the other fighter, whom the message names.
            ["name"] = message.Status == CoupleBreakUpStatus.Left ? message.Actor : message.Partner,
            // cn-774 sub_BBF1A0 / tw-454 sub_A9C220 read the initiator and former partner before the status.
            ["actor"] = message.Actor, ["partner"] = message.Partner,
        });

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleProposalDelivered message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleInvite"), WriteInvitation(message), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleProposalReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleInviteResult"), PacketCodec.Write(tables, build, "CoupleInviteResult",
            new Dictionary<string, object?> { ["result"] = _results[message.Result], ["name"] = message.Name }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleAnswerPublished message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleInviteAnswer"), PacketCodec.Write(tables, build, "CoupleInviteAnswerReply",
            new Dictionary<string, object?>
            {
                ["accepted"] = message.Accepted, ["inviter"] = message.Inviter, ["recipient"] = message.Recipient,
            }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleBreakUpReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleBreakUp"), WriteBreakUp(message), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CouplePartnerPublished message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleNotifyPartner"), PacketCodec.Write(tables, build, "CoupleNotifyPartner",
            new Dictionary<string, object?>
            {
                ["partner_online"] = message.PartnerOnline, ["partner_user_number"] = checked((uint)message.PartnerUserNum),
                ["partner_name"] = message.PartnerName,
            }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleRingChangeReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResChangeCouplering"), PacketCodec.Write(tables, build, "CoupleChangeRingReply",
            new Dictionary<string, object?>
            {
                // A refusal is the client's negative result word; zero is the change (kr-1256 sub_9BF1D0).
                ["ring_result"] = message.Changed ? 0u : unchecked((uint)-1), ["ring_item"] = message.RingItemId,
            }), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleProfilePublished message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspUpdateMyCoupledInfo"), CoupleProfileWire.Write(tables, build, message.Profile), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleInviterStatusPublished message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleInviterStatus"), WriteInviterStatus(message.Status), ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ISession session, CoupleConfirmationReady message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResCoupleInviteResult"), WriteConfirmation(message.Target), ct);

    private static string Text<TValue>(IReadOnlyDictionary<string, TValue> values, string name) =>
        values[name] as string ?? throw new PacketFormatException($"Couple field {name} is not a string.", 0);

    private static int Number<TValue>(IReadOnlyDictionary<string, TValue> values, string name) =>
        unchecked((int)Convert.ToUInt32(values[name], CultureInfo.InvariantCulture));
}
