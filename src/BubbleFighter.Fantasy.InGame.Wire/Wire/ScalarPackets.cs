using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>Empty C2S bodies: 17 (<c>sub_9E7570</c>), 506 (<c>sub_75F3F0</c>), 577, 625.</summary>
public static class EmptyPayload
{
    /// <summary>Requires a zero-length payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <param name="name">Opcode label for the error.</param>
    public static void Parse(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length != 0)
            throw new PacketFormatException($"{name} must be empty, got {payload.Length} byte(s).", 0);
    }
}

/// <summary>C2S 607 / C2S 571 / C2S 517-shaped single <c>i32</c>.</summary>
public readonly record struct Int32Payload(int Value)
{
    /// <summary>Parses a complete single-<c>i32</c> payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <param name="name">Opcode label for the error.</param>
    public static Int32Payload Parse(ReadOnlySpan<byte> payload, string name)
    {
        var r = new PacketReader(payload);
        var v = r.ReadInt32();
        WireUtil.RequireConsumed(r, name);
        return new Int32Payload(v);
    }
}

/// <summary>C2S 32779: i32 actor, bool32 unknown0. 0x6D6765 resolves Character and calls virtual +0x1DC/+0x1E0.</summary>
/// <param name="Unknown0">The character entity id.</param>
/// <param name="Unknown4">Second <c>i32</c>, read by <c>sub_4245E0</c> and stored as a bool.</param>
public readonly record struct Unknown32779Request(int Unknown0, int Unknown4)
{
    /// <summary>Parses the two-<c>i32</c> payload strictly.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static Unknown32779Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadInt32();
        var unknown4 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 32779");
        return new Unknown32779Request(unknown0, unknown4);
    }
}

/// <summary>C2S 32780: i32 actor, i32 unknown0, u8 unknown1, u8 unknown2. Reader 0x6D67D8 calls Character virtual +0x1D0.</summary>
/// <param name="Unknown0">The character entity id.</param>
/// <param name="Unknown4">The <c>i32</c> after the prefix; 1.</param>
/// <param name="Unknown8">The two trailing bytes preserved together as a little-endian u16.</param>
public readonly record struct Unknown32780Request(int Unknown0, int Unknown4, ushort Unknown8)
{
    /// <summary>Total body length of this family member.</summary>
    public const int BodyBytes = 10;

    /// <summary>Parses the payload strictly.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static Unknown32780Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadInt32();
        var unknown4 = r.ReadInt32();
        var unknown8 = r.ReadUInt16();
        WireUtil.RequireConsumed(r, "C2S 32780");
        return new Unknown32780Request(unknown0, unknown4, unknown8);
    }
}

/// <summary>C2S 32813: i32 actor. 0x6D6E22 resolves Character and calls virtual +0x1CC.</summary>
/// <param name="Unknown0">The character entity id.</param>
public readonly record struct Unknown32813Request(int Unknown0)
{
    /// <summary>Total body length of this family member.</summary>
    public const int BodyBytes = 4;

    /// <summary>Parses the payload strictly.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static Unknown32813Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 32813");
        return new Unknown32813Request(unknown0);
    }
}

/// <summary>C2S 578 body from <c>sub_6DC970</c> (<c>0x6DC970</c>): four <c>i32</c>. Evidence: verified.</summary>
/// <param name="Unknown0">Delay / mode-selected first field (0, 3000, or 5000 on the builder).</param>
/// <param name="Unknown1">Second <c>i32</c>.</param>
/// <param name="Unknown2">Third <c>i32</c>, builder writes 0.</param>
/// <param name="Unknown3">Fourth <c>i32</c>, builder writes 0.</param>
public readonly record struct RelayEndpointRequest(int Unknown0, int Unknown1, int Unknown2, int Unknown3)
{
    /// <summary>Parses a complete C2S 578 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RelayEndpointRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var a = r.ReadInt32();
        var b = r.ReadInt32();
        var c = r.ReadInt32();
        var d = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 578");
        return new RelayEndpointRequest(a, b, c, d);
    }
}

/// <summary>C2S 103 body from <c>sub_113F230</c> (<c>0x113F230</c>): thirty <c>i32</c>. Evidence: verified.</summary>
/// <param name="Values">Exactly thirty little-endian integers.</param>
public sealed record GameResultStats(int[] Values)
{
    /// <summary>Required scalar count.</summary>
    public const int Count = 30;

    /// <summary>Parses a complete C2S 103 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static GameResultStats Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var values = new int[Count];
        for (var i = 0; i < Count; i++)
            values[i] = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 103");
        return new GameResultStats(values);
    }
}
