using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Messenger;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>Configuration of the <c>messenger</c> row: trusted tenants, the game catalog and every bound the core enforces.</summary>
public sealed class MessengerOptions : IValidatableOptions
{
    /// <summary>Gets or sets the tenants whose tickets are accepted, each with its public verification key.</summary>
    public MessengerTenantOptions[] Tenants { get; set; } = [];

    /// <summary>Gets or sets whether a <c>messenger.tickets</c> row in the same process is trusted without listing its key.</summary>
    public bool TrustLocalIssuer { get; set; } = true;

    /// <summary>Gets or sets the catalog of games whose friends native clients group and label.</summary>
    public MessengerGameOptions[] Games { get; set; } = [];

    /// <summary>Gets or sets the maximum friends per account.</summary>
    [Range(1, 1000)] public int MaxFriends { get; set; } = 200;

    /// <summary>Gets or sets the maximum categories per account.</summary>
    [Range(0, 256)] public int MaxCategories { get; set; } = 32;

    /// <summary>Gets or sets the maximum pending requests addressed to one account.</summary>
    [Range(1, 1000)] public int MaxPendingRequests { get; set; } = 64;

    /// <summary>Gets or sets how long an unanswered request stays pending, in hours.</summary>
    [Range(1, 8760)] public int RequestHours { get; set; } = 720;

    /// <summary>Gets or sets the maximum category name length in UTF-16 units; native clients hold 32.</summary>
    [Range(1, 256)] public int MaxCategoryName { get; set; } = 32;

    /// <summary>Gets or sets the maximum friend request message length in UTF-16 units; native clients hold 256.</summary>
    [Range(0, 1024)] public int MaxRequestMessage { get; set; } = 256;

    /// <summary>Gets or sets the maximum nickname length accepted in tickets and lookups; native clients hold 64.</summary>
    [Range(1, 256)] public int MaxNickname { get; set; } = 64;

    /// <summary>Gets or sets friends per list page.</summary>
    [Range(1, 1000)] public int PageSize { get; set; } = 100;

    /// <summary>Gets or sets queued events retained per signed-in account; older events are dropped and the client is told to refetch.</summary>
    [Range(8, 4096)] public int EventQueue { get; set; } = 256;

    /// <summary>Gets or sets the maximum live sessions across all accounts.</summary>
    [Range(1, 1000000)] public int MaxSessions { get; set; } = 4096;

    /// <summary>Gets or sets the maximum live sessions per account; the oldest is ended first.</summary>
    [Range(1, 64)] public int MaxSessionsPerAccount { get; set; } = 4;

    /// <summary>Gets or sets the idle session lifetime in seconds; every authenticated call renews it.</summary>
    [Range(60, 86400)] public int SessionSeconds { get; set; } = 3600;

    /// <summary>Gets or sets after how many seconds without a call a signed-in account is shown offline.</summary>
    [Range(10, 3600)] public int PresenceTimeoutSeconds { get; set; } = 90;

    /// <summary>Gets or sets how many used ticket ids are remembered to refuse replays; tickets beyond it are refused until older ones expire.</summary>
    [Range(16, 1000000)] public int MaxRememberedTickets { get; set; } = 16384;

    /// <summary>Gets or sets how many mutation request keys are remembered for replay protection.</summary>
    [Range(16, 1000000)] public int MaxRequestKeys { get; set; } = 16384;

    /// <summary>Gets or sets how long a mutation request key is remembered, in seconds.</summary>
    [Range(10, 86400)] public int RequestKeySeconds { get; set; } = 600;

    /// <summary>Gets or sets how many chats may be live at once across all accounts.</summary>
    [Range(1, 1000000)] public int MaxChats { get; set; } = 4096;

    /// <summary>Gets or sets the longest chat line in UTF-16 units; native clients send at most 1023 (kr-812 sub_86BC00).</summary>
    [Range(1, 1023)] public int MaxChatMessage { get; set; } = 1023;

    /// <summary>Gets or sets the presence and expiry sweep period in seconds.</summary>
    [Range(1, 600)] public int SweepSeconds { get; set; } = 10;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Tenants is null || Tenants.Length > 256 || Tenants.Any(t => t is null))
        {
            yield return new("tenants must contain at most 256 entries.", [nameof(Tenants)]);
            yield break;
        }
        foreach (var tenant in Tenants)
        {
            if (!MessengerTicketOptions.ValidTenant(tenant.Key)) yield return new($"Tenant key '{tenant.Key}' must be 1-64 characters of a-z, 0-9, '.', '-' or '_'.", [nameof(Tenants)]);
            ValidationResult? keyError = null;
            try { using var key = MessengerTicketCodec.ImportKey(tenant.PublicKey ?? "", requirePrivate: false); }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { keyError = new($"Tenant '{tenant.Key}' needs a P-256 public key in PEM or base64 SubjectPublicKeyInfo.", [nameof(Tenants)]); }
            if (keyError is not null) yield return keyError;
        }
        if (Tenants.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != Tenants.Length) yield return new("tenant keys must be unique.", [nameof(Tenants)]);
        if (Games is null || Games.Length > 512 || Games.Any(g => g is null || string.IsNullOrWhiteSpace(g.FullName) || g.FullName.Length > 128 ||
                string.IsNullOrWhiteSpace(g.ShortName) || g.ShortName.Length > 128 || g.FullName.Any(char.IsControl) || g.ShortName.Any(char.IsControl)))
            yield return new("games must contain at most 512 entries with 1-128 character full and short names.", [nameof(Games)]);
        else if (Games.Any(g => g.Aliases is null || g.Aliases.Length > 32))
            yield return new("each game may list at most 32 aliases.", [nameof(Games)]);
        else
        {
            var codes = Games.SelectMany(g => g.Aliases.Prepend(g.Code)).ToArray();
            if (codes.Distinct().Count() != codes.Length) yield return new("game codes and aliases must be unique across games.", [nameof(Games)]);
        }
    }
}

