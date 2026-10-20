using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>One field of an SDK plugin's config schema (spec section 5).</summary>
/// <param name="Name">The snake_case key.</param>
/// <param name="Type">One of string, int, bool, number, list, map.</param>
/// <param name="Required">Whether the row must provide it when there is no default.</param>
/// <param name="Default">The default value, when the schema has one.</param>
internal sealed record FieldSpec(string Name, string Type, bool Required, JsonElement? Default);

/// <summary>A definition returned by <c>describe</c>, validated and resolved against the host's contracts.</summary>
internal sealed class RemoteDefinition
{
    private static readonly Lazy<ILookup<string, Type>> HostContracts = new(() =>
        ContractSchema.ServiceTypes
            .Where(type => type.Assembly == typeof(LoginRequested).Assembly || type == typeof(IMessageRouter))
            .ToLookup(type => type.Name, StringComparer.Ordinal));

    private static readonly string[] FieldTypes = ["string", "int", "bool", "number", "list", "map"];

    private static readonly Lazy<ILookup<string, Type>> MessageTypes = new(() =>
        typeof(LoginRequested).Assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IMessage).IsAssignableFrom(type))
            .ToLookup(type => type.Name, StringComparer.Ordinal));

    /// <summary>Gets the plugin id rows name.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the declared release version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the protocol selected by the process during initialization.</summary>
    public int ProtocolVersion { get; init; } = 1;

    /// <summary>Gets the declaration the host pins: contracts built from the definition's lists.</summary>
    public required PluginAttribute Declaration { get; init; }

    /// <summary>Gets the message types the plugin may register handlers for, by short name.</summary>
    public required IReadOnlyDictionary<string, Type> Handles { get; init; }

    /// <summary>Gets the message types the plugin may send, by short name.</summary>
    public required IReadOnlyDictionary<string, Type> Sends { get; init; }

    /// <summary>Gets the message types for which the plugin may install middleware, by short name.</summary>
    public required IReadOnlyDictionary<string, Type> Middleware { get; init; }

    /// <summary>Gets the service interfaces the plugin may consume, keyed by schema short name.</summary>
    public required IReadOnlyDictionary<string, Type> RequiresServices { get; init; }

    /// <summary>Gets the optional service interfaces the plugin may consume, keyed by schema short name.</summary>
    public required IReadOnlyDictionary<string, Type> OptionalServices { get; init; }

    /// <summary>Gets required ordered extension interfaces consumed through explicit collection selections.</summary>
    public IReadOnlyDictionary<string, Type> RequiresManyServices { get; init; } = new Dictionary<string, Type>();

    /// <summary>Gets optional ordered extension interfaces; an absent provider produces an empty collection.</summary>
    public IReadOnlyDictionary<string, Type> OptionalManyServices { get; init; } = new Dictionary<string, Type>();

    /// <summary>Gets the singleton service interfaces this definition may provide.</summary>
    public IReadOnlyDictionary<string, Type> ProvidedServices { get; init; } = new Dictionary<string, Type>();

    /// <summary>Gets the extension interfaces this definition may publish as multiple registrations.</summary>
    public IReadOnlyDictionary<string, Type> ProvidedManyServices { get; init; } = new Dictionary<string, Type>();

    /// <summary>Gets the decoratable singleton interfaces this definition may wrap.</summary>
    public IReadOnlyDictionary<string, Type> DecoratedServices { get; init; } = new Dictionary<string, Type>();

    /// <summary>Gets event subscriptions, each restricted to its declared dispatch mode and result type.</summary>
    public IReadOnlyList<RemoteEventContract> Subscribes { get; init; } = [];

    /// <summary>Gets event publications, each restricted to its declared dispatch mode and result type.</summary>
    public IReadOnlyList<RemoteEventContract> Publishes { get; init; } = [];

    /// <summary>Gets the config schema.</summary>
    public required IReadOnlyList<FieldSpec> Fields { get; init; }

    /// <summary>Parses and validates one definition object.</summary>
    /// <param name="element">The entry of the <c>describe</c> result.</param>
    /// <param name="source">The source label for error messages.</param>
    /// <param name="protocolVersion">The negotiated runtime protocol version.</param>
    /// <param name="allowedServices">The loader entry's allowed contract names, or null when every contract is allowed.</param>
    /// <param name="allowedEvents">The loader entry's allowed event type names, or null when every event is allowed.</param>
    /// <returns>The resolved definition.</returns>
    /// <exception cref="ConfigException">Thrown for malformed fields, unknown contracts, declarations outside the entry's allowlists or ambiguous message names.</exception>
    public static RemoteDefinition Parse(JsonElement element, string source, int protocolVersion = 1,
        IReadOnlySet<string>? allowedServices = null, IReadOnlySet<string>? allowedEvents = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"SDK plugin source '{source}': a definition must be an object.");
        var id = RequiredString(element, "id", source, null);
        var where = $"SDK plugin '{id}' (source '{source}')";
        var version = RequiredString(element, "version", source, id);
        if (!System.Version.TryParse(version, out _))
            throw new ConfigException($"{where}: field 'version' value '{version}' is not a parseable version.");
        var description = OptionalString(element, "description", where);
        var builds = Strings(element, "supported_builds", where);
        foreach (var build in builds)
        {
            try { _ = BuildRange.Parse(build); }
            catch (FormatException ex) { throw new ConfigException($"{where}: field 'supported_builds': {ex.Message}"); }
        }
        var families = Strings(element, "supported_families", where);
        if (families.FirstOrDefault(family => family is not ("legacy" or "modern")) is { } unknownFamily)
            throw new ConfigException($"{where}: field 'supported_families' lists '{unknownFamily}'; the families are legacy and modern.");

        var requiredServices = HostTypes(element, "requires", where);
        AllowServices(requiredServices, "requires", allowedServices, where);
        var optionalServices = HostTypes(element, "optional", where);
        AllowServices(optionalServices, "optional", allowedServices, where);
        var requiredMany = HostTypes(element, "requires_many", where);
        AllowServices(requiredMany, "requires_many", allowedServices, where);
        var optionalMany = HostTypes(element, "optional_many", where);
        AllowServices(optionalMany, "optional_many", allowedServices, where);
        if (protocolVersion < 2 && requiredMany.Count + optionalMany.Count > 0)
            throw new ConfigException($"{where}: requires_many and optional_many require protocol v2.");
        foreach (var name in optionalMany.Keys.Where(requiredMany.ContainsKey).ToArray())
            optionalMany.Remove(name);
        if (requiredServices.Keys.Concat(optionalServices.Keys).Intersect(requiredMany.Keys.Concat(optionalMany.Keys)).Any())
            throw new ConfigException($"{where}: a service cannot be consumed as both singleton and multiple.");
        if (requiredMany.ContainsKey(nameof(IMessageRouter)) || optionalMany.ContainsKey(nameof(IMessageRouter)))
            throw new ConfigException($"{where}: IMessageRouter is always a singleton dependency.");
        if (protocolVersion == 1 && requiredServices.Values.Concat(optionalServices.Values).Any(t => t != typeof(IMessageRouter)))
            throw new ConfigException($"{where}: protocol v1 allows only IMessageRouter in requires and optional.");
        foreach (var name in optionalServices.Keys.Where(name => requiredServices.ContainsKey(name)).ToArray())
            optionalServices.Remove(name);
        var requires = requiredServices.Values.ToList();
        var optional = optionalServices.Values.ToList();
        var provided = HostTypes(element, "provides", where);
        AllowServices(provided, "provides", allowedServices, where);
        var providedMany = HostTypes(element, "provides_many", where);
        AllowServices(providedMany, "provides_many", allowedServices, where);
        var decorated = HostTypes(element, "decorates", where);
        AllowServices(decorated, "decorates", allowedServices, where);
        if (protocolVersion < 2 && provided.Count + providedMany.Count + decorated.Count > 0)
            throw new ConfigException($"{where}: provides, provides_many and decorates require protocol v2.");
        foreach (var type in provided.Values.Concat(providedMany.Values).Concat(decorated.Values).Distinct())
            RemoteServiceProvider.Validate(type);
        if (provided.Keys.Intersect(providedMany.Keys).Any())
            throw new ConfigException($"{where}: provides and provides_many cannot name the same service.");
        if (!requires.Contains(typeof(IMessageRouter)))
            requires.Insert(0, typeof(IMessageRouter));
        optional.Remove(typeof(IMessageRouter));
        requiredServices[typeof(IMessageRouter).Name] = typeof(IMessageRouter);
        optionalServices.Remove(typeof(IMessageRouter).Name);

        var handles = Messages(element, "handles", where);
        var sends = Messages(element, "sends", where);
        var middleware = Messages(element, "middleware", where);
        var overrides = Strings(element, "overrides", where);
        var subscribes = RemoteEventContract.Parse(element, "subscribes", where);
        AllowEvents(subscribes, "subscribes", allowedEvents, where);
        var publishes = RemoteEventContract.Parse(element, "publishes", where);
        AllowEvents(publishes, "publishes", allowedEvents, where);
        if (protocolVersion < 2 && subscribes.Count + publishes.Count + middleware.Count > 0)
            throw new ConfigException($"{where}: event declarations require protocol v2.");
        var declaration = new PluginAttribute(id)
        {
            Description = description,
            SupportedBuilds = [.. builds],
            SupportedFamilies = [.. families],
            Requires = [.. requires],
            Optional = [.. optional],
            Provides = [.. provided.Values],
            ProvidesMany = [.. providedMany.Values],
            Decorates = [.. decorated.Values],
            RequiresMany = [.. requiredMany.Values.Concat(sends.Values.Select(type => typeof(IMessageSender<>).MakeGenericType(type))).Distinct()],
            OptionalMany = [.. optionalMany.Values
                .Concat(handles.Values.Select(type => typeof(IInboundMessageBinding<>).MakeGenericType(type)))
                .Concat(middleware.Values.Select(type => typeof(IInboundMessageBinding<>).MakeGenericType(type)))
                .Distinct()],
            Overrides = [.. overrides],
            Middleware = [.. middleware.Keys],
        };
        return new RemoteDefinition
        {
            Id = id,
            Version = version,
            ProtocolVersion = protocolVersion,
            Declaration = declaration,
            Handles = handles,
            Sends = sends,
            Middleware = middleware,
            RequiresServices = requiredServices,
            OptionalServices = optionalServices,
            RequiresManyServices = requiredMany,
            OptionalManyServices = optionalMany,
            ProvidedServices = provided,
            ProvidedManyServices = providedMany,
            DecoratedServices = decorated,
            Subscribes = subscribes,
            Publishes = publishes,
            Fields = ParseFields(element, where),
        };
    }

    /// <summary>Validates a row's config against the schema and returns the defaults-applied object the plugin receives.</summary>
    /// <param name="row">The row being activated.</param>
    /// <returns>The config object, keys exactly as the schema names them.</returns>
    /// <exception cref="ConfigException">Thrown listing every unknown key, missing field and type mismatch.</exception>
    public JsonElement BindConfig(PluginRow row)
    {
        var errors = new List<string>();
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (row.Config is not null)
            foreach (var (key, value) in row.Config)
                map[key] = value;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in map.Keys.Where(key => Fields.All(field => field.Name != key)))
            errors.Add($"unknown key '{key}'");
        foreach (var field in Fields)
        {
            if (map.TryGetValue(field.Name, out var value))
            {
                if (Check(field.Type, value, field.Name, out var normalized, errors))
                    result[field.Name] = normalized;
            }
            else if (field.Default is { } fallback)
                result[field.Name] = fallback;
            else if (field.Required)
                errors.Add($"key '{field.Name}' is required");
        }
        if (errors.Count > 0)
            throw new ConfigException(row.Id, errors);
        return JsonSerializer.SerializeToElement(result);
    }

    private static bool Check(string type, object? value, string path, out object? normalized, List<string> errors)
    {
        normalized = null;
        if (value is null)
        {
            errors.Add($"key '{path}': null is not valid for {type}");
            return false;
        }
        switch (type)
        {
            case "string" when value is string text:
                normalized = text;
                return true;
            case "bool" when value is bool flag:
                normalized = flag;
                return true;
            case "int" when ConfigValues.TryInteger(value, out var integer):
                normalized = integer;
                return true;
            case "number" when ConfigValues.TryInteger(value, out var whole):
                normalized = whole;
                return true;
            case "number" when ConfigValues.TryNumber(value, out var real):
                normalized = real;
                return true;
            case "list" when ConfigValues.TryList(value, out var list):
                normalized = list.Select(Plain).ToList();
                return true;
            case "map" when ConfigValues.TryMap(value, out var map):
                normalized = map.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal);
                return true;
            default:
                errors.Add($"key '{path}': '{value}' is not a valid {type}");
                return false;
        }
    }

    private static object? Plain(object? value) =>
        ConfigValues.TryMap(value, out var map) ? map.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal)
        : ConfigValues.TryList(value, out var list) ? list.Select(Plain).ToList()
        : value is decimal number ? (double)number
        : value;

    private static void AllowServices(Dictionary<string, Type> services, string field, IReadOnlySet<string>? allowed, string where)
    {
        if (allowed is null)
            return;
        foreach (var name in services.Keys.Where(name => !allowed.Contains(name)).OrderBy(name => name, StringComparer.Ordinal))
            throw new ConfigException($"{where}: field '{field}' names service '{name}', which is not in the loader entry's allowed_services list.");
    }

    private static void AllowEvents(IReadOnlyList<RemoteEventContract> events, string field, IReadOnlySet<string>? allowed, string where)
    {
        if (allowed is null)
            return;
        foreach (var contract in events.OrderBy(contract => contract.Event.Name, StringComparer.Ordinal))
        {
            if (allowed.Contains(contract.Event.Name) || allowed.Contains(ContractSchema.TypeName(contract.Event)))
                continue;
            throw new ConfigException($"{where}: field '{field}' names event '{contract.Event.Name}', which is not in the loader entry's allowed_events list.");
        }
    }

    private static string RequiredString(JsonElement element, string name, string source, string? id)
    {
        var where = id is null ? $"SDK plugin source '{source}'" : $"SDK plugin '{id}' (source '{source}')";
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ConfigException($"{where}: definition field '{name}' must be a nonempty string.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : throw new ConfigException($"{where}: field '{name}' must be a string.");
    }

    private static List<string> Strings(JsonElement element, string name, string where)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return result;
        if (value.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"{where}: field '{name}' must be a list of strings.");
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new ConfigException($"{where}: field '{name}' must contain nonempty strings.");
            if (result.Contains(item.GetString()!))
                throw new ConfigException($"{where}: field '{name}' lists '{item.GetString()}' twice.");
            result.Add(item.GetString()!);
        }
        return result;
    }

    private static Dictionary<string, Type> HostTypes(JsonElement element, string name, string where)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var contract in Strings(element, name, where))
        {
            var matches = HostContracts.Value[contract].ToArray();
            if (matches.Length == 0)
                throw new ConfigException(
                    $"{where}: field '{name}' names unknown host contract '{contract}'; allowed contracts are [{string.Join(", ", HostContracts.Value.Select(group => group.Key).OrderBy(key => key, StringComparer.Ordinal))}].");
            if (matches.Length > 1)
                throw new ConfigException(
                    $"{where}: field '{name}' name '{contract}' is ambiguous between {string.Join(" and ", matches.Select(type => type.FullName))}.");
            result.Add(contract, matches[0]);
        }
        return result;
    }

    private static Dictionary<string, Type> Messages(JsonElement element, string name, string where)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var typeName in Strings(element, name, where))
        {
            var matches = MessageTypes.Value[typeName].ToArray();
            if (matches.Length == 0)
                throw new ConfigException($"{where}: field '{name}' names unknown message type '{typeName}'; it must be an IMessage record in BubbleFighter.Fantasy.Contracts.");
            if (matches.Length > 1)
                throw new ConfigException(
                    $"{where}: field '{name}' name '{typeName}' is ambiguous between {string.Join(" and ", matches.Select(type => type.FullName))}.");
            result[typeName] = matches[0];
        }
        return result;
    }

    private static List<FieldSpec> ParseFields(JsonElement element, string where)
    {
        var result = new List<FieldSpec>();
        if (!element.TryGetProperty("config", out var config) || config.ValueKind == JsonValueKind.Null)
            return result;
        if (config.ValueKind != JsonValueKind.Object)
            throw new ConfigException($"{where}: field 'config' must be an object of field specs.");
        foreach (var property in config.EnumerateObject())
        {
            var spec = property.Value;
            if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !FieldTypes.Contains(type.GetString()))
                throw new ConfigException($"{where}: config field '{property.Name}' needs a 'type' of {string.Join("|", FieldTypes)}.");
            var required = spec.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.True;
            if (spec.TryGetProperty("required", out var check) && check.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ConfigException($"{where}: config field '{property.Name}' has a non-boolean 'required'.");
            JsonElement? fallback = spec.TryGetProperty("default", out var defaultValue) ? defaultValue.Clone() : null;
            result.Add(new FieldSpec(property.Name, type.GetString()!, required, fallback));
        }
        return result;
    }
}
