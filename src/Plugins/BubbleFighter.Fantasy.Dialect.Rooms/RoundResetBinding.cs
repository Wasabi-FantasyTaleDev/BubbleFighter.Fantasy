using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Adapts native life resets to the shared life state machine using the build's packet tables.</summary>
internal sealed class RoundResetBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundResetRequested>, IMessageSender<RoundResetGranted>
{
    private ushort _reply;

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        // cn-774 sub_8B5670 and tw-454 sub_729D30 read three plain words and a trailing seat word;
        // kr-1256 sub_6F5E50 instead reads two tagged values. The tables own that distinction.
        var requestName = table.Find(PacketDirection.ClientToServer, "gcpRespawn") is not null ? "gcpRespawn" : "Unknown602";
        _reply = table.Find(PacketDirection.ServerToClient, "gspRespawn")
            ?? table.Find(PacketDirection.ServerToClient, "Unknown632")
            ?? throw new ConfigException("Native life resets require a named S2C reset grant.");
        PacketLayoutContract.RequireFields(packets, build, "RoundResetRequest", "Native life resets", ("actor", "u32"));
        var request = packets.Resolve(build, "RoundResetRequest");
        var reply = packets.Resolve(build, "RoundResetReply");
        if (reply.Count != request.Count + 1 || reply[^1].Type != "u32" || reply[^1].Default != 0
            || !request.Select(field => (field.Name, field.Type)).SequenceEqual(reply.Take(request.Count).Select(field => (field.Name, field.Type))))
            throw new ConfigException("RoundResetReply must echo RoundResetRequest and append the default-zero seat word.");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundResetRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundResetGranted>>(this));
        RoomRoutes.Bind(context, protocol, table, logger, requestName, body =>
        {
            var values = Read(packets, build, "RoundResetRequest", body);
            return new RoundResetRequested((int)values["actor"]!, body.ToArray());
        });
    }

    public ValueTask SendAsync(ISession session, RoundResetGranted message, CancellationToken ct) =>
        session.SendAsync(_reply, PacketCodec.Write(packets, build, "RoundResetReply",
            Read(packets, build, "RoundResetRequest", message.Request.Span)), ct);

    internal static Dictionary<string, object?> Read(PacketTables packets, string build, string structure, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, structure, body);
        // kr-1256 sub_10C04D0/sub_10C0640 accept 0x3A with a word or the absent 0xBA marker.
        foreach (var field in packets.Resolve(build, structure))
            if (field.Type.StartsWith("optional<", StringComparison.Ordinal)
                && values[field.Name] is byte marker && marker != field.Default)
                throw new PacketFormatException($"{structure}.{field.Name} has an invalid absent-value tag.", 0);
        return values;
    }
}