/// <summary>A tenant whose tickets the messenger accepts.</summary>
public sealed class MessengerTenantOptions
{
    /// <summary>Gets or sets the tenant isolation key the tickets assert.</summary>
    public string Key { get; set; } = "";

    /// <summary>Gets or sets the tenant's P-256 verification key, as PEM or base64 SubjectPublicKeyInfo.</summary>
    public string PublicKey { get; set; } = "";
}

/// <summary>A game catalog entry.</summary>
public sealed class MessengerGameOptions
{
    /// <summary>Gets or sets the code friendships of this game are stored under.</summary>
    public NexonGameCode Code { get; set; }

    /// <summary>
    /// Gets or sets the other codes the same game carries in other regions. A client that announces one of them sees
    /// every friend, category and catalog entry of the game under its own code.
    /// </summary>
    public NexonGameCode[] Aliases { get; set; } = [];

    /// <summary>Gets or sets the full display name.</summary>
    public string FullName { get; set; } = "";

    /// <summary>Gets or sets the short display name.</summary>
    public string ShortName { get; set; } = "";
}

/// <summary>Configuration of the <c>messenger.tickets</c> row that signs this server's messenger tickets.</summary>
public sealed class MessengerTicketOptions : IValidatableOptions
{
    /// <summary>Gets or sets the tenant key asserted by the tickets.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>
    /// Gets or sets the P-256 private signing key as PEM or base64 PKCS#8, or <c>ephemeral</c> to generate a key at
    /// activation that only an in-process messenger can trust.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>The base URL used when <see cref="PublicUrl"/> is empty: the default listener port on the host the client used.</summary>
    internal const string DefaultPublicUrl = "http://{host}:8091";

    /// <summary>
    /// Gets or sets the messenger base URL handed to clients; <c>{host}</c> becomes the host the client used to reach this
    /// server. Empty selects <see cref="DefaultPublicUrl"/>, so a profile can leave the value to an optional variable
    /// without writing the placeholder's braces inside a variable default.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Gets the configured base URL, or <see cref="DefaultPublicUrl"/> when none is configured.</summary>
    internal string EffectivePublicUrl => string.IsNullOrEmpty(PublicUrl) ? DefaultPublicUrl : PublicUrl;

    /// <summary>Gets or sets the ticket lifetime in seconds.</summary>
    [Range(10, 600)] public int TicketSeconds { get; set; } = 300;

    /// <summary>Gets or sets the game code clients of each build show in the messenger; builds without an entry get no ticket.</summary>
    public MessengerBuildGameCode[] GameCodes { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!ValidTenant(Tenant)) yield return new("tenant must be 1-64 characters of a-z, 0-9, '.', '-' or '_'.", [nameof(Tenant)]);
        if (string.IsNullOrWhiteSpace(SigningKey)) yield return new("signing_key must be a P-256 private key or 'ephemeral'.", [nameof(SigningKey)]);
        else if (SigningKey != "ephemeral")
        {
            ValidationResult? keyError = null;
            try { using var key = MessengerTicketCodec.ImportKey(SigningKey, requirePrivate: true); }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { keyError = new("signing_key must be a P-256 private key in PEM or base64 PKCS#8.", [nameof(SigningKey)]); }
            if (keyError is not null) yield return keyError;
        }
        if (PublicUrl is null || PublicUrl.Length > 2048 || !Uri.TryCreate(EffectivePublicUrl.Replace("{host}", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            yield return new("public_url must be an HTTP(S) URL without credentials, query or fragment.", [nameof(PublicUrl)]);
        if (GameCodes is null || GameCodes.Length > 256 || GameCodes.Any(g => g is null || g.Build is not { Length: > 0 and <= 64 } || !g.Build.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            yield return new("game_codes must contain at most 256 entries, each with a valid build id.", [nameof(GameCodes)]);
        else if (GameCodes.Select(g => g.Build).Distinct(StringComparer.Ordinal).Count() != GameCodes.Length)
            yield return new("game_codes builds must be unique.", [nameof(GameCodes)]);
    }

    internal static bool ValidTenant(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

/// <summary>The game code a client build announces in the messenger.</summary>
public sealed class MessengerBuildGameCode
{
    /// <summary>Gets or sets the client build id.</summary>
    public string Build { get; set; } = "";

    /// <summary>
    /// Gets or sets the code the build's client compares friends against when it does not announce its own; a client
    /// that announces a code at messenger sign-in is shown that code instead.
    /// </summary>
    public NexonGameCode Code { get; set; }
}
