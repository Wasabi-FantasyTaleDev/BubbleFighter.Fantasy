using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The clan-server connection's handshake packets (layouts in <c>packets/clan-server.yaml</c>, numbers from the
/// build's <c>connection: clan</c> opcode rows by name): the heartbeat and the login in; the hello and the login
/// result out. The dispatcher that consumes the two server frames is kr-1256 sub_525170 (cases 0x3EA and 0x3EE);
/// the writers are kr-1256 sub_525050 (heartbeat) and kr-1256 sub_525DF0 (login).
/// </summary>
internal sealed class ClanServerBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<ClanServerHeartbeatReceived>, IInboundMessageBinding<ClanServerLoginRequested>,
      IMessageSender<ClanServerHello>, IMessageSender<ClanServerLoginAnswered>
{
    /// <summary>The clan-connection C2S names the binding reads.</summary>
    internal static readonly string[] Requests = ["ClanHeartbeat", "ClanLogin"];

    /// <summary>The clan-connection S2C names the binding writes.</summary>
    internal static readonly string[] Replies = ["ClanHello", "ClanLoginResult"];

    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        ValidateLayouts();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanServerHeartbeatReceived>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ClanServerLoginRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanServerHello>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ClanServerLoginAnswered>>(this));
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(router.Register(protocol, opcodes.C2S("ClanHeartbeat"), async (session, packet, ct) =>
        {
            // kr-1256 sub_525050 stamps the opcode and writes no field.
            if (!packet.Payload.Span.IsEmpty)
                throw new PacketFormatException("ClanHeartbeat must be empty.", 0);
            await DispatchAsync(messages, session, new ClanServerHeartbeatReceived(), "ClanHeartbeat", ct).ConfigureAwait(false);
        }));
        context.Effect(router.Register(protocol, opcodes.C2S("ClanLogin"), async (session, packet, ct) =>
            await DispatchAsync(messages, session, ReadLogin(tables, build, packet.Payload.Span), "ClanLogin", ct).ConfigureAwait(false)));
    }

    public ValueTask SendAsync(ISession session, ClanServerHello message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("ClanHello"), ReadOnlyMemory<byte>.Empty, ct);

    public ValueTask SendAsync(ISession session, ClanServerLoginAnswered message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("ClanLoginResult"), WriteLoginResult(tables, build, message.Result), ct);

    /// <summary>
    /// Reads the clan login (writer kr-1256 sub_525DF0): the account id, the NUL-terminated nickname, two echoed
    /// words the server does not use, and the counted ticket bytes.
    /// </summary>
    internal static ClanServerLoginRequested ReadLogin(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "ClanServerLogin", body);
        var ticket = (object?[])fields["ticket"]!;
        var bytes = new byte[ticket.Length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(ticket[i], System.Globalization.CultureInfo.InvariantCulture);
        return new ClanServerLoginRequested(
            Convert.ToInt32(fields["user_number"], System.Globalization.CultureInfo.InvariantCulture),
            (string)fields["nickname"]!, bytes);
    }

    /// <summary>The one-word login result the client switches on (kr-1256 sub_525170 case 0x3EE).</summary>
    internal static byte[] WriteLoginResult(PacketTables tables, string build, ClanServerLoginResult result) =>
        PacketCodec.Write(tables, build, "ClanServerLoginResult",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = (int)result });

    private async ValueTask DispatchAsync<T>(IMessageRouter messages, ISession session, T message, string name, CancellationToken ct)
        where T : IMessage
    {
        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{name} has no typed clan handler.");
    }

    private void ValidateLayouts()
    {
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerHello", "Clan connection");
        PacketLayoutContract.RequireEmpty(tables, build, "ClanServerHeartbeat", "Clan connection");
        PacketLayoutContract.RequireFields(tables, build, "ClanServerLogin", "Clan connection",
            ("user_number", "u32"), ("nickname", "wcstr"), ("ticket", "list<u8>"));
        PacketLayoutContract.RequireSignature(tables, build, "ClanServerLoginResult", "Clan connection", "result:u32");
        foreach (var name in Replies)
            _ = opcodes.S2C(name);
        foreach (var name in Requests)
            _ = opcodes.C2S(name);
    }
}
