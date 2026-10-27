using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.RoomCore;

/// <summary>
/// The 53-byte rule record written by <c>kr-1256 sub_10D26C0</c> at <c>0x10D26C0</c> (thirteen little-endian
/// <c>i32</c> plus a trailing <c>u8</c>). Named fields follow the verified <c>kr-1256 sub_AADD50</c> config-key
/// mapping; unnamed positions stay <c>unknownN</c>. Evidence: verified widths and order, verified key names.
///
/// </summary>
public sealed record RoomRuleRecord(
    int EndGameTimeOver,
    int EndGameNotifyWinOrLoseTickTeam,
    int EndGameNotifyWinOrLoseTickStar,
    int EndGameNotifyResultTick,
    int FeverTimeDuration,
    int StarDropTimeDuration,
    int DeathMatchKill,
    int DeathMatchTimeMs,
    int TimeAttackTimeMs,
    int ConquerTimeLimitMs,
    int StarModeTimeLimitMs,
    int Unknown44,
    int Unknown48,
    byte Flags52)
{
    /// <summary>Reads the rule record from an open reader.</summary>
    /// <param name="r">The reader.</param>
    public static RoomRuleRecord Read(ref PacketReader r) => new(
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadByte());
}
