using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>One plaza change between a node and the directory; the directory never interprets the position body.</summary>
/// <param name="Build">The client build whose plaza changed.</param>
/// <param name="Kind">What happened.</param>
/// <param name="User">The player's account number.</param>
/// <param name="Member">The appearance, for entries only.</param>
/// <param name="Position">The build's position upload body, for moves only.</param>
internal sealed record PlazaWireEvent(string Build, PlazaFederationEventKind Kind, int User, PlazaWireMember? Member, byte[]? Position);

/// <summary>What a remote plaza seat shows; the seat index itself is each node's own.</summary>
internal sealed record PlazaWireMember(string Nickname, int Level, int CharacterItemId, int[] EquippedItemIds, int ColorIndex)
{
    /// <summary>Gets the optional native modifiers; older nodes omit this field, and an empty vector clears them.</summary>
    public RoomItemAttribute[] ItemAttributes { get; init; } = [];
}

/// <summary>A node's queued local changes.</summary>
internal sealed record PlazaWirePost(PlazaWireEvent[] Events);

/// <summary>The changes other nodes made after the reader's cursor, or a full snapshot when the cursor is unusable.</summary>
/// <param name="Hub">The directory incarnation; a new one means the directory restarted and lost every member.</param>
/// <param name="Cursor">The sequence to read after next time.</param>
/// <param name="Snapshot">True when <paramref name="Events"/> list every remote member present.</param>
/// <param name="Events">The changes, in order.</param>
internal sealed record PlazaWireBatch(string Hub, long Cursor, bool Snapshot, PlazaWireEvent[] Events);

internal static class PlazaTransport
{
    // Position uploads are a few dozen bytes in every build (kr-812 sub_934030 writes 18 after the identity).
    internal const int MaxPositionBytes = 128;
    internal const int MaxEquippedItems = 64;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<PlazaFederationEventKind>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    internal static bool Valid(PlazaWireEvent? change) => change is not null && RoomDirectoryClientOptions.Identifier(change.Build)
        && change.User > 0 && Enum.IsDefined(change.Kind) && change.Kind switch
        {
            PlazaFederationEventKind.Entered => change.Position is null && change.Member is { } member
                && !string.IsNullOrWhiteSpace(member.Nickname) && member.Nickname.Length <= 64 && !member.Nickname.Any(char.IsControl)
                && member.Level is >= 0 and <= 100000 && member.CharacterItemId >= 0 && member.ColorIndex is >= 0 and <= ushort.MaxValue
                && ValidEquipment(member),
            PlazaFederationEventKind.Moved => change.Member is null && change.Position is { Length: > 0 and <= MaxPositionBytes },
            _ => change.Member is null && change.Position is null,
        };

    private static bool ValidEquipment(PlazaWireMember member)
    {
        if (member.EquippedItemIds is not { Length: <= MaxEquippedItems } ids
            || member.ItemAttributes is not { Length: <= InventoryLimits.MaxInventorySlotsPerBag } attributes)
            return false;
        var selected = new HashSet<int>();
        foreach (var itemId in ids)
            if (itemId <= 0 || !selected.Add(itemId)) return false;
        long bytes = sizeof(uint);
        foreach (var attribute in attributes)
        {
            if (attribute is null || !selected.Contains(attribute.ItemId)
                || attribute.Values is not { Count: <= InventoryLimits.MaxInventorySlotsPerBag } values
                || attribute.Unknown32 is null) return false;
            // cn-774 sub_449C80 reads five fixed words, the value words and UTF-16 text inside a 16-bit frame.
            bytes += 5 * sizeof(uint) + (long)values.Count * sizeof(uint) + (long)attribute.Unknown32.Length * sizeof(char);
            if (bytes > ushort.MaxValue) return false;
        }
        return true;
    }

    internal static PlazaWireEvent ToWire(string build, PlazaFederationEvent change)
    {
        PlazaWireMember? appearance = null;
        if (change.Member is { } member)
        {
            var snapshot = new RoundEquipmentSnapshot(member.EquippedItemIds, member.ItemAttributes);
            appearance = new(member.Nickname, member.Level, member.CharacterItemId, [.. snapshot.WornItemIds], member.ColorIndex)
            {
                ItemAttributes = [.. snapshot.ItemAttributes],
            };
        }
        var wire = new PlazaWireEvent(build, change.Kind, change.UserNumber, appearance,
            change.Kind == PlazaFederationEventKind.Moved ? change.Position.ToArray() : null);
        if (!Valid(wire)) throw new InvalidDataException("The plaza publication exceeds the federation appearance bounds.");
        return wire;
    }

    internal static PlazaFederationEvent FromWire(PlazaWireEvent change)
    {
        if (!Valid(change)) throw new InvalidDataException("The remote plaza appearance is malformed or exceeds its bounds.");
        PlazaMember? appearance = null;
        if (change.Member is { } member)
        {
            var snapshot = new RoundEquipmentSnapshot(member.EquippedItemIds, member.ItemAttributes);
            appearance = new(0, change.User, member.Nickname, member.Level, member.CharacterItemId, snapshot.WornItemIds)
            {
                ColorIndex = checked((ushort)member.ColorIndex),
                ItemAttributes = snapshot.ItemAttributes,
            };
        }
        return new(change.Kind, change.User, appearance,
            change.Position is { } position ? position.ToArray() : ReadOnlyMemory<byte>.Empty);
    }
}
