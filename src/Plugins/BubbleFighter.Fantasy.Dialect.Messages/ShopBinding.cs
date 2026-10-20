using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Owns selected legacy purchase packet layouts; the connection's inventory
/// time origin lives in the row-wide <see cref="InventoryBaselineScope"/> so the
/// shop state and the inventory restatements write against the same anchor.</summary>
internal sealed class ShopBinding(string protocol, IOpcodeTable table,
    InventoryBaselineScope scope, PacketTables tables, string build)
    : IMessageSender<ShopStateUpdated>, IMessageSender<ShopPurchaseReplied>, IMessageSender<ShopBalanceUpdated>,
      IInboundMessageBinding<ShopPurchaseRequested>, IInboundMessageBinding<ShopInventoryRequested>,
      IInboundMessageBinding<ShopBalanceRequested>
{
    private readonly ushort _full = table.S2C("gspInventoryUpdateAllInventorySlot");
    private readonly ushort _update = table.S2C("gspInventoryUpdateInventorySlots");
    private readonly ushort _stat = table.S2C("gspUserStat");
    private readonly ushort _reply = table.S2C("gspRepShopBuy");
    private readonly InventoryWire _inventory = new(tables, build);

    public string Protocol => protocol;

    internal static ShopBalanceRequested ReadBalanceRequest(ReadOnlySpan<byte> body)
    {
        // Cash checks carry no payload; purchase and slot codecs below supply the nonempty shop records.
        if (!body.IsEmpty)
            throw new PacketFormatException("Cash balance check requires an empty body.", body.Length);
        return new();
    }

    public ValueTask SendAsync(ISession session, ShopBalanceUpdated message, CancellationToken ct)
    {
        if (message.Wallet.UserNum != message.Player.UserNum)
            throw new InvalidOperationException("Balance publication contains another player's wallet.");
        // 0x81A820 -> 0x96FB40 -> 0x96F710 enables the waiting cash dialog on a fresh S105.
        return session.SendAsync(_stat, WriteStat(tables, build, message.Player.Nickname, message.Player.Level,
            message.Wallet.Lucci, message.Wallet.Cash, message.Player.ColorIndex, message.Player.UserNum), ct);
    }

    // Login and shop refreshes use the same audited UserStat fields.
    internal static byte[] WriteStat(PacketTables tables, string build, string nickname, int level, int lucci, int cash,
        ushort colorIndex = 0, long userNum = 0) =>
        UserStatWire.Write(tables, build, nickname, level, lucci, cash, colorIndex, userNum);

    internal static ShopPurchaseRequested ReadPurchase(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "ShopPurchaseRequest", body,
            requireCanonicalBooleans: true);
        return new((int)values["key"], !(bool)values["unknown_8"]);
    }

    public async ValueTask SendAsync(ISession session, ShopStateUpdated message, CancellationToken ct)
    {
        if (message.Wallet.UserNum != message.Player.UserNum || message.Slots.Any(s =>
            s.UserNum != message.Player.UserNum || s.Bag != InventoryBag.CharacterBag))
            throw new InvalidOperationException("Shop publication contains another player's state.");
        var baseline = message.FullInventory
            ? scope.Reset(session.Connection.Id)
            : scope.Require(session.Connection.Id);
        var slots = message.Slots.Select(s => InventoryBinding.Map(s, baseline)).ToArray();
        var inventory = _inventory.RepInventoryAll(slots);
        var stat = WriteStat(tables, build, message.Player.Nickname, message.Player.Level, message.Wallet.Lucci,
            message.Wallet.Cash, message.Player.ColorIndex, message.Player.UserNum);
        await session.SendAsync(message.FullInventory ? _full : _update, inventory, ct).ConfigureAwait(false);
        await session.SendAsync(_stat, stat, ct).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ISession session, ShopPurchaseReplied message, CancellationToken ct) =>
        session.SendAsync(_reply, WriteReply(tables, build, message), ct);

    /// <summary>Writes u32 shop key and u32 result; kr-812 sub_827550 consumes result 1 as success.</summary>
    internal static byte[] WriteReply(PacketTables tables, string build, ShopPurchaseReplied message) =>
        PacketCodec.Write(tables, build, "ShopPurchaseReply", new Dictionary<string, object?>
        {
            ["key"] = message.Key, ["result"] = message.Succeeded ? 1 : 0,
        });
}
