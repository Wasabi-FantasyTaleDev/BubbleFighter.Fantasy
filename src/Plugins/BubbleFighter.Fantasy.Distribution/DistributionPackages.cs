using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Distribution;
using Tomlyn;
using Tomlyn.Model;

namespace BubbleFighter.Fantasy.Distribution;

/// <summary>
/// Builds the digest-verified file inventory of one server package selection. Two payload origins
/// exist: a repository attachment, which needs one named release asset carrying a publisher SHA-256,
/// and a manifest document, whose bytes must hash to the catalog's declared manifest digest before
/// they are parsed. A manifest may use the experimental neutral release format (only when its option
/// is enabled) or the schema-one server-application format; every manifest field is allowlisted, the
/// release identity, compatibility sets and aggregate size must agree with the selected offer, and
/// destination paths pass portable-path validation with reserved device names and parent-directory
/// destinations rejected. Preparation ends by revalidating the plan, so a selection that changed or
/// was withdrawn while a document was being read cannot be returned as current.
/// </summary>
internal sealed class DistributionPackages(IDistributionPlans plans, IDistributionTransport transport, DistributionPackageOptions options, CancellationToken lifetime = default)
    : IDistributionPackages
{
    /// <summary>
    /// Revalidates the selection, verifies the manifest bytes or attachment identity, and returns the
    /// complete file plan. Nothing is downloaded here; the plan lists what a later transfer must fetch.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown for a non-server target, a missing or failing manifest,
    /// a manifest that disagrees with the catalog, or an invalid payload path.</exception>
    /// <exception cref="NotSupportedException">Thrown when a neutral manifest appears while the experimental release format is disabled.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the selection changed or was withdrawn during preparation (<c>distribution_selection_changed</c>).</exception>
    public async ValueTask<DistributionFilePlan> PrepareAsync(string planId, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var plan = await plans.ValidateAsync(planId, token).ConfigureAwait(false);
        var offer = plan.Selection.Package;
        if (offer.Identity.Target != "server" || offer.Identity.Kind is not ("plugin" or "application"))
            throw new InvalidDataException("Only server packages can prepare payload files.");
        DistributionFilePlan result;
        if (plan.Asset is { } asset)
        {
            ValidatePath(asset.Name);
            if (asset.Name.Contains('/') || asset.Size < 0 || asset.Sha256 is not { } digest || !IsDigest(digest))
                throw new InvalidDataException("A repository inventory requires one named attachment with a publisher SHA-256.");
            result = new(plan, "repository-asset", null, offer.Builds, offer.Platforms,
                Array.AsReadOnly(new[] { new DistributionPayloadFile(asset.Name, null, asset.Size, digest.ToLowerInvariant(), asset) }), asset.Size);
        }
        else
        {
            if (offer.ManifestPath is null || offer.ManifestSha256 is not { } expected || !IsDigest(expected))
                throw new InvalidDataException("The selected offer does not contain a verified manifest reference.");
            var bytes = await transport.ReadDocumentAsync(plan.Source, offer.ManifestPath, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected)))
                throw new InvalidDataException("The release manifest SHA-256 disagrees with the selected catalog.");
            try { result = ReadManifest(plan, bytes, expected.ToLowerInvariant(), options.ExperimentalReleaseFormat); }
            catch (Exception error) when (error is TomlException or DecoderFallbackException or OverflowException)
            { throw new InvalidDataException("The release manifest is malformed or its aggregate size is out of range.", error); }
        }
        // Preparation may await a document read; a withdrawn or changed selection cannot be returned as current.
        var current = await plans.ValidateAsync(planId, token).ConfigureAwait(false);
        if (current.Id != plan.Id || current.SourceRevision != plan.SourceRevision || current.Source != plan.Source || current.Asset != plan.Asset ||
            current.Selection.SelectionFingerprint != plan.Selection.SelectionFingerprint || current.ExpiresAt != plan.ExpiresAt)
            throw new InvalidOperationException("distribution_selection_changed");
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static DistributionFilePlan ReadManifest(DistributionPlan plan, byte[] bytes, string digest, bool experimental)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'))
            ?? throw new InvalidDataException("The release manifest is empty.");
        OnlyKeys(root, "format", "schema", "release", "files");
        if (!root.TryGetValue("schema", out var schema) || schema is not 1L ||
            !root.TryGetValue("release", out var releaseValue) || releaseValue is not TomlTable release)
            throw new InvalidDataException("The release manifest schema is unsupported.");
        var offer = plan.Selection.Package;
        var neutral = root.ContainsKey("format");
        if (neutral)
        {
            if (!experimental) throw new NotSupportedException("The experimental distribution release format is not enabled.");
            if (Text(root, "format") != "fantasytale-distribution-release") throw new InvalidDataException("The release format is unsupported.");
            OnlyKeys(release, "catalog_id", "id", "target", "kind", "game", "version", "revision", "builds", "platforms", "payload_origin");
            if (Text(release, "catalog_id") != offer.CatalogId || Text(release, "target") != offer.Identity.Target || Text(release, "kind") != offer.Identity.Kind ||
                Text(release, "payload_origin") != "@catalog")
                throw new InvalidDataException("The release target, kind or payload origin disagrees with the selected offer.");
            // The current catalog adapter has no declared external origins; a manifest cannot introduce one.
            if (!release.TryGetValue("revision", out var revision) || revision is not long number || number != offer.Revision)
                throw new InvalidDataException("The release revision disagrees with the selected catalog.");
        }
        else
        {
            // Schema-one server application manifests have a separate meaning from neutral package manifests.
            if (offer.Identity.Kind != "application") throw new InvalidDataException("Server plugins require an explicit distribution release format.");
            OnlyKeys(release, "id", "game", "builds", "version");
        }
        if (Text(release, "id") != offer.Identity.Id || Text(release, "game") != offer.Identity.Game || Text(release, "version") != offer.Version)
            throw new InvalidDataException("The release identity disagrees with the selected catalog.");
        var builds = Strings(release, "builds");
        var platforms = neutral ? Strings(release, "platforms") : Array.Empty<string>();
        if (!SameSet(builds, offer.Builds) || !SameSet(platforms, offer.Platforms))
            throw new InvalidDataException("The release compatibility declarations disagree with the selected catalog.");
        if (!root.TryGetValue("files", out var filesValue) || filesValue is not TomlTableArray rows || rows.Count == 0)
            throw new InvalidDataException("The release requires a nonempty file inventory.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<DistributionPayloadFile>();
        long total = 0;
        foreach (var row in rows)
        {
            OnlyKeys(row, "path", "source_path", "size", "sha256");
            var path = Text(row, "path");
            var source = Text(row, "source_path");
            ValidatePath(path);
            ValidatePath(source);
            if (source.IndexOfAny(['%', '#']) >= 0) throw new InvalidDataException("Payload source paths cannot contain encoded or fragment segments.");
            if (!paths.Add(path)) throw new InvalidDataException("The inventory has duplicate destination paths.");
            if (!row.TryGetValue("size", out var sizeValue) || sizeValue is not long size || size < 0)
                throw new InvalidDataException("Payload sizes must be nonnegative integers.");
            var hash = Text(row, "sha256");
            if (!IsDigest(hash)) throw new InvalidDataException("Each payload requires a SHA-256 digest.");
            total = checked(total + size);
            files.Add(new(path, source, size, hash.ToLowerInvariant(), null));
        }
        foreach (var path in paths)
            for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
                if (paths.Contains(path[..i])) throw new InvalidDataException("A payload destination is also a parent directory.");
        if (offer.Size is { } expectedSize && expectedSize != total) throw new InvalidDataException("The manifest size disagrees with the selected catalog.");
        return new(plan, neutral ? "fantasytale-distribution-release" : "server-application-schema-1", digest,
            builds, platforms, files.AsReadOnly(), total);
    }

    private static bool SameSet(IReadOnlyList<string> first, IReadOnlyList<string> second) => first.Count == second.Count && first.ToHashSet(StringComparer.Ordinal).SetEquals(second);
    private static bool IsDigest(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    private static string Text(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is string text &&
        !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text : throw new InvalidDataException("The manifest requires a printable " + key + ".");
    private static void OnlyKeys(TomlTable table, params string[] names)
    {
        if (table.Keys.Except(names, StringComparer.Ordinal).Any()) throw new InvalidDataException("The manifest contains an unsupported field.");
    }
    private static IReadOnlyList<string> Strings(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not TomlArray rows || rows.Any(item => item is not string text || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)))
            throw new InvalidDataException("The manifest requires a compatibility string array.");
        var values = rows.Cast<string>().ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new InvalidDataException("Compatibility identifiers must be unique.");
        return Array.AsReadOnly(values);
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.IndexOfAny(['\\', ':', '?', '<', '>', '"', '|', '*']) >= 0 ||
            path.Split('/').Any(part => part is "" or "." or ".." || part != part.Trim() || part.EndsWith('.')))
            throw new InvalidDataException("The payload path is not a portable relative path.");
        foreach (var part in path.Split('/'))
        {
            var device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]))
                throw new InvalidDataException("The payload path contains a reserved device name.");
        }
    }
}
