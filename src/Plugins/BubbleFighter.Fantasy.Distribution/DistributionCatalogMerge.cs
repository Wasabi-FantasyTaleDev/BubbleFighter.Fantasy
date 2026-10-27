using BubbleFighter.Fantasy.Contracts.Distribution;
using System.Security.Cryptography;
using System.Text.Json;

namespace BubbleFighter.Fantasy.Distribution;

internal static class DistributionCatalogMerge
{
    internal static IReadOnlyList<DistributionStoreEntry> Merge(IReadOnlyList<DistributionSource> sources,
        IReadOnlyList<DistributionCatalogSnapshot> catalogs, string? selectedSource = null)
    {
        var sourceIds = sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        if (sourceIds.Count != sources.Count) throw new InvalidDataException("Configured source identities are duplicated.");
        if (selectedSource is not null && !sourceIds.Contains(selectedSource)) throw new KeyNotFoundException("The selected source is not configured.");
        var snapshots = new Dictionary<string, DistributionCatalogSnapshot>(StringComparer.Ordinal);
        foreach (var catalog in catalogs)
        {
            if (!sourceIds.Contains(catalog.SourceId) || !snapshots.TryAdd(catalog.SourceId, catalog))
                throw new InvalidDataException("A catalog has an unknown or duplicate source identity.");
        }
        var entries = new Dictionary<DistributionPackageIdentity, List<DistributionDownloadChoice>>();
        foreach (var source in sources)
        {
            if (selectedSource is not null && selectedSource != source.Id || !snapshots.TryGetValue(source.Id, out var snapshot)) continue;
            var identities = new HashSet<DistributionPackageIdentity>();
            foreach (var package in snapshot.Packages)
            {
                // Each source resolves its release choice before aggregation; ordering here is operator-owned.
                if (!identities.Add(package.Identity)) throw new InvalidDataException("A source supplied more than one current offer for a package.");
                if (!entries.TryGetValue(package.Identity, out var downloads)) entries.Add(package.Identity, downloads = []);
                downloads.Add(new(source.Id, source.Name, package)
                {
                    SelectionFingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Source = source, Package = package })))
                });
            }
        }
        return Array.AsReadOnly(entries.Select(entry => new DistributionStoreEntry(entry.Key, entry.Value.AsReadOnly())).ToArray());
    }
}
