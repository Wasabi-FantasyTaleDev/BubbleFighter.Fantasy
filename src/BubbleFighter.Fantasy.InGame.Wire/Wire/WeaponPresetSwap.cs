using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 832 body, the in-round weapon-preset swap request. The builder <c>sub_808F10</c>
/// (<c>0x808F10</c>) stamps the opcode at <c>sub_808F10</c> and appends one <c>i32</c> then one <c>u8</c>
/// through the stream vtable slot <c>+48</c> (<c>sub_808F10</c>), so the body is exactly five bytes. The
/// opcode has no client label; the semantic name comes from the proven swap path. </summary> <remarks> The
/// <c>u8</c> is the preset slot: the builder rejects a slot above 4 before anything else (<c>sub_808F10</c>)
/// and the in-game hotkeys <c>sub_B033E0</c> map to slots 0..4. The <c>i32</c> is 0 on
/// the two UI paths (<c>sub_5FB6D0</c> passes a zero flag, <c>sub_9753A0</c> passes flag 0 at
/// <c>sub_9753A0</c>) and the actor dword at +448 on the hotkey path (<c>sub_B033E0</c>). Its consumer is not
/// recovered, so it is carried as <see cref="Unknown1"/> and never interpreted, per the opcode policy for
/// unknown fields. </remarks> <param name="Unknown1"> The leading <c>i32</c>. Zero on the UI builder paths, the
/// actor +448 dword on the hotkey path; no recovered reader consumes it. </param> <param name="Slot">Preset
/// slot 0..4 the client asks to switch to.</param>
public readonly record struct WeaponPresetSwap(int Unknown1, byte Slot)
{
    /// <summary>
    /// Body length the builder writes: 4 + 1 = 5. Length pin for the
    /// byte-length test the opcode policy requires.
    ///
    /// </summary>
    public const int BodyBytes = 5;

    /// <summary>
    /// Highest slot the client's own builder sends: <c>a2 &gt; 4u</c> returns at <c>sub_808F10</c>,
    /// and the hotkey table has five entries. A slot outside 0..4 cannot come from this client, so it is a
    /// malformed packet.
    /// </summary>
    public const byte MaxSlot = 4;

    /// <summary>Parses a complete C2S 832 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <returns>The decoded swap request.</returns>
    /// <exception cref="PacketFormatException">
    /// The body is not <see cref="BodyBytes"/> long or the slot exceeds
    /// <see cref="MaxSlot"/>.
    /// </exception>
    public static WeaponPresetSwap Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != BodyBytes)
            throw new PacketFormatException($"C2S 832 must be {BodyBytes} bytes, got {payload.Length}.", 0);
        var r = new PacketReader(payload);
        var unknown1 = r.ReadInt32();
        var slot = r.ReadByte();
        WireUtil.RequireConsumed(r, "C2S 832");
        if (slot > MaxSlot)
            throw new PacketFormatException(
                $"C2S 832 slot {slot} exceeds the client builder's 0..{MaxSlot} range.", r.Offset - 1);
        return new WeaponPresetSwap(unknown1, slot);
    }
}

/// <summary>
/// S2C 836 body, the swap-apply reply the client waits for after C2S 832. The dispatcher case
/// (<c>sub_9D2FF0</c> jumptable <c>jpt_009D48B5</c> case 836 at <c>0x9D5035</c>, client label
/// <c>gspReqInventoryChangeWeaponPresetIdx</c>) reads one <c>u8</c> through <c>sub_429DD0</c> and then one
/// <c>i32</c> through the same stream vtable slot (<c>sub_9D2FF0:9D5043-9D5067</c>), so the body is exactly
/// five bytes. </summary> <remarks> The case stores the <c>u8</c> as the active preset index at profile +28
/// (<c>kr-1256 sub_9D2FF0:9D5081-9D5097</c>), clears the C2S 832 in-flight latch at <c>dword_18E55D4+208</c>
/// (<c>sub_9D2FF0:9D506A-9D5072</c>; the byte <c>sub_808F10</c> sets after sending), and applies the in-session weapon-model swap only when the <c>i32</c> is zero
/// (<c>sub_9D2FF0:9D5079-9D5110</c>: the chain <c>sub_81CC20</c> -&gt; <c>sub_6D3390</c> -&gt;
/// <c>sub_ABE0F0</c> -&gt; <c>sub_6DA420</c>). Without this reply the latch stays set and every later swap
/// attempt returns at <c>sub_808F10</c>, so the server must send it. </remarks> <param name="Slot">The preset
/// index the client stores at profile +28.</param> <param name="Status">Zero applies the swap; any other value
/// skips it.</param>
public readonly record struct WeaponPresetSwapReply(byte Slot, int Status)
{
    /// <summary>Body length the dispatcher case consumes: 1 + 4 = 5.</summary>
    public const int BodyBytes = 5;

    /// <summary>The only status the dispatcher case applies the swap on.</summary>
    public const int Success = 0;

    /// <summary>Writes this reply in wire order.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteByte(Slot);
        w.WriteInt32(Status);
    }

    /// <summary>Builds a successful swap-apply body for a preset slot.</summary>
    /// <param name="slot">The preset index to confirm.</param>
    /// <returns>The five-byte body with <see cref="Success"/> status.</returns>
    public static byte[] WriteSuccess(byte slot)
    {
        var w = PacketWriter.Create();
        new WeaponPresetSwapReply(slot, Success).Write(w);
        return WireUtil.Written(w);
    }

    /// <summary>Builds a refusal body for a preset slot that could not be persisted.</summary>
    /// <param name="slot">The requested preset index.</param>
    /// <returns>The five-byte body with a nonzero refusal status.</returns>
    public static byte[] WriteFailure(byte slot)
    {
        var w = PacketWriter.Create();
        new WeaponPresetSwapReply(slot, 1).Write(w);
        return WireUtil.Written(w);
    }
}
