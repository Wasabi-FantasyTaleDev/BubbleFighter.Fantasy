using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;

namespace BubbleFighter.Fantasy.Persistence;

/// <summary>
/// The stored form of one Mysterystore selection's board, shared by the SQL providers. Owner, scope, token, deadline
/// and purchased bits are kept beside it by each provider; this document holds only the frozen cards and their terms.
/// </summary>
internal static class MysteryStoreSelectionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<InventoryGrantKind>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>Encodes a board as compact JSON.</summary>
    internal static string Encode(MysteryStoreBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var json = JsonSerializer.Serialize(new BoardDocument(board.CatalogRevision, board.CoinItemId, board.RefreshCoinCost,
            board.GroupStockVariants, board.Offers.Select(offer => new OfferDocument(offer.NativeKey, offer.CoinPrice,
                offer.Unknown4, offer.UnknownName, offer.Grants.Select(term => new GrantDocument(term.ItemId, term.Kind,
                    term.Amount, term.Maximum, term.RejectOwned)).ToArray())).ToArray()), Options);
        if (Encoding.UTF8.GetByteCount(json) > 65536)
            throw new InvalidDataException("A Mysterystore board exceeds its storage limit.");
        return json;
    }

    /// <summary>Decodes a stored board; any unknown, missing or malformed member is a damaged row, not a default.</summary>
    internal static MysteryStoreBoard Decode(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        if (json.Length > 65536 || Encoding.UTF8.GetByteCount(json) > 65536)
            throw new InvalidDataException("A stored Mysterystore board exceeds its storage limit.");
        BoardDocument document;
        try
        {
            document = JsonSerializer.Deserialize<BoardDocument>(json, Options)
                ?? throw new InvalidDataException("A stored Mysterystore board is null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("A stored Mysterystore board is malformed.", error);
        }
        if (document.Offers is null || document.Offers.Length != 6
            || document.Offers.Any(offer => offer?.Grants is not { Length: >= 1 and <= 10 }
                || offer.Grants.Any(term => term is null)))
            throw new InvalidDataException("A stored Mysterystore board lacks its cards or their grants.");
        try
        {
            return new(document.CatalogRevision, document.CoinItemId, document.RefreshCoinCost, document.GroupStockVariants,
                document.Offers.Select(offer => new MysteryStoreOffer(offer.NativeKey, offer.CoinPrice, offer.Unknown4,
                    offer.UnknownName, offer.Grants.Select(term => new InventoryGrantTerm(term.ItemId, term.Kind,
                        term.Amount, term.Maximum, term.RejectOwned)).ToArray())).ToArray());
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("A stored Mysterystore board breaks its card bounds.", error);
        }
    }

    private sealed record BoardDocument(string CatalogRevision, int CoinItemId, ushort RefreshCoinCost,
        bool GroupStockVariants, OfferDocument[] Offers);

    private sealed record OfferDocument(int NativeKey, ushort CoinPrice, uint Unknown4, string UnknownName,
        GrantDocument[] Grants);

    private sealed record GrantDocument(int ItemId, InventoryGrantKind Kind, int Amount, int Maximum, bool RejectOwned);
}
