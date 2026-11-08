using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Finite tsunami phase progression using map intervals and the native phase meanings.</summary>
internal sealed class FinalZoneWaterCycle(FinalZoneWaterRules rules)
{
    private long _elapsed = rules.StartMs;
    private int _cycle;
    private int _phase;
    internal int InitialDelayMs => rules.StartMs;

    internal RoundFinalZoneWaterTimerChanged? Next()
    {
        if (!rules.Enabled || _elapsed >= rules.StopMs) return null;
        // kr-1256 sub_45BED0 and kr-1256 sub_45CF20 apply alarm, escape, recession and rising in phases 1..4.
        _phase = _phase % 4 + 1;
        var duration = _phase switch
        {
            1 => (int)Math.Max(rules.MinimumAlarmMs, rules.AlarmMs - (long)_cycle * rules.ShortenMs),
            2 => rules.EscapeMs,
            3 => rules.DownMs,
            _ => rules.UpMs,
        };
        duration = (int)Math.Min(duration, rules.StopMs - _elapsed);
        _elapsed += duration;
        if (_phase == 4) _cycle++;
        return new(true, _phase, duration);
    }
}
