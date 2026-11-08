namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Map resource intervals controlling the first and subsequent FinalZone drone appearances.</summary>
internal sealed record FinalZoneDroneRules(bool Enabled, FinalZoneAmountRange InitialSeconds, FinalZoneAmountRange IntervalSeconds)
{
    internal static FinalZoneDroneRules FromFields(IReadOnlyDictionary<string, string> fields)
    {
        var enabled = FinalZoneWaterRules.Field(fields, "DroneUse", 0, 1) != 0;
        if (!enabled) return new(false, new(0, 0), new(0, 0));
        FinalZoneAmountRange Range(string prefix)
        {
            var first = FinalZoneWaterRules.Field(fields, prefix + "MinSecond", 1, int.MaxValue / 1000);
            var last = FinalZoneWaterRules.Field(fields, prefix + "MaxSecond", first, int.MaxValue / 1000);
            return new(first, last);
        }
        return new(true, Range("DroneTrigger"), Range("DroneAppear"));
    }
}
