using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Identifies an earned equipment enhancement independently of native attribute numbers.</summary>
public enum ItemEnhancementKind
{
    /// <summary>No enhancement has been earned; a failed attempt may still have a revision.</summary>
    None,
    /// <summary>The catalog's weapon enhancement has been earned.</summary>
    Weapon,
    /// <summary>The catalog's clothing enhancement has been earned.</summary>
    Clothing,
}

/// <summary>Stores an enhancement outcome and the monotonic revision of committed attempts.</summary>
/// <param name="Kind">The earned enhancement, or None after an unsuccessful attempt.</param>
/// <param name="Revision">Positive attempt revision used to reject stale plans even after materials are replenished.</param>
/// <param name="Advancement">
/// The native ItemAttr selector of the laboratory grade-up this Article carries (the MakeItemAdvanced tab), or zero for none.
/// A native set carries it on one member; the client merges the effects of all members (kr-1256 sub_D3B2C0, sub_10E7EC0).
/// </param>
public sealed record ItemEnhancementState(ItemEnhancementKind Kind, long Revision, int Advancement = 0)
{
    /// <summary>Rejects unknown outcomes, unset or wrapped attempt revisions and a negative advancement selector.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Revision <= 0 || Advancement < 0) throw new InvalidDataException("Invalid equipment enhancement state.");
    }
}

/// <summary>Owns bounded, immutable enhancement state with structural equality across persistence round trips.</summary>
[JsonConverter(typeof(ItemEnhancementsJsonConverter))]
public sealed class ItemEnhancements : IEquatable<ItemEnhancements>
{
    private readonly SortedDictionary<string, ItemEnhancementState> _states;
    private ItemEnhancements(SortedDictionary<string, ItemEnhancementState> states) => _states = states;

    /// <summary>Gets a collection with no recorded enhancement attempts.</summary>
    public static ItemEnhancements Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets the number of catalog scopes with recorded state.</summary>
    public int Count => _states.Count;

    /// <summary>Finds the exact state for a catalog scope without borrowing another client's definitions.</summary>
    public ItemEnhancementState? Find(string scope)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        return _states.GetValueOrDefault(scope);
    }

    /// <summary>Returns a new collection preserving every scope except the supplied replacement.</summary>
    public ItemEnhancements With(string scope, ItemEnhancementState state)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        if (!_states.ContainsKey(scope) && Count >= 256) throw new InvalidDataException("Enhancement scopes exceed capacity.");
        return new(new(_states, StringComparer.Ordinal) { [scope] = state });
    }

    /// <summary>Validates every scope and enforces the persistence provider's configured capacity.</summary>
    public void Validate(int maximumScopes = 256)
    {
        if (maximumScopes is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumScopes));
        if (Count > maximumScopes) throw new InvalidDataException("Enhancement scopes exceed configured capacity.");
        foreach (var (scope, state) in _states) { InventoryLoadoutScope.ValidateKey(scope); state.Validate(); }
    }

    /// <summary>Compares complete state independently of insertion order or deserialization identity.</summary>
    public bool Equals(ItemEnhancements? other) => ReferenceEquals(this, other)
        || other is not null && _states.SequenceEqual(other._states);

    /// <summary>Compares another object as an immutable enhancement collection.</summary>
    public override bool Equals(object? obj) => obj is ItemEnhancements other && Equals(other);

    /// <summary>Hashes scope names and all recorded state in ordinal order.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _states) { hash.Add(pair.Key, StringComparer.Ordinal); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }

    internal IEnumerable<KeyValuePair<string, ItemEnhancementState>> States => _states;
}

internal sealed class ItemEnhancementsJsonConverter : JsonConverter<ItemEnhancements>
{
    public override ItemEnhancements Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Enhancements must be indexed by scope.");
        var states = ItemEnhancements.Empty;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return states;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Enhancement scope is missing.");
            var key = reader.GetString()!;
            if (states.Find(key) is not null) throw new JsonException("Enhancement scope is repeated.");
            if (!reader.Read()) throw new JsonException("Enhancement state is missing.");
            states = states.With(key, ReadState(ref reader));
        }
        throw new JsonException("Enhancement state is incomplete.");
    }

    public override void Write(Utf8JsonWriter writer, ItemEnhancements value, JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        foreach (var (key, state) in value.States)
        {
            writer.WritePropertyName(key);
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ItemEnhancementState.Kind), (int)state.Kind);
            writer.WriteNumber(nameof(ItemEnhancementState.Revision), state.Revision);
            // Written only when earned, so rows that never carried a grade-up keep their earlier stored form.
            if (state.Advancement != 0) writer.WriteNumber(nameof(ItemEnhancementState.Advancement), state.Advancement);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static ItemEnhancementState ReadState(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Enhancement state must be an object.");
        ItemEnhancementKind? kind = null;
        long? revision = null;
        int? advancement = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return kind is { } savedKind && revision is { } savedRevision ? new(savedKind, savedRevision, advancement ?? 0)
                    : throw new JsonException("Enhancement state is missing a required field.");
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Enhancement field name is missing.");
            var name = reader.GetString();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Enhancement fields must be integers.");
            if (name == nameof(ItemEnhancementState.Kind) && kind is null && reader.TryGetInt32(out var number))
                kind = (ItemEnhancementKind)number;
            else if (name == nameof(ItemEnhancementState.Revision) && revision is null && reader.TryGetInt64(out var counter))
                revision = counter;
            else if (name == nameof(ItemEnhancementState.Advancement) && advancement is null && reader.TryGetInt32(out var selector))
                advancement = selector;
            else throw new JsonException("Enhancement state has an unknown, repeated or invalid field.");
        }
        throw new JsonException("Enhancement state is incomplete.");
    }
}
