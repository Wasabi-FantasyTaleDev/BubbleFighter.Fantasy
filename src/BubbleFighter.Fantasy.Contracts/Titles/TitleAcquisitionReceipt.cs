using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BubbleFighter.Fantasy.Contracts.Titles;

/// <summary>Retains a title's one-time bonus grant independently of the lifetime of its inventory item.</summary>
/// <param name="TitleId">The native title identity used as the account-and-scope receipt key.</param>
/// <param name="StockId">The native title stock whose acquisition committed the bonuses.</param>
/// <param name="Fingerprint">The SHA-256 of the resolved bonus terms and title stock.</param>
public sealed record TitleAcquisitionReceipt(int TitleId, int StockId, string Fingerprint)
{
    /// <summary>Hashes copied bonus terms so an existing acquisition cannot be replayed under changed reward contents.</summary>
    public static TitleAcquisitionReceipt For(TitleClaimCommand command)
    {
        command.Validate();
        var rows = command.BonusGrants.OrderBy(term => term.ItemId).ThenBy(term => term.Kind).ThenBy(term => term.Amount)
            .Select(term => string.Create(CultureInfo.InvariantCulture,
                $"item:{term.ItemId}:{(int)term.Kind}:{term.Amount}:{term.Maximum}:{term.RejectOwned}"));
        var medals = command.BonusMedals.OrderBy(row => row.MedalId)
            .Select(row => string.Create(CultureInfo.InvariantCulture, $"medal:{row.MedalId}:{row.Count}"));
        var text = string.Create(CultureInfo.InvariantCulture, $"title:{command.TitleId}:{command.StockId}:{command.GrantTerm.ItemId};")
            + string.Join(";", rows.Concat(medals));
        return new(command.TitleId, command.StockId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>Rejects a malformed persisted receipt or an attempted replay with different native bonus terms.</summary>
    public void RequireSameAcquisition(TitleAcquisitionReceipt stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.TitleId <= 0 || stored.StockId <= 0 || stored.Fingerprint is null
            || stored.Fingerprint.Length != 64 || stored.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("A title acquisition receipt is malformed.");
        if (stored != this)
            throw new InvalidOperationException("A title acquisition was replayed with different bonus contents.");
    }
}
