using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Tomlyn;
using Tomlyn.Model;

namespace BubbleFighter.Fantasy.Distribution;

internal sealed partial class DistributionSources
{
    /// <summary>
    /// Reads every selected source's catalog and merges their offers into one store snapshot. Each
    /// source contributes its own result: a timeout, an unreachable server and an invalid document are
    /// recorded as per-source failures (never silently dropped), while healthy sources bind their
    /// catalog digest onto every offer so a later selection can pin the exact bytes it browsed. The
    /// snapshot carries the source revision captured before the reads began.
    /// </summary>
    public async ValueTask<DistributionStoreSnapshot> BrowseAsync(string? sourceId = null, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        var sources = settings.Sources;
        if (sourceId is not null && !sources.Any(source => source.Id == sourceId)) throw new KeyNotFoundException("The selected source is not configured.");
        var catalogs = new List<DistributionCatalogSnapshot>();
        var failures = new List<DistributionSourceFailure>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.ThrowIfCancellationRequested();
            if (sourceId is not null && source.Id != sourceId) continue;
            try
            {
                var bytes = await ReadCatalogAsync(source, cancellationToken).ConfigureAwait(false);
                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var packages = (await ParseCatalogAsync(bytes, cancellationToken).ConfigureAwait(false))
                    .Select(package => package with { CatalogSha256 = digest }).ToArray();
                catalogs.Add(new(source.Id, packages));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { failures.Add(new(source.Id, "source_timeout")); }
            catch (HttpRequestException) { failures.Add(new(source.Id, "source_unavailable")); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or TomlException or DecoderFallbackException or KeyNotFoundException or InvalidOperationException or FormatException)
            { failures.Add(new(source.Id, "catalog_invalid")); }
        }
        return new(DistributionCatalogMerge.Merge(sources, catalogs, sourceId), failures.AsReadOnly()) { SourceRevision = settings.Revision };
    }

    /// <summary>
    /// Parses one catalog document into deduplicated package offers. Schemas 1 and 2 carry plain
    /// manifests and skip runtime/launcher/client kinds; schema 3 requires an explicit target and
    /// kind, and is the only schema that accepts a repository reference (provisional, not a published
    /// shared contract) — otherwise a manifest path plus a SHA-256 digest is required, and a
    /// cross-origin manifest reference is refused. A source may publish release history: the offer
    /// with the highest publisher revision wins, and an ambiguous history (a tie, or revisions left
    /// at zero) is an error rather than a lexical version guess.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown for an empty, unknown-schema or malformed catalog, or an ambiguous release history.</exception>
    private async Task<IReadOnlyList<DistributionPackageOffer>> ParseCatalogAsync(byte[] bytes, CancellationToken token)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'))
            ?? throw new InvalidDataException("The catalog is empty.");
        if (!root.TryGetValue("schema", out var schemaValue) || schemaValue is not long schema || schema is not (1 or 2 or 3))
            throw new InvalidDataException("The catalog schema must be 1, 2 or 3.");
        if (!root.TryGetValue("catalog", out var catalogValue) || catalogValue is not TomlTable catalog)
            throw new InvalidDataException("The catalog requires an identity table.");
        var catalogId = Text(catalog, "id");
        if (!root.TryGetValue("packages", out var packageRows)) return [];
        if (packageRows is not TomlTableArray rows) throw new InvalidDataException("Catalog packages must be table rows.");
        var offers = new List<DistributionPackageOffer>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var kind = Text(row, "kind");
            var target = schema == 3 ? Text(row, "target") : kind == "server" ? "server" : "client";
            if (schema < 3 && kind is "runtime" or "launcher" or "client") continue;
            if (schema < 3 && kind is not ("plugin" or "server") || schema == 3 && (target is not ("client" or "server") || kind is not ("plugin" or "application")))
                throw new InvalidDataException("The catalog package target or kind is invalid.");
            if (schema < 3) kind = kind == "server" ? "application" : "plugin";
            var id = Text(row, "id");
            var game = row.TryGetValue("game", out var gameValue) ? gameValue as string ?? throw new InvalidDataException("The game id must be text.") : "";
            var name = id;
            if (row.TryGetValue("name", out var names))
            {
                name = names switch
                {
                    string plain => plain,
                    TomlTable localized when localized.TryGetValue("en", out var english) && english is string text => text,
                    TomlTable localized when localized.Values.OfType<string>().FirstOrDefault() is { } text => text,
                    _ => throw new InvalidDataException("Package names must be text or a language table.")
                };
            }
            string version;
            string? repository = null;
            string? manifest = null;
            string? hash = null;
            RepositoryRelease? repositoryRelease = null;
            if (row.ContainsKey("github_repository"))
            {
                if (schema != 3) throw new InvalidDataException("Repository references require catalog schema 3.");
                repository = Text(row, "github_repository");
                var tag = row.ContainsKey("github_tag") ? Text(row, "github_tag") : null;
                var release = await ReadRepositoryReleaseAsync(repository, tag, token).ConfigureAwait(false);
                version = release.Tag;
                repositoryRelease = release;
            }
            else
            {
                version = Text(row, "version");
                if (schema == 3)
                {
                    if (!row.TryGetValue("manifest", out var reference) || reference is not TomlTable manifestTable)
                        throw new InvalidDataException("A package requires a manifest reference.");
                    manifest = Text(manifestTable, "path");
                    hash = Text(manifestTable, "sha256");
                    if (manifestTable.ContainsKey("origin")) throw new InvalidDataException("Cross-origin manifest references are not supported yet.");
                }
                else { manifest = Text(row, "manifest"); hash = Text(row, "manifest_sha256"); }
                _ = RelativePath(manifest);
                if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("A manifest requires a SHA-256 digest.");
                hash = hash.ToLowerInvariant();
            }
            var revision = row.TryGetValue("revision", out var revisionValue)
                ? revisionValue is long number && number >= 0 ? number : throw new InvalidDataException("Package revisions must be nonnegative integers.") : 0;
            offers.Add(new(new(target, kind, game, id), version, name, repository, manifest, hash)
            { RepositoryRelease = repositoryRelease, Revision = revision, CatalogId = catalogId,
                Builds = Strings(row, "builds"), Platforms = Strings(row, "platforms"),
                Size = row.TryGetValue("size", out var size) ? size is long length && length >= 0 ? length
                    : throw new InvalidDataException("The declared package size must be nonnegative.") : null });
        }
        // A source may contain release history; select its latest declared revision, never a lexical version guess.
        var selected = new List<DistributionPackageOffer>();
        foreach (var group in offers.GroupBy(offer => offer.Identity))
        {
            var ordered = group.OrderByDescending(offer => offer.Revision).ToArray();
            if (ordered.Length > 1 && (ordered[0].Revision == 0 || ordered[0].Revision == ordered[1].Revision))
                throw new InvalidDataException("Multiple releases require an unambiguous highest publisher revision.");
            selected.Add(ordered[0]);
        }
        return selected.AsReadOnly();
    }

    private static string Text(TomlTable row, string key) => row.TryGetValue(key, out var value) && value is string text
        && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text : throw new InvalidDataException("The catalog requires a printable " + key + ".");

    private static IReadOnlyList<string> Strings(TomlTable row, string key)
    {
        if (!row.TryGetValue(key, out var value)) return Array.Empty<string>();
        if (value is not TomlArray array || array.Any(item => item is not string text || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)))
            throw new InvalidDataException("Compatibility declarations must be printable string arrays.");
        var values = array.Cast<string>().ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new InvalidDataException("Compatibility declarations must be unique.");
        return Array.AsReadOnly(values);
    }
}
