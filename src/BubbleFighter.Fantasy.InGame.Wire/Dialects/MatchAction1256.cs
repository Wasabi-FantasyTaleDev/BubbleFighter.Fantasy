using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Dialects;

/// <summary>
/// Validated match actions read by kr-1256 sub_6D55D0. Unknown fields remain byte-identical; the
/// actor offset identifies the sending character, not a target or damager key. The table below pins the
/// permitted lengths and actor offsets; unmodeled fields stay opaque.
/// </summary>
internal readonly record struct MatchAction1256(
    ushort InboundOpcode, ushort OutboundOpcode, int BodyBytes, int ActorOffset, bool EchoSender = false,
    int AlternateActorOffset = -1)
{
    // Each C2S/S2C pair is independently evidenced; equal numbers are not a generic relay rule.
    internal static IReadOnlyList<MatchAction1256> All { get; } = Array.AsReadOnly<MatchAction1256>([
        // 0x6D6765 / 0x6D67D8: character virtual calls, not latency reports.
        new(32779, 32779, 8, 0),
        new(32780, 32780, 10, 0),
        // 0x6D8E90: i32 actor, i32 unknown0, bool32 unknown1; virtual +0x1E4/+0x1E8.
        new(32781, 32781, 12, 0),
        // 0x6D686F: i32 actor.
        new(32782, 32782, 4, 0),
        // 0x6FC8E0: i32 unknown0 (target, possibly -1), i32 actor.
        new(32783, 32783, 8, 4),
        // 0x6D6994: i32 actor, i32 unknown0, bool32 unknown1, i32 unknown2.
        new(32784, 32784, 16, 0),
        // 0x6D6AA1 / writer 0x6DF180: i32 actor, i32 unknown0, u8 unknown1.
        new(32786, 32786, 9, 0),
        // 0x6FA7B0 / writer 0x6E3DA0: affected actor, source, 3*f32, i32 unknown0; stun/knockback.
        new(32788, 32788, 24, 0),
        // 0x6D6D35: affected actor; character virtual +664 transitions to normal sub-state 107.
        new(32790, 32790, 4, 0),
        // 0x6D6ED8: i32 actor, i32 target, i32 unknown0.
        new(32791, 32791, 12, 0),
        // kr-1256 sub_6F7600: i32 actor; the local actor alone receives the failure feedback.
        new(32792, 32792, 4, 0, EchoSender: true),
        // 0x6F65A0: i32 unknown0, i32 unknown1, i32 actor, u32 unknown2, 2*bool32.
        new(32810, 32810, 24, 8),
        // 0x6D7153: i32 actor, u32 unknown0 (packed).
        new(32811, 32811, 8, 0),
        // 0x6D6E22: i32 actor; character virtual +0x1CC.
        new(32813, 32813, 4, 0),
        // 0x6D6C0A / 0x6F7780: i32 actor, i32 unknown0.
        new(32815, 32815, 8, 0),
        new(32816, 32816, 8, 0),
        // 0x6D7EBC: i32 actor, u8 unknown0, u8 unknown1.
        new(32821, 32821, 6, 0),
        // 0x6D68BC / writer 0x6DED10: i32 actor, i32 unknown0.
        new(32834, 32834, 8, 0),
        // kr-1256 sub_6D55D0 / sub_6F6900: i32 actor, death presentation stages.
        new(32835, 32835, 4, 0),
        new(32836, 32836, 4, 0, EchoSender: true),
        // kr-1256 sub_6D55D0 -> sub_4AE3F0: completion applies a local-only flash effect.
        new(32837, 32837, 4, 0, EchoSender: true),
        // 0x6D783F: i32 actor, bool32 unknown0, bool32 unknown1.
        new(32882, 32882, 12, 0),
        // 0x6F0100: i32 actor, i32 target, bool32 unknown0, i32 unknown1.
        new(32884, 32884, 16, 0),
        // 0x6D6577 / writer 0x6DD310: i32 damager key; echoed to the sender.
        new(32907, 32907, 4, -1, EchoSender: true),
        // 0x6DFE40: i32 actor, i32 unknown0, i32 unknown1; not an HP write.
        new(32921, 32921, 12, 0),
        // 0x10F3990: i32 actor, u8 weapon slot, 6*u16 unknown0..5 (binary16),
        // i32 unknown6, u8 unknown7, i32 unknown8, u32 unknown9, u16 unknown10.
        new(32925, 32925, 32, 0),
        // 0x6F6200 / 0x706770: actor, two tagged optional i32s, u8, bool32.
        new(32926, 32926, -1, 0),
        // 0x6D81CE: i32 actor, i32 unknown0, i32 unknown1.
        new(33286, 33286, 12, 0),
        // 0x6F4EF0 / writer 0x706840: i32 damager key, 3*f32 hit position, i32 owner, i32 target.
        new(33301, 33301, 24, 16),
        // 0x6FAAF0 / 0x6E42B0: victim, initiator; either participant can report contact.
        new(38271, 38271, 8, 4, EchoSender: true, AlternateActorOffset: 0),
        // 0x6F7B30 / 0x6E2FD0: actor, other actor or -1/-2 for self release.
        new(38272, 38272, 8, 0, EchoSender: true),
        // 0x6D97D0 / 0x85CB70: victim, rescuer, f32 distance, u8 unknown0.
        new(38273, 38273, 13, 0, EchoSender: true, AlternateActorOffset: 4),
        // 0x6D8743 / 0x6E2CA0: actor; bubble timer expiry requests dead sub-state 402.
        new(38274, 38274, 4, 0, EchoSender: true),
        // 0x6FAC30 / 0x6E3F00: bool32, victim, initiator, 3*f32, bool32, i32.
        new(38277, 38277, 32, 8, EchoSender: true, AlternateActorOffset: 4),
        // 0x6F7C50 / 0x4AAFA7: rescuer, released actor, u8 unknown0.
        new(38280, 38280, 9, 4, EchoSender: true, AlternateActorOffset: 0),
        // 0x6D88D9 -> 0x4A6F90 / 0x6E4D60: actor; enters cssLockedIn (301).
        new(38281, 38281, 4, 0, EchoSender: true),
        // 0x6F8280 / 0x6E3B80: victim, source, 3*f32, i32, 2*bool32, u32, bool32.
        new(38282, 38282, 40, 0, EchoSender: true),
        // 0x6D8679: damager key, target, 3*binary16, u8, bool32.
        new(38283, 38283, 19, -1, EchoSender: true),
        // 0x6D8832: i32 actor, bool32 unknown0 (render flag, not a release command).
        new(38284, 38284, 8, 0),
    ]);

    internal static MatchAction1256 Read(ushort opcode, ReadOnlySpan<byte> payload)
    {
        foreach (var action in All)
        {
            if (action.InboundOpcode != opcode) continue;
            if (action.InboundOpcode == 32926)
                ValidateTaggedReset(payload);
            else if (payload.Length != action.BodyBytes)
                throw new PacketFormatException($"C2S Unknown{opcode} must be {action.BodyBytes} bytes, got {payload.Length}.", 0);
            return action;
        }
        throw new PacketFormatException($"C2S Unknown{opcode} has no verified match-action layout.", 0);
    }

    internal bool IsReportedBy(ReadOnlySpan<byte> payload, int entityId) =>
        ActorOffset < 0 || BinaryPrimitives.ReadInt32LittleEndian(payload[ActorOffset..]) == entityId
        || (AlternateActorOffset >= 0 && BinaryPrimitives.ReadInt32LittleEndian(payload[AlternateActorOffset..]) == entityId);

    private static void ValidateTaggedReset(ReadOnlySpan<byte> payload)
    {
        // 0x10C04D0 / 0x10C0640: a 0x3A tag includes an i32; 0xBA omits it.
        var offset = 4;
        for (var field = 0; field < 2; field++)
        {
            if (offset >= payload.Length || payload[offset] is not (0x3A or 0xBA))
                throw new PacketFormatException("C2S Unknown32926 has an invalid optional-value tag.", offset);
            offset += payload[offset] == 0x3A ? 5 : 1;
        }
        if (payload.Length != offset + 5)
            throw new PacketFormatException("C2S Unknown32926 has an invalid tagged body length.", offset);
    }
}
