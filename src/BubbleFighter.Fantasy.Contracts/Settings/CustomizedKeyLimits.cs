using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Contracts.Settings;

/// <summary>Configurable per-account key-map budgets, bounded by the native 65,535-byte packet body.</summary>
public sealed record CustomizedKeyLimits : IValidatableObject
{
    /// <summary>Gets the maximum independently named control scopes stored for one player.</summary>
    [Range(1, 64)]
    public int MaxScopes { get; init; } = 16;

    /// <summary>Gets the maximum number of customized controls stored in one scope.</summary>
    [Range(1, 512)]
    public int MaxMappings { get; init; } = 128;

    /// <summary>Gets the maximum UTF-16 code-unit length of an individual control name.</summary>
    [Range(1, 1024)]
    public int MaxNameCharacters { get; init; } = 128;

    /// <summary>Validates these deployment budgets before a repository or packet binding accepts work.</summary>
    public void Validate()
    {
        Validator.ValidateObject(this, new(this), validateAllProperties: true);
    }

    /// <summary>Reports a combined row/name budget that cannot fit the native body, including during nested config binding.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // A native map has an eight-byte envelope and eight bytes per row besides UTF-16 names.
        if (8L + MaxMappings * (8L + 2L * MaxNameCharacters) > ushort.MaxValue)
            yield return new("Customized-key limits must fit a native 65,535-byte packet body.",
                [nameof(MaxMappings), nameof(MaxNameCharacters)]);
    }

    /// <summary>Validates the authenticated account identity and the bounded, case-sensitive settings scope.</summary>
    public static void ValidateIdentity(long userNum, string scope)
    {
        if (userNum <= 0) throw new ArgumentOutOfRangeException(nameof(userNum));
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Length is < 1 or > 64 || scope.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new ArgumentException("A settings scope requires 1 to 64 lowercase ASCII letters, digits, dots, dashes or underscores.", nameof(scope));
    }

    /// <summary>Checks count, unique names and well-formed UTF-16 before returning a defensive ordered copy.</summary>
    public CustomizedKey[] Copy(IReadOnlyList<CustomizedKey> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count > MaxMappings)
            throw new ArgumentException("The customized-key map exceeds its configured row budget.", nameof(mappings));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var copy = new CustomizedKey[mappings.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var row = mappings[i];
            if (row is null || string.IsNullOrEmpty(row.Name) || row.Name.Length > MaxNameCharacters
                || !IsValidName(row.Name) || !names.Add(row.Name))
                throw new ArgumentException("Customized-key names must be unique, nonempty, bounded, well-formed UTF-16 without NUL.", nameof(mappings));
            copy[i] = row;
        }
        return copy;
    }

    private static bool IsValidName(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '\0' || char.IsLowSurrogate(c)) return false;
            if (char.IsHighSurrogate(c) && (++i >= name.Length || !char.IsLowSurrogate(name[i]))) return false;
        }
        return true;
    }
}
