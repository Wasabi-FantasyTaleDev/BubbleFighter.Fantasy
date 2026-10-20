using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Localization;

/// <summary>Loads bounded game-language resources and publishes the game text service.</summary>
[Plugin("localization", Provides = [typeof(IGameText)])]
public sealed class LocalizationPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<LocalizationOptions>();
        var file = new FileInfo(options.ResourcesPath);
        if (!file.Exists || file.Length > 1024 * 1024)
            throw new ConfigException("Game localization resources must exist and be at most one MiB.");
        var resources = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file.FullName))
            ?? throw new ConfigException("Game localization resources must contain language dictionaries.");
        if (resources.Count is < 1 or > 32 || !resources.ContainsKey(options.DefaultLanguage)
            || resources.Values.Any(messages => messages.Count is < 1 or > 512 || messages.Values.Any(string.IsNullOrWhiteSpace)))
            throw new ConfigException("Invalid game localization languages or messages.");
        if (options.RegionLanguages.Any(mapping => !resources.ContainsKey(mapping.Language)))
            throw new ConfigException("Every configured region language must exist in the resource file.");
        if (options.ProfileLanguages.Any(mapping => !resources.ContainsKey(mapping.Language)))
            throw new ConfigException("Every configured profile language must exist in the resource file.");
        var languages = options.RegionLanguages.ToDictionary(mapping => mapping.Region, mapping => mapping.Language, StringComparer.OrdinalIgnoreCase);
        var profileLanguages = options.ProfileLanguages.ToDictionary(mapping => mapping.Profile, mapping => mapping.Language, StringComparer.OrdinalIgnoreCase);
        context.Effect(context.Services.Register<IGameText>(new GameText(options.DefaultLanguage, languages, profileLanguages, resources)));
        return ValueTask.CompletedTask;
    }

    private sealed class GameText(string defaultLanguage, Dictionary<string, string> languages, Dictionary<string, string> profileLanguages,
        Dictionary<string, Dictionary<string, string>> resources) : IGameText
    {
        public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
            TryFormat(region, key, out var text, arguments) ? text : throw new InvalidOperationException($"Missing game message '{key}'.");

        public string Format(ClientProfile profile, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
            TryFormat(profile, key, out var text, arguments) ? text : throw new InvalidOperationException($"Missing game message '{key}'.");

        public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null) =>
            TryFormat(null, region, key, out text, arguments);

        public bool TryFormat(ClientProfile profile, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null) =>
            TryFormat(profileLanguages.GetValueOrDefault(profile.Id), profile.Region, key, out text, arguments);

        // Lookup order: the build's own language (only the wording that differs from its region), the region's
        // language, then the default language.
        private bool TryFormat(string? profileLanguage, string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments)
        {
            string? found = null;
            if (profileLanguage is null || !resources[profileLanguage].TryGetValue(key, out found))
            {
                var language = languages.GetValueOrDefault(region, defaultLanguage);
                if (!resources[language].TryGetValue(key, out found) && !resources[defaultLanguage].TryGetValue(key, out found))
                {
                    text = "";
                    return false;
                }
            }
            if (arguments is not null)
                foreach (var (name, value) in arguments)
                    found = found.Replace("{" + name + "}", value, StringComparison.Ordinal);
            text = found;
            return true;
        }
    }
}

/// <summary>Configures the game message file, fallback language and region-to-language selection.</summary>
public sealed class LocalizationOptions : IValidatableOptions
{
    /// <summary>Gets or sets the JSON file containing one message dictionary per language.</summary>
    [Required]
    public string ResourcesPath { get; set; } = "data/locales/game-messages.json";

    /// <summary>Gets or sets the fallback language used for unknown regions or untranslated keys.</summary>
    [Required]
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>Gets or sets the language selected for each client region.</summary>
    [Required]
    public LocalizationRegionLanguage[] RegionLanguages { get; set; } = [];

    /// <summary>
    /// Gets or sets the language layered over the region language for individual client builds. Such a language holds
    /// only the wording that differs from the region's, for example mode names a build's own client spells differently.
    /// </summary>
    public LocalizationProfileLanguage[] ProfileLanguages { get; set; } = [];

    /// <summary>Rejects oversized or ambiguous region-language and profile-language selections.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (RegionLanguages is null || ProfileLanguages is null)
            yield break;
        if (RegionLanguages.Length > 64)
            yield return new ValidationResult("At most 64 region languages may be configured.");
        if (RegionLanguages.Select(mapping => mapping.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RegionLanguages.Length)
            yield return new ValidationResult("Each region must have exactly one configured language.");
        if (ProfileLanguages.Length > 64)
            yield return new ValidationResult("At most 64 profile languages may be configured.");
        if (ProfileLanguages.Select(mapping => mapping.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ProfileLanguages.Length)
            yield return new ValidationResult("Each profile must have exactly one configured language.");
    }
}

/// <summary>Selects a resource language for one client region in a YAML sequence entry.</summary>
public sealed class LocalizationRegionLanguage
{
    /// <summary>Gets or sets the client region identifier, compared without case sensitivity.</summary>
    [Required]
    public string Region { get; set; } = "";

    /// <summary>Gets or sets the language identifier present in the message resource file.</summary>
    [Required]
    public string Language { get; set; } = "";
}

/// <summary>Selects a resource language layered over the region language for one client build.</summary>
public sealed class LocalizationProfileLanguage
{
    /// <summary>Gets or sets the client profile id (for example <c>cn-774</c>), compared without case sensitivity.</summary>
    [Required]
    public string Profile { get; set; } = "";

    /// <summary>Gets or sets the language identifier present in the message resource file.</summary>
    [Required]
    public string Language { get; set; } = "";
}
