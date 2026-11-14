using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Stores the accumulated Favorite weapon points of one Article by native catalog. Only the points are durable: the
/// client holds no separate grade store and derives the displayed bar from the points and its Upgrade thresholds
/// (kr-1256 sub_5EE200), so the grade is always recomputed from the points and never persisted.
/// </summary>
[JsonConverter(typeof(ItemFavoritesJsonConverter))]
public sealed class ItemFavorites : IEquatable<ItemFavorites>
{
    private readonly SortedDictionary<string, long> _points;
    private ItemFavorites(SortedDictionary<string, long> points) => _points = points;

    /// <summary>Gets an empty collection with no recorded progress in any catalog.</summary>
    public static ItemFavorites Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets the number of catalog scopes with recorded progress.</summary>
    public int Count => _points.Count;

    /// <summary>Finds the exact catalog's points without borrowing another build's progress, or null when none are recorded.</summary>
    public long? Find(string scope)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        return _points.TryGetValue(scope, out var points) ? points : null;
    }

    /// <summary>Returns a collection replacing one scope's points while preserving all others.</summary>
    public ItemFavorites With(string scope, long points)
    {
        InventoryLoadoutScope.ValidateKey(scope);
        ValidatePoints(points);
        if (!_points.ContainsKey(scope) && Count >= 256) throw new InvalidDataException("Favorite scopes exceed capacity.");
        return new(new(_points, StringComparer.Ordinal) { [scope] = points });
    }

    /// <summary>Checks all catalog keys, the point bounds and the provider's configured scope capacity.</summary>
    public void Validate(int maximumScopes = 256)
    {
        if (maximumScopes is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumScopes));
        if (Count > maximumScopes) throw new InvalidDataException("Favorite scopes exceed configured capacity.");
        foreach (var (scope, points) in _points) { InventoryLoadoutScope.ValidateKey(scope); ValidatePoints(points); }
    }

    /// <summary>Compares every catalog and its points independently of serialization identity.</summary>
    public bool Equals(ItemFavorites? other) => ReferenceEquals(this, other)
        || other is not null && _points.SequenceEqual(other._points);

    /// <summary>Compares another object as an immutable scoped Favorite collection.</summary>
    public override bool Equals(object? obj) => obj is ItemFavorites other && Equals(other);

    /// <summary>Hashes all catalog points in ordinal key order.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in _points) { hash.Add(pair.Key, StringComparer.Ordinal); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }

    internal IEnumerable<KeyValuePair<string, long>> Entries => _points;

    // kr-1256 sub_5EE200 reads the points as the second u32 of the attribute's value vector, so the wire cannot carry more.
    private static void ValidatePoints(long points)
    {
        if (points is < 0 or > uint.MaxValue) throw new InvalidDataException("Favorite points must fit the client's unsigned 32-bit value.");
    }
}

internal sealed class ItemFavoritesJsonConverter : JsonConverter<ItemFavorites>
{
    public override ItemFavorites Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Favorites must be indexed by scope.");
        var favorites = ItemFavorites.Empty;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return favorites;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Favorite scope is missing.");
            var scope = reader.GetString()!;
            if (favorites.Find(scope) is not null || !reader.Read() || reader.TokenType != JsonTokenType.Number
                || !reader.TryGetInt64(out var points))
                throw new JsonException("Favorite scope is repeated, incomplete or not an integer.");
            favorites = favorites.With(scope, points);
        }
        throw new JsonException("Favorite collection is incomplete.");
    }

    public override void Write(Utf8JsonWriter writer, ItemFavorites value, JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        foreach (var (scope, points) in value.Entries) writer.WriteNumber(scope, points);
        writer.WriteEndObject();
    }
}
