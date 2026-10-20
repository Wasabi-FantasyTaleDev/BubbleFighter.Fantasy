using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Shop.Wire;

/// <summary>
/// Parsed <c>gcpEnterShop</c>. kr-1256 builder <c>sub_9328B0</c> writes an <c>i64</c> then an
/// <c>i32</c>. The <c>i32</c> is the builder's <c>u16</c> argument stored in 4 bytes; its
/// meaning is unproven, so it stays <c>unknown4</c>.
/// </summary>
/// <param name="Token">The 8-byte value copied from <c>dword_18E56E8+37</c>.</param>
/// <param name="Unknown4">The 4-byte slot after the token.</param>
public sealed record EnterShopRequest(long Token, int Unknown4)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static EnterShopRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var token = r.ReadInt64();
        var unknown4 = r.ReadInt32();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 801 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new EnterShopRequest(token, unknown4);
    }
}

/// <summary>
/// Parsed id-400 <c>gcpEnterShop</c>. id-400 writer <c>sub_63B750</c> sends only the f64 cache
/// token, no main category.
/// </summary>
/// <param name="Token">The 8-byte cache-revision value.</param>
public sealed record EnterShopRequest400(long Token)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static EnterShopRequest400 Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var token = r.ReadInt64();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 801 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new EnterShopRequest400(token);
    }
}

/// <summary>
/// The cn-324 / cn-279 <c>gcpEnterShop</c>: the writer is a nullsub on both builds, so
/// the body is empty.
/// </summary>
public sealed record EnterShopRequestEmpty
{
    /// <summary>Parses the payload strictly; any byte throws <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static EnterShopRequestEmpty Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0)
            throw new PacketFormatException($"C2S 801 carries {payload.Length} byte(s); the writer sends none.", payload.Length);
        return new EnterShopRequestEmpty();
    }
}

/// <summary>
/// Legacy C2S 808 <c>gcpReqShopBuy</c>. kr-812 writer <c>sub_503440</c> stores a key and a
/// bool in four bytes. The shop confirm UI writes the bool as 0; the value is not a quantity.
/// </summary>
/// <param name="Key">The table key from the buy button.</param>
/// <param name="Unknown4">The bool widened to four bytes.</param>
public sealed record LegacyShopBuyRequest(int Key, int Unknown4)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static LegacyShopBuyRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var key = r.ReadInt32();
        var unknown4 = r.ReadInt32();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 808 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new LegacyShopBuyRequest(key, unknown4);
    }
}

/// <summary>
/// Parsed <c>gcpEnterTCShop_</c>, the cash shop entry. The kr-1256 send site in <c>sub_64DAB0</c> writes one <c>i64</c>
/// from <c>dword_18E56E8+38</c>.
/// </summary>
/// <param name="Token">The 8-byte value copied from <c>dword_18E56E8+38</c>.</param>
public sealed record EnterTCShopRequest(long Token)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static EnterTCShopRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var token = r.ReadInt64();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 802 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new EnterTCShopRequest(token);
    }
}

/// <summary>
/// Parsed <c>gcpReqShopBuy</c>. kr-1256 builder <c>sub_E1D720</c> writes the item <c>i32</c> and then a
/// second <c>i32</c> whose source is a <c>u8</c> at object +4.
/// </summary>
/// <param name="ItemId">The first <c>i32</c> written by kr-1256 <c>sub_E1D720</c>.</param>
/// <param name="Unknown4">The second <c>i32</c>; source width is one byte, wire width is 4.</param>
public sealed record ShopBuyRequest(int ItemId, int Unknown4)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static ShopBuyRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var itemId = r.ReadInt32();
        var unknown4 = r.ReadInt32();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 809 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new ShopBuyRequest(itemId, unknown4);
    }
}

/// <summary>
/// Parsed <c>gcpReqShopItemCardInfo</c>. kr-1256 builder <c>sub_814490</c> writes one <c>i32</c>.
/// </summary>
/// <param name="ItemId">The sole <c>i32</c> written by kr-1256 <c>sub_814490</c>.</param>
public sealed record ShopItemCardInfoRequest(int ItemId)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static ShopItemCardInfoRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var itemId = r.ReadInt32();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 810 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new ShopItemCardInfoRequest(itemId);
    }
}

/// <summary>
/// Parsed <c>gcpReqRealCouponUse</c>. kr-1256 <c>sub_E30E70</c> on feature 1010 writes one
/// prefixed wide string via kr-1256 <c>sub_40DF60</c>.
/// </summary>
/// <param name="Unknown0">The counted wide payload. Meaning unproven.</param>
public sealed record RealCouponUseRequest(string Unknown0)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RealCouponUseRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadWideStringPrefixed();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 833 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new RealCouponUseRequest(unknown0);
    }
}

/// <summary>
/// Parsed <c>Unknown834</c>. kr-1256 <c>sub_E30E70</c> on feature 1018 writes one prefixed wide string, a
/// code of 17 to 20 characters typed in the coupon dialog; the row answers it with the cash balance.
/// </summary>
/// <param name="Unknown1">The counted wide payload. Meaning unproven.</param>
public sealed record CheckCashRequest(string Unknown1)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static CheckCashRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown1 = r.ReadWideStringPrefixed();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 834 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new CheckCashRequest(unknown1);
    }
}

/// <summary>
/// <c>Unknown2059</c> from kr-1256 builder <c>sub_92F6C0</c>: <c>u8</c> then one <c>i32</c>. The kr-1256
/// shop entry <c>sub_9328B0</c> sends it as <c>sub_92F6C0(0xD, 0)</c>, so the live body is
/// <c>0D 00 00 00 00</c>. The builder latches at <c>dword_18E566C+984</c> and sends once; no reply
/// path exists.
/// </summary>
/// <param name="Unknown0">The <c>u8</c>; live <c>0x0D</c> from the shop entry.</param>
/// <param name="Unknown4">The <c>i32</c>; live <c>0</c> from the shop entry. No reader traced.</param>
public sealed record SceneBootstrapRequest(byte Unknown0, int Unknown4)
{
    /// <summary>Parses the payload strictly; any trailing or missing bytes throw <see cref="PacketFormatException"/>.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static SceneBootstrapRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadByte();
        var unknown4 = r.ReadInt32();
        if (r.Remaining != 0)
            throw new PacketFormatException($"C2S 2059 has {r.Remaining} trailing byte(s).", payload.Length - r.Remaining);
        return new SceneBootstrapRequest(unknown0, unknown4);
    }
}
