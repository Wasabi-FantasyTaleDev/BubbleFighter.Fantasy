using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Reads the loosely typed values of a row's configuration mapping (strings, numbers, booleans, lists and maps).</summary>
internal static class ConfigValues
{
    /// <summary>Tries to view a value as a string-keyed mapping.</summary>
    public static bool TryMap(object? value, [NotNullWhen(true)] out Dictionary<string, object?>? map)
    {
        map = null;
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnly:
                map = new Dictionary<string, object?>(readOnly, StringComparer.Ordinal);
                return true;
            case IDictionary<string, object?> dictionary:
                map = new Dictionary<string, object?>(dictionary, StringComparer.Ordinal);
                return true;
            case IDictionary untyped:
                map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in untyped)
                {
                    if (entry.Key is not string key)
                        return false;
                    map[key] = entry.Value;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Tries to view a value as a list; strings and mappings are not lists.</summary>
    public static bool TryList(object? value, [NotNullWhen(true)] out List<object?>? list)
    {
        list = null;
        if (value is null or string || TryMap(value, out _) || value is not IEnumerable items)
            return false;
        list = [.. items.Cast<object?>()];
        return true;
    }

    /// <summary>Tries to view a value as an integer; fractional numbers are rejected.</summary>
    public static bool TryInteger(object? value, out long result)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long:
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case ulong big when big <= long.MaxValue:
                result = (long)big;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>Tries to view a value as any number.</summary>
    public static bool TryNumber(object? value, out double result)
    {
        if (TryInteger(value, out var integer))
        {
            result = integer;
            return true;
        }
        switch (value)
        {
            case float or double or decimal or ulong:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
