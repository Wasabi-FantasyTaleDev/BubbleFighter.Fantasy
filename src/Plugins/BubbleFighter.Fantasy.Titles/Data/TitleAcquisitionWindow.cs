using System.Globalization;

namespace BubbleFighter.Fantasy.Titles.Data;

internal sealed record TitleAcquisitionWindow(int Start, int End)
{
    internal static TitleAcquisitionWindow? Parse(string value)
    {
        if (value is "" or "0") return null;
        var parts = value.Split('~');
        if (parts.Length != 2 || !DateTime.TryParseExact(parts[0], "yyyy/MM/dd/HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateTime.TryParseExact(parts[1], "yyyy/MM/dd/HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
            throw new InvalidDataException("A title acquisition interval must contain two native minute-precision dates.");
        return new(Key(start), Key(end));
    }

    internal bool Contains(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        // kr-812 sub_4FE3D0 and kr-1256 sub_EAE090 compare month/day/hour/minute inclusively, without a year check.
        var current = Key(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
        return current >= Start && current <= End;
    }

    private static int Key(DateTime value) => ((value.Month * 32 + value.Day) * 24 + value.Hour) * 60 + value.Minute;
}
