using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 32923, the client's high-cadence movement sample. Variable length, mask-gated: <c>{u32 actor,
/// u16 tick, u8 mask, [f32 x][f32 y][f32 z][u8 yaw][u8 pitch][u8 state]}</c> where mask bits 1/2/4 mark the
/// position floats present and 8/0x10/0x20 the trailing bytes. Built by <c>sub_95C250</c>'s delta engine
/// <c>sub_95F490</c>: mode 2 samples carry only what changed since the last full frame. The actor id is the
/// sender's own, carried in the body (Character+448). </summary> <param name="Actor">The sender's
/// client-computed actor id.</param> <param name="Tick">The sample's tick, forwarded verbatim.</param> <param
/// name="Mask">Presence bits for the optional fields.</param> <param name="X">Position x; zero when the mask
/// bit is clear.</param> <param name="Y">Position y; zero when the mask bit is clear.</param> <param
/// name="Z">Position z; zero when the mask bit is clear.</param> <param name="Yaw">Yaw byte; zero when the mask
/// bit is clear.</param> <param name="Pitch">Pitch byte; zero when the mask bit is clear.</param> <param
/// name="State">State byte; zero when the mask bit is clear.</param>
public readonly record struct MovementSample(
    int Actor,
    ushort Tick,
    byte Mask,
    float X,
    float Y,
    float Z,
    byte Yaw,
    byte Pitch,
    byte State)
{
    private const int BaseLength = 7;

    /// <summary>Mask bit for the x float.</summary>
    public const byte MaskX = 0x01;

    /// <summary>Mask bit for the y float.</summary>
    public const byte MaskY = 0x02;

    /// <summary>Mask bit for the z float.</summary>
    public const byte MaskZ = 0x04;

    /// <summary>Mask bit for the yaw byte.</summary>
    public const byte MaskYaw = 0x08;

    /// <summary>Mask bit for the pitch byte.</summary>
    public const byte MaskPitch = 0x10;

    /// <summary>Mask bit for the state byte.</summary>
    public const byte MaskState = 0x20;

    /// <summary>Parses a complete C2S 32923 payload. The body length is
    /// determined by the mask; any other length is a format error.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static MovementSample Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < BaseLength)
            throw new PacketFormatException(
                $"movement sample 32923 needs at least {BaseLength} bytes, got {payload.Length}", payload.Length);
        var mask = payload[6];
        var needed = BaseLength
            + ((mask & MaskX) != 0 ? 4 : 0)
            + ((mask & MaskY) != 0 ? 4 : 0)
            + ((mask & MaskZ) != 0 ? 4 : 0)
            + ((mask & MaskYaw) != 0 ? 1 : 0)
            + ((mask & MaskPitch) != 0 ? 1 : 0)
            + ((mask & MaskState) != 0 ? 1 : 0);
        if (payload.Length != needed)
            throw new PacketFormatException(
                $"movement sample 32923 mask 0x{mask:X2} needs {needed} bytes, got {payload.Length}", payload.Length);

        var r = new PacketReader(payload);
        var actor = r.ReadInt32();
        var tick = r.ReadUInt16();
        r.ReadByte();                         // mask, already used for the length check
        float x = 0, y = 0, z = 0;
        byte yaw = 0, pitch = 0, state = 0;
        if ((mask & MaskX) != 0) x = r.ReadSingle();
        if ((mask & MaskY) != 0) y = r.ReadSingle();
        if ((mask & MaskZ) != 0) z = r.ReadSingle();
        if ((mask & MaskYaw) != 0) yaw = r.ReadByte();
        if ((mask & MaskPitch) != 0) pitch = r.ReadByte();
        if ((mask & MaskState) != 0) state = r.ReadByte();
        return new MovementSample(actor, tick, mask, x, y, z, yaw, pitch, state);
    }
}
