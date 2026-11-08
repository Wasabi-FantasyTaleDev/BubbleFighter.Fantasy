using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates native plaza recruitment requests and existing-seat updates through audited build tables.</summary>
internal sealed class PlazaRecruitmentBinding(string protocol, IOpcodeTable opcodes, PacketTables packets,
    string build, ILogger logger) : IInboundMessageBinding<PlazaRecruitmentRequested>,
    IInboundMessageBinding<PlazaRecruitmentTextRequested>, IMessageSender<PlazaRecruitmentChanged>,
    IMessageSender<PlazaRecruitmentTextReceived>, IInboundMessageBinding<PlazaRecruitmentListRequested>,
    IMessageSender<PlazaRecruitmentListReceived>, IMessageSender<PlazaRecruitmentRejected>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        NativePlazaRecruitment.RequireActiveField(packets, build);
        PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentRequest", "Plaza recruitment",
            "active:bool", "unknown_0:u32,member_limit:u32,category:u8,message:wstr");
        var toggle = packets.Resolve(build, "PlazaRecruitmentRequest").Any(field => field.Name == "active");
        var request = toggle ? "gcpReqPartyRecruiting" : "gcpReqPublishPartyRecruitment_";
        _ = opcodes.C2S(request);
        _ = opcodes.S2C("gspRoomLeaveSlot");
        _ = packets.Resolve(build, "RoomSeatLeave");
        if (toggle)
        {
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentTextRequest", "Plaza recruitment", "message:wstr");
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentTextReply", "Plaza recruitment", "slot:u16,message:wstr");
            _ = opcodes.C2S("gcpRoomSendRecMsg");
            _ = opcodes.S2C("gspRoomReceiveRecruitMsg");
        }
        else
        {
            _ = opcodes.C2S("gcpReqCancelPartyRecruiting_");
            _ = opcodes.C2S("gcpReqPartyRecruitDocuments_");
            _ = opcodes.S2C("gspRepPartyRecruitDocument");
            _ = opcodes.S2C("gspFailedPartyRecruitingReq");
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentListRequest", "Plaza recruitment", "category:u8,page:u32");
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentListReply", "Plaza recruitment",
                "entries:list<PlazaRecruitmentDocument>,total:u32,page:u32");
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentDocument", "Plaza recruitment",
                "user_number:u32,nickname:wstr,unknown_28:u32,address:RelayEndpoint,message:wstr,category:u8,member_count:u32,member_limit:u32,unknown_76:u32");
            PacketLayoutContract.RequireSignature(packets, build, "PlazaRecruitmentFailure", "Plaza recruitment", "reason:u8");
        }
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaRecruitmentRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<PlazaRecruitmentChanged>>(this));
        RoomRoutes.Bind(context, protocol, opcodes, logger, request, ReadRequest);
        if (toggle)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaRecruitmentTextRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PlazaRecruitmentTextReceived>>(this));
            RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpRoomSendRecMsg", body =>
                new PlazaRecruitmentTextRequested((string)PacketCodec.ReadScalars(packets, build,
                    "PlazaRecruitmentTextRequest", body)["message"]!));
        }
        else
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PlazaRecruitmentListRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PlazaRecruitmentListReceived>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PlazaRecruitmentRejected>>(this));
            RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpReqPartyRecruitDocuments_", ReadListRequest);
            RoomRoutes.Bind(context, protocol, opcodes, logger, "gcpReqCancelPartyRecruiting_", body =>
            {
                RoomRoutes.RequireEmpty(body, "Plaza recruitment cancellation");
                return new PlazaRecruitmentRequested(false);
            });
        }
    }

    internal PlazaRecruitmentRequested ReadRequest(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "PlazaRecruitmentRequest", body, requireCanonicalBooleans: true);
        return fields.TryGetValue("active", out var active)
            ? new((bool)active!)
            : new(true, (string)fields["message"]!)
            {
                MemberLimit = (int)fields["member_limit"]!, Category = (byte)fields["category"]!,
            };
    }

    internal PlazaRecruitmentListRequested ReadListRequest(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "PlazaRecruitmentListRequest", body);
        return new((byte)fields["category"]!, unchecked((uint)(int)fields["page"]!));
    }

    public ValueTask SendAsync(ISession session, PlazaRecruitmentListReceived message, CancellationToken ct)
    {
        if (message.Entries.Count > 10 || message.Total < message.Entries.Count)
            throw new InvalidOperationException("Recruitment documents exceed the native page or total bounds.");
        var entries = message.Entries.Select(entry =>
        {
            var member = entry.Member;
            var recruitment = member.Recruitment ?? throw new InvalidOperationException("Recruitment document has no committed state.");
            return new Dictionary<string, object?>
            {
                ["user_number"] = member.UserNumber, ["nickname"] = member.Nickname,
                ["address"] = new Dictionary<string, object?> { ["ipv4_word"] = 0u, ["port"] = (ushort)0 },
                ["message"] = recruitment.Text, ["category"] = recruitment.Category,
                ["member_count"] = entry.MemberCount, ["member_limit"] = recruitment.MemberLimit,
            };
        }).ToArray();
        // kr-1256 sub_631590 and cn-774 sub_7B0800 copy the endpoint into the current connection's join request.
        return session.SendAsync(opcodes.S2C("gspRepPartyRecruitDocument"), PacketCodec.Write(packets, build,
            "PlazaRecruitmentListReply", new Dictionary<string, object?>
            {
                ["entries"] = entries, ["total"] = message.Total, ["page"] = message.Page,
            }), ct);
    }

    public ValueTask SendAsync(ISession session, PlazaRecruitmentRejected message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspFailedPartyRecruitingReq"), PacketCodec.Write(packets, build,
            "PlazaRecruitmentFailure", new Dictionary<string, object?> { ["reason"] = (byte)0 }), ct);

    public ValueTask SendAsync(ISession session, PlazaRecruitmentChanged message, CancellationToken ct)
    {
        var member = message.Member;
        var seat = new RoomMemberView(member.Slot, member.UserNumber, member.Nickname, member.Level, 0, false,
            member.CharacterItemId, member.EquippedItemIds) { ColorIndex = member.ColorIndex, Recruitment = member.Recruitment };
        // cn-279 sub_708040 and kr-812 sub_7FDE50 reconcile the installed identity without recreating its actor.
        return session.SendAsync(opcodes.S2C("gspRoomLeaveSlot"), RoomRecords.PlazaRecruitmentUpdate(packets, build, seat), ct);
    }

    public ValueTask SendAsync(ISession session, PlazaRecruitmentTextReceived message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRoomReceiveRecruitMsg"), PacketCodec.Write(packets, build,
            "PlazaRecruitmentTextReply", new Dictionary<string, object?>
            {
                ["slot"] = checked((ushort)message.Slot), ["message"] = message.Text,
            }), ct);
}
