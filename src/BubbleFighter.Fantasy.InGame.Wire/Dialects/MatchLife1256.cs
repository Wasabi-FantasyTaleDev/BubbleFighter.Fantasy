using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;

namespace BubbleFighter.Fantasy.InGame.Dialects;

/// <summary>Native reset and live-score bodies, independently read by 0x6F5E50 and 0x9E3420.</summary>
internal static class MatchLife1256
{
    internal const ushort ResetRequest = 602;
    internal const ushort ResetReply = 632;
    internal const ushort ScoreUpdate = 985;

    // 0x6E3730 / 0x10C0430: actor, two tagged optional i32 values, bool32 item reset.
    internal static int ReadReset(ReadOnlySpan<byte> body)
    {
        var offset = 4;
        for (var i = 0; i < 2; i++)
        {
            if (offset >= body.Length || body[offset] is not (0x3A or 0xBA))
                throw new PacketFormatException("C2S 602 has an invalid optional-value tag.", offset);
            offset += body[offset] == 0x3A ? 5 : 1;
        }
        if (body.Length != offset + 4)
            throw new PacketFormatException("C2S 602 must contain actor, two optional values and bool32.", offset);
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }

    // 0x6F5E50 reads the same fields, then i32 unknown0 into seat+304.
    // 0x917460 initializes seat+304 to zero. Mode 1 keeps that initial value:
    // 0x6D3750 does not apply an elimination life budget in the ordinary timed-score path.
    internal static byte[] ResetReplyForTimedScore(ReadOnlySpan<byte> request)
    {
        ReadReset(request);
        var body = new byte[request.Length + 4];
        request.CopyTo(body);
        return body;
    }

    // 0x9E3420: i8 team, u16 score, i8 first slot, i8 second slot, three u32-counted vectors.
    // 0x9E8B20 applies the team total; -1 slots and empty vectors preserve individual statistics.
    internal static byte[] TeamScore(byte team, ushort score)
    {
        if (team is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(team));
        var body = new byte[17];
        body[0] = team;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), score);
        body[3] = body[4] = 0xFF;
        return body;
    }

    // 0x429620 reads two interleaved head entries; 0x9E8B20/0x9E9A60 use +12/+16 as team totals.
    // The other head arrays remain neutral; no personal statistics or rewards are inferred from C2S 103.
    internal static ResultScoreboard Scoreboard(ushort blue, ushort red, IEnumerable<ResultScoreboardRow> rows) =>
        ResultScoreboard.FromRows(rows) with
        {
            HeadUnknown12 = [blue, red],
            HeadUnknown160 = [0, 0],
            HeadUnknown308 = [0, 0],
            WinnerSlot = 0xFF,
        };
}
