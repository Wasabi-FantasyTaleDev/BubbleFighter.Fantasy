using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

internal sealed class CenterPreloginBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build,
    bool prelogin = true, bool migration = false) : IInboundMessageBinding<MoveIntoRequested>
{
    private const string Continuation = "dialect.messages.center-prelogin";
    public string Protocol => protocol;

    internal void Bind(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(tables, build, "ChannelMoveIntoRequest", "Center prelogin",
            "unknown_4:u32,unknown_8:u32");
        var request = opcodes.C2S("gcpReqMoveInto");
        if (prelogin)
        {
            PacketLayoutContract.RequireSignature(tables, build, "gspRepGamePatch", "Center prelogin",
                "requires_patch:bool,command_line:wstr");
            _ = opcodes.S2C("gspRepGamePatch");
        }
        if (migration) context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MoveIntoRequested>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, request, async (session, packet, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var fields = PacketCodec.ReadScalars(tables, build, "ChannelMoveIntoRequest", packet.Payload.Span);
            // kr-812 sub_813060 and kr-1256 sub_99DD00 send retained identity and stage, not a credential.
            var identity = (long)unchecked((uint)(int)fields["unknown_4"]);
            var stage = unchecked((uint)(int)fields["unknown_8"]);
            if (stage > 1 || session.TryFeature<IPlayerSession>(out _)
                || identity == 0 && !prelogin || identity != 0 && !migration)
                throw new PacketFormatException("Native move request is not permitted for this connection.", 0);
            if (!session.Connection.Items.TryAdd(Continuation, true))
                throw new PacketFormatException("Native move admission was already requested.", 0);
            if (identity != 0)
            {
                if (!await context.Services.Get<IMessageRouter>().DispatchAsync(protocol, session, new MoveIntoRequested(identity, stage), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Native migration requires a destination admission handler.");
                return;
            }
            var admission = session.Feature<IInitialHandshakeContinuation>();
            var patch = PacketCodec.Write(tables, build, "gspRepGamePatch", new Dictionary<string, object?>
            {
                ["requires_patch"] = false, ["command_line"] = "",
            });
            // kr-1256 sub_9A1250 requests stage 10 on false; account state is granted only by later login.
            await session.SendAsync(opcodes.S2C("gspRepGamePatch"), patch, ct).ConfigureAwait(false);
            admission.ContinueWithoutVersionVerification();
        }));
    }
}
