using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>An earned native ItemAttr selector and its four unscaled RandomTable values.</summary>
/// <param name="Selector">The client's ItemAttr catalog identity.</param>
/// <param name="Value0">The integer for the first catalog buff reference, including a blank reference.</param>
/// <param name="Value1">The integer for the second catalog buff reference.</param>
/// <param name="Value2">The integer for the third catalog buff reference.</param>
/// <param name="Value3">The integer for the fourth catalog buff reference.</param>
public sealed record PetSkill(int Selector, int Value0, int Value1, int Value2, int Value3);

/// <summary>Immutable pet growth and earned abilities, independent of the item's ownership lifetime.</summary>
/// <param name="Grade">Internal growth stage, zero through two.</param>
/// <param name="Experience">Progress within the current stage, interpreted through the native catalog.</param>
/// <param name="FullnessExpiresAt">UTC deadline from which the native remaining-days hunger meter is derived.</param>
/// <param name="LastFedAt">Last committed feeding time, or null before the first feed.</param>
/// <param name="FirstSkill">Ability acquired at internal stage one, if earned.</param>
/// <param name="SecondSkill">Ability acquired at internal stage two, if earned.</param>
public sealed record PetProgression(int Grade, int Experience, DateTimeOffset FullnessExpiresAt,
    DateTimeOffset? LastFedAt, PetSkill? FirstSkill, PetSkill? SecondSkill)
{
    /// <summary>Rejects corrupt persisted stages and selectors before they reach unchecked native attribute readers.</summary>
    public void Validate()
    {
        if (Grade is < 0 or > 2 || Experience < 0
            || Grade == 0 && (FirstSkill is not null || SecondSkill is not null)
            || Grade < 2 && SecondSkill is not null
            || FirstSkill is { Selector: <= 0 or 7 } || SecondSkill is { Selector: <= 0 or 7 })
            throw new InvalidDataException("Pet progression has an invalid stage or earned ability.");
    }
}

/// <summary>Immutable, structurally comparable pet growth indexed by the client build whose native catalog defines it.</summary>
[JsonConverter(typeof(PetProgressionsJsonConverter))]
public sealed class PetProgressions : IEquatable<PetProgressions>
{
    private readonly SortedDictionary<string, PetProgression> _states;

    private PetProgressions(SortedDictionary<string, PetProgression> states) => _states = states;

    /// <summary>Gets an empty collection without inventing initial growth for any client build.</summary>
    public static PetProgressions Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets how many client builds have a saved progression.</summary>
    public int Count => _states.Count;

    /// <summary>Returns one build's saved growth, or null when that build has never initialized this pet.</summary>
    public PetProgression? Find(string key)
    {
        InventoryLoadoutScope.ValidateKey(key);
        return _states.GetValueOrDefault(key);
    }

    /// <summary>Returns a new collection with one build replaced while preserving every other build's exact values.</summary>
    public PetProgressions With(string key, PetProgression progression)
    {
        InventoryLoadoutScope.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(progression);
        progression.Validate();
        if (!_states.ContainsKey(key) && Count >= 256)
            throw new InvalidDataException("Pet progression exceeds the maximum supported build capacity.");
        var copy = new SortedDictionary<string, PetProgression>(_states, StringComparer.Ordinal) { [key] = progression };
        return new(copy);
    }

    /// <summary>Checks the provider's configured build capacity and the validity of every saved native state.</summary>
    public void Validate(int maximumBuilds = 256)
    {
        if (maximumBuilds is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumBuilds));
        if (Count > maximumBuilds) throw new InvalidDataException("Pet progression exceeds the configured build capacity.");
        foreach (var (key, state) in _states)
        {
            InventoryLoadoutScope.ValidateKey(key);
            state.Validate();
        }
    }

    /// <summary>Compares build identities and immutable scalar states independently of insertion order.</summary>
    public bool Equals(PetProgressions? other) => ReferenceEquals(this, other)
        || other is not null && _states.Count == other._states.Count && _states.SequenceEqual(other._states);

    /// <summary>Compares another object as a build-indexed collection of pet progression.</summary>
    public override bool Equals(object? obj) => obj is PetProgressions other && Equals(other);

    /// <summary>Hashes the ordinally ordered build identities and their complete progression values.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, state) in _states)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(state);
        }
        return hash.ToHashCode();
    }

    internal IEnumerable<KeyValuePair<string, PetProgression>> States => _states;
}

internal sealed class PetProgressionsJsonConverter : JsonConverter<PetProgressions>
{
    private static readonly JsonSerializerOptions StateJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public override PetProgressions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Pet progression must be an object indexed by build.");
        var states = PetProgressions.Empty;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return states;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("A pet progression build key is missing.");
            var key = reader.GetString()!;
            InventoryLoadoutScope.ValidateKey(key);
            if (states.Find(key) is not null) throw new JsonException("Pet progression repeats a build key.");
            if (!reader.Read()) throw new JsonException("A pet progression state is missing.");
            var state = JsonSerializer.Deserialize<PetProgression>(ref reader, StateJson)
                ?? throw new JsonException("A build's saved pet progression cannot be null.");
            states = states.With(key, state);
        }
        throw new JsonException("Pet progression is incomplete.");
    }

    public override void Write(Utf8JsonWriter writer, PetProgressions value, JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        foreach (var (key, state) in value.States)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, state, StateJson);
        }
        writer.WriteEndObject();
    }
}
