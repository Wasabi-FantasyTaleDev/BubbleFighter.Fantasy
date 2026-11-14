using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Stores one native modification selector, its signed effect pair, installed group and numeric configuration tag.</summary>
/// <param name="Id">ItemAttr selector resolved in the owning catalog.</param>
/// <param name="Value0">Signed first effect value, preserved as an int32 on the wire.</param>
/// <param name="Value1">Signed second effect value, preserved as an int32 on the wire.</param>
/// <param name="Group">Installed modification slot, numbered zero through two by the native dialog.</param>
/// <param name="NumericTag">Configuration category zero through two, rendered as invariant decimal text in the native content attribute.</param>
public sealed record ItemModificationAttribute(int Id, int Value0, int Value1, int Group, int NumericTag)
{
    /// <summary>Rejects identities that the native three-slot modification dialog cannot address.</summary>
    public void Validate()
    {
        // kr-1256 sub_DE6100 creates controls keyed 0..2; kr-1256 sub_DFA540 indexes attributes by their group.
        // kr-1256 sub_DE9260 seeds the configuration's numeric tag categories 0..2.
        if (Id <= 0 || Group is < 0 or > 2 || NumericTag is < 0 or > 2)
            throw new InvalidDataException("Invalid installed item modification.");
    }
}

/// <summary>Owns one immutable set of installed modifications and its optimistic transaction revision.</summary>
public sealed class ItemModificationState : IEquatable<ItemModificationState>
{
    /// <summary>Copies the bounded installed attributes so transaction plans cannot change after validation.</summary>
    public ItemModificationState(long revision, IReadOnlyList<ItemModificationAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count > 3) throw new InvalidDataException("An item can contain at most three modifications.");
        Revision = revision;
        Attributes = Array.AsReadOnly(attributes.OrderBy(attribute => attribute?.Group).ToArray());
        Validate();
    }

    /// <summary>Gets the positive monotonically increasing revision, retained even after the last modification is deleted.</summary>
    public long Revision { get; }

    /// <summary>Gets the exact installed attributes in group order.</summary>
    public IReadOnlyList<ItemModificationAttribute> Attributes { get; }

    /// <summary>Validates the revision, bounded groups and distinct group identity.</summary>
    public void Validate()
    {
        if (Revision <= 0 || Attributes.Count > 3 || Attributes.Any(attribute => attribute is null)
            || Attributes.Select(attribute => attribute.Group).Distinct().Count() != Attributes.Count)
            throw new InvalidDataException("Invalid item modification state.");
        foreach (var attribute in Attributes) attribute.Validate();
    }

    /// <summary>Compares the revision and every installed effect across persistence round trips.</summary>
    public bool Equals(ItemModificationState? other) => ReferenceEquals(this, other)
        || other is not null && Revision == other.Revision && Attributes.SequenceEqual(other.Attributes);

    /// <summary>Compares another object as a complete immutable modification state.</summary>
    public override bool Equals(object? obj) => obj is ItemModificationState other && Equals(other);

    /// <summary>Hashes the revision and attributes in their canonical group order.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Revision);
        foreach (var attribute in Attributes) hash.Add(attribute);
        return hash.ToHashCode();
    }
}

/// <summary>Separates immutable installed modification state by native catalog with bounded persistent storage.</summary>
[JsonConverter(typeof(ItemModificationsJsonConverter))]
public sealed class ItemModifications : IEquatable<ItemModifications>
{
    private readonly SortedDictionary<string, ItemModificationState> _states;
    private ItemModifications(SortedDictionary<string, ItemModificationState> states) => _states = states;

    /// <summary>Gets an empty collection with no modification revisions.</summary>
    public static ItemModifications Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets the number of catalog scopes with modification history.</summary>
    public int Count => _states.Count;

