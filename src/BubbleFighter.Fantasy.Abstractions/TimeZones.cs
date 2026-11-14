using System.Collections.Frozen;
using System.Reflection;

namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Resolves a configured time zone by either its IANA id (<c>Asia/Seoul</c>) or its Windows id
/// (<c>Korea Standard Time</c>) on every host. The server runs with invariant globalization, which leaves .NET on
/// Windows without the ICU conversion between the two naming systems, so a profile written with IANA ids failed on
/// Windows and one written with Windows ids failed on Linux. The embedded CLDR mapping bridges both directions.
/// </summary>
public static class TimeZones
{
    private static readonly Lazy<(FrozenDictionary<string, string> ToWindows, FrozenDictionary<string, string> ToIana)> Map =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Finds the zone the id names, trying the id as given and then its counterpart in the other naming system.</summary>
    /// <param name="id">An IANA or Windows time zone id.</param>
    /// <returns>The installed zone.</returns>
    /// <exception cref="TimeZoneNotFoundException">Neither the id nor its counterpart is installed on this host.</exception>
    /// <exception cref="InvalidTimeZoneException">The installed zone data is invalid.</exception>
    public static TimeZoneInfo Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (TryFind(id, out var zone)) return zone;
        var (toWindows, toIana) = Map.Value;
        if ((toWindows.TryGetValue(id, out var other) || toIana.TryGetValue(id, out other)) && TryFind(other, out zone))
            return zone;
        throw new TimeZoneNotFoundException(
            $"Time zone '{id}' is not installed on this host under its IANA or Windows id.");
    }

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = null!;
            return false;
        }
    }

    // TimeZoneIds.tsv: "iana<TAB>windows" lines, and "=windows<TAB>iana" lines for each Windows id's primary zone.
    private static (FrozenDictionary<string, string>, FrozenDictionary<string, string>) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BubbleFighter.Fantasy.Abstractions.TimeZoneIds.tsv")
            ?? throw new InvalidOperationException("The embedded time zone mapping is missing from the Abstractions assembly.");
        using var reader = new StreamReader(stream);
        var toWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toIana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                throw new InvalidDataException($"The embedded time zone mapping has a malformed line: '{line}'.");
            if (line[0] == '=') toIana[line[1..tab]] = line[(tab + 1)..];
            else toWindows[line[..tab]] = line[(tab + 1)..];
        }
        return (toWindows.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), toIana.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
