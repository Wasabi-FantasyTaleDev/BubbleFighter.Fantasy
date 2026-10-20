using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Shop;

/// <summary>Adapts the empty balance check to the six-field money snapshot that refreshes a cash dialog.</summary>
internal sealed class ShopBalanceCodec(string protocol, ushort request, ushort reply)
    : IInboundMessageBinding<ShopBalanceRequested>, IMessageSender<ShopBalanceUpdated>
{
    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopBalanceRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ShopBalanceUpdated>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(Protocol, request, async (session, packet, ct) =>
        {
            // 0x56EB50 and 0x56E860 stamp the request without writing any fields.
            if (!packet.Payload.IsEmpty)
                throw new PacketFormatException("Cash balance check requires an empty body.", packet.Payload.Length);
            if (!await messages.DispatchAsync(Protocol, session, new ShopBalanceRequested(), ct).ConfigureAwait(false))
                throw new InvalidOperationException("No shop balance handler is installed.");
        }));
    }

    public ValueTask SendAsync(ISession session, ShopBalanceUpdated message, CancellationToken ct)
    {
        if (message.Wallet.UserNum != message.Player.UserNum)
            throw new InvalidOperationException("Balance publication contains another player's wallet.");
        // 0x9D2FF0 reads four UI switches, Lucci and Cash; 0x648810 -> 0xE1D5C0 enables the waiting button.
        return session.SendAsync(reply, MoneyStat(message.Wallet), ct);
    }

    // The six-word money snapshot: four UI switches, then the wallet's lucci and cash. The modern login slice
    // moved the login burst's copy of this body into the shared login adapter; this shop balance path keeps its
    // own copy until the shop moves onto the shared shop binding (slice S6).
    private static byte[] MoneyStat(PlayerWallet money)
    {
        var w = PacketWriter.Create();
        for (var i = 0; i < 4; i++) w.WriteInt32(0);
        w.WriteInt32(money.Lucci);
        w.WriteInt32(money.Cash);
        return ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