    /// <summary>Finds the exact catalog state without borrowing another build's effect selectors.</summary>
    public ItemModificationState? Find(string scope)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        return _states.GetValueOrDefault(scope);
    }

    /// <summary>Returns a collection replacing one scope while preserving all others.</summary>
    public ItemModifications With(string scope, ItemModificationState state)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        if (!_states.ContainsKey(scope) && Count >= 256) throw new InvalidDataException("Modification scopes exceed capacity.");
        return new(new(_states, StringComparer.Ordinal) { [scope] = state });
    }

    /// <summary>Checks all catalog keys and the provider's configured scope capacity.</summary>
    public void Validate(int maximumScopes = 256)
    {
        if (maximumScopes is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumScopes));
        if (Count > maximumScopes) throw new InvalidDataException("Modification scopes exceed configured capacity.");
        foreach (var (scope, state) in _states) { InventoryLoadoutScope.ValidateKey(scope); state.Validate(); }
    }

    /// <summary>Compares every catalog and installed modification independently of serialization identity.</summary>
    public bool Equals(ItemModifications? other) => ReferenceEquals(this, other)
        || other is not null && _states.SequenceEqual(other._states);

    /// <summary>Compares another object as an immutable scoped modification collection.</summary>
    public override bool Equals(object? obj) => obj is ItemModifications other && Equals(other);

    /// <summary>Hashes all catalog states in ordinal key order.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _states) { hash.Add(pair.Key, StringComparer.Ordinal); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }

    internal IEnumerable<KeyValuePair<string, ItemModificationState>> States => _states;
}

internal sealed class ItemModificationsJsonConverter : JsonConverter<ItemModifications>
{
    public override ItemModifications Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Modifications must be indexed by scope.");
        var states = ItemModifications.Empty;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return states;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Modification scope is missing.");
            var scope = reader.GetString()!;
            if (states.Find(scope) is not null || !reader.Read()) throw new JsonException("Modification scope is repeated or incomplete.");
            states = states.With(scope, ReadState(ref reader));
        }
        throw new JsonException("Modification collection is incomplete.");
    }

    public override void Write(Utf8JsonWriter writer, ItemModifications value, JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        foreach (var (scope, state) in value.States)
        {
            writer.WritePropertyName(scope);
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ItemModificationState.Revision), state.Revision);
            writer.WriteStartArray(nameof(ItemModificationState.Attributes));
            foreach (var attribute in state.Attributes)
            {
                writer.WriteStartObject();
                writer.WriteNumber(nameof(ItemModificationAttribute.Id), attribute.Id);
                writer.WriteNumber(nameof(ItemModificationAttribute.Value0), attribute.Value0);
                writer.WriteNumber(nameof(ItemModificationAttribute.Value1), attribute.Value1);
                writer.WriteNumber(nameof(ItemModificationAttribute.Group), attribute.Group);
                writer.WriteNumber(nameof(ItemModificationAttribute.NumericTag), attribute.NumericTag);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static ItemModificationState ReadState(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Modification state must be an object.");
        long? revision = null;
        List<ItemModificationAttribute>? attributes = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return revision is { } counter && attributes is not null ? new(counter, attributes)
                    : throw new JsonException("Modification state is missing a required field.");
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Modification field is missing.");
            var name = reader.GetString();
            if (!reader.Read()) throw new JsonException("Modification field is incomplete.");
            if (name == nameof(ItemModificationState.Revision) && revision is null && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var counterValue))
                revision = counterValue;
            else if (name == nameof(ItemModificationState.Attributes) && attributes is null && reader.TokenType == JsonTokenType.StartArray)
            {
                attributes = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (attributes.Count >= 3) throw new JsonException("Installed modifications exceed capacity.");
                    attributes.Add(ReadAttribute(ref reader));
                }
                if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Modification attributes are incomplete.");
            }
            else throw new JsonException("Modification state has an unknown, repeated or invalid field.");
        }
        throw new JsonException("Modification state is incomplete.");
    }

    private static ItemModificationAttribute ReadAttribute(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Modification attribute must be an object.");
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return fields.Count == 5 ? new(fields["Id"], fields["Value0"], fields["Value1"], fields["Group"], fields["NumericTag"])
                    : throw new JsonException("Modification attribute is missing a required field.");
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Modification attribute field is missing.");
            var name = reader.GetString()!;
            if (name is not ("Id" or "Value0" or "Value1" or "Group" or "NumericTag")
                || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var number)
                || !fields.TryAdd(name, number)) throw new JsonException("Modification attribute has an unknown, repeated or invalid field.");
        }
        throw new JsonException("Modification attribute is incomplete.");
    }
}
