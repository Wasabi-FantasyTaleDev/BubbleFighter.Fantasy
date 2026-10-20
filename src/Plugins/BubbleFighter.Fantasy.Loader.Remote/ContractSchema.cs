using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Describes the shared contracts without loading implementations or granting access to a service.</summary>
internal sealed class ContractSchema
{
    private static readonly Assembly[] Assemblies = [typeof(IPluginContext).Assembly, typeof(LoginRequested).Assembly];
    private readonly NullabilityInfoContext _nullability = new();
    private readonly JsonObject _types = new();

    internal static Type[] EventTypes => Assemblies.SelectMany(assembly => assembly.GetExportedTypes())
        .Where(type => !type.IsAbstract && typeof(IEvent).IsAssignableFrom(type)).OrderBy(TypeName, StringComparer.Ordinal).ToArray();

    private static readonly Lazy<Type[]> Results = new(() =>
    {
        // Result records need not occur in a service signature: a policy can be used only by an event pipeline.
        var candidates = Assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .Concat(ServiceTypes.SelectMany(Methods).Select(method => Unwrap(method.ReturnType)))
            .Concat([typeof(bool), typeof(string), typeof(int), typeof(long), typeof(double), typeof(decimal), typeof(Guid)]);
        return candidates.Distinct().Where(type => type != typeof(void) && !type.ContainsGenericParameters &&
                !typeof(IEvent).IsAssignableFrom(type) && new ContractSchema().Unsupported(type, []) is null)
            .OrderBy(TypeName, StringComparer.Ordinal).ToArray();
    });

    internal static Type[] EventResultTypes => Results.Value;

    internal static Type[] ServiceTypes => Assemblies.SelectMany(a => a.GetExportedTypes())
        .Where(t => t.IsInterface && t != typeof(IMessage) && t != typeof(IEvent))
        .OrderBy(TypeName, StringComparer.Ordinal).ToArray();

    internal static MethodInfo[] Methods(Type service) => service.GetInterfaces().Append(service)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        .Distinct().OrderBy(MethodId, StringComparer.Ordinal).ToArray();

    internal static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var definition = type.GetGenericTypeDefinition();
        return (definition.FullName ?? definition.Name).Split('`')[0] + "<" +
            string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    internal static string MethodId(MethodInfo method) => TypeName(method.DeclaringType!) + "." + method.Name +
        (method.IsGenericMethod ? "`" + method.GetGenericArguments().Length : "") + "(" +
        string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";

    internal JsonObject Export()
    {
        var exported = Assemblies.SelectMany(a => a.GetExportedTypes()).OrderBy(TypeName, StringComparer.Ordinal).ToArray();
        var messages = Records(exported, typeof(IMessage));
        var events = Records(exported, typeof(IEvent));
        var services = new JsonArray(ServiceTypes.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["namespace"] = t.Namespace,
            ["full_name"] = TypeName(t),
            ["registration"] = "provider_declared",
            ["methods"] = new JsonArray(Methods(t).Select(m => (JsonNode)Operation(m)).ToArray()),
        }).ToArray());
        var eventResults = new JsonArray(EventResultTypes.Select(type => (JsonNode)new JsonObject
        {
            ["name"] = type.Name, ["full_name"] = TypeName(type), ["body"] = Shape(type),
            ["bail_compatible"] = !type.IsValueType,
        }).ToArray());
        var sortedTypes = new JsonObject();
        foreach (var entry in _types.OrderBy(p => p.Key, StringComparer.Ordinal))
            sortedTypes.Add(entry.Key, entry.Value?.DeepClone());
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["runtime_protocol_versions"] = new JsonArray(1, 2),
            ["runtime_features"] = new JsonArray(RemoteProtocolFeatures.Names
                .Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
            ["assemblies"] = new JsonArray(Assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .Select(a => (JsonNode)new JsonObject { ["name"] = a.GetName().Name, ["version"] = a.GetName().Version?.ToString() }).ToArray()),
            ["messages"] = messages,
            ["events"] = events,
            ["event_result_types"] = eventResults,
            ["services"] = services,
            ["types"] = sortedTypes,
        };
    }

    private JsonArray Records(IEnumerable<Type> types, Type marker) => new(types
        .Where(t => !t.IsAbstract && marker.IsAssignableFrom(t))
        .Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name, ["namespace"] = t.Namespace, ["full_name"] = TypeName(t),
            ["body"] = Shape(t), ["unsupported_reason"] = Unsupported(t, []),
        }).ToArray());

    internal JsonObject Operation(MethodInfo method)
    {
        var result = Unwrap(method.ReturnType);
        var reason = method.ContainsGenericParameters ? "Open generic methods require a closed contract."
            : method.IsStatic ? "Static interface members are not instance service calls."
            : method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal) && !method.Name.StartsWith("set_", StringComparison.Ordinal)
                ? "Event accessors require an owned subscription bridge."
            : Unsupported(result, []);
        var parameters = new JsonArray();
        foreach (var parameter in method.GetParameters())
        {
            var injected = parameter.ParameterType == typeof(CancellationToken) ? "cancellation"
                : parameter.ParameterType == typeof(IPluginContext) ? "context" : null;
            if (injected is null && Unsupported(parameter.ParameterType, []) is { } failure)
                reason ??= $"Parameter '{parameter.Name}': {failure}";
            parameters.Add(new JsonObject
            {
                ["name"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!),
                ["clr_name"] = parameter.Name,
                ["injected"] = injected,
                ["optional"] = parameter.HasDefaultValue,
                ["default"] = parameter.HasDefaultValue && injected is null && !parameter.ParameterType.ContainsGenericParameters
                    ? JsonSerializer.SerializeToNode(parameter.DefaultValue, parameter.ParameterType, WireJson.Options) : null,
                ["type"] = injected is null ? Shape(parameter.ParameterType, _nullability.Create(parameter)) : null,
            });
        }
        var resultInfo = _nullability.Create(method.ReturnParameter);
        if (result != method.ReturnType && resultInfo.GenericTypeArguments.Length == 1)
            resultInfo = resultInfo.GenericTypeArguments[0];
        var providerReason = reason ?? (method.GetParameters().Any(parameter => parameter.ParameterType == typeof(IPluginContext))
            ? "Caller contexts require an invocation-scoped context adapter." : null);
        return new JsonObject
        {
            ["id"] = MethodId(method),
            ["name"] = method.Name,
            ["declaring_type"] = TypeName(method.DeclaringType!),
            ["execution"] = result != method.ReturnType ? "async" : "sync",
            ["rpc_shape"] = reason is null ? "supported" : "unsupported",
            ["unsupported_reason"] = reason,
            ["provider_rpc_shape"] = providerReason is null ? "supported" : "unsupported",
            ["provider_unsupported_reason"] = providerReason,
            ["parameters"] = parameters,
            ["result"] = Shape(result, resultInfo),
        };
    }

    internal static Type Unwrap(Type type) => type == typeof(Task) || type == typeof(ValueTask) ? typeof(void)
        : type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GenericTypeArguments[0] : type;

    internal string? Unsupported(Type type, HashSet<Type> path)
    {
        if (type.ContainsGenericParameters) return "Open generic types require a closed contract.";
        if (type.IsByRef || type.IsByRefLike || type.IsPointer || type.IsFunctionPointer)
            return "Managed references, pointers and stack-only values cannot cross the SDK wire.";
        if (typeof(Delegate).IsAssignableFrom(type)) return "Delegates require an owned callback bridge.";
        if (type == typeof(ISession) || Scalar(type) is not null || type.IsEnum) return null;
        if (Nullable.GetUnderlyingType(type) is { } nullable) return Unsupported(nullable, path);
        if (!path.Add(type)) return null;
        try
        {
            if (DictionaryTypes(type) is { } dictionary)
            {
                if (dictionary[0] != typeof(string) && !IsInteger(dictionary[0]) && !dictionary[0].IsEnum)
                    return "Dictionary keys must be strings, integers or enums.";
                return Unsupported(dictionary[1], path);
            }
            if (ElementType(type) is { } element) return Unsupported(element, path);
            if (!IsRecordType(type)) return $"Type '{TypeName(type)}' requires a dedicated SDK representation.";
            foreach (var property in Properties(type))
                if (Unsupported(property.PropertyType, path) is { } reason)
                    return $"Member '{property.Name}': {reason}";
            return null;
        }
        finally { path.Remove(type); }
    }

    private JsonObject Shape(Type type, NullabilityInfo? info = null)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        var shape = NonNullableShape(nullable ?? type, info);
        if (nullable is not null || (!type.IsValueType && info?.ReadState == NullabilityState.Nullable))
            return new JsonObject { ["any_of"] = new JsonArray(shape, new JsonObject { ["type"] = "null" }) };
        return shape;
    }

    private JsonObject NonNullableShape(Type type, NullabilityInfo? info)
    {
        if (Scalar(type) is { } scalar) return scalar;
        if (type == typeof(ISession)) return new JsonObject { ["type"] = "string", ["format"] = "session_handle" };
        if (type.ContainsGenericParameters || type.IsByRef || type.IsByRefLike || type.IsPointer || typeof(Delegate).IsAssignableFrom(type))
            return Unrepresented(type);
        if (DictionaryTypes(type) is { } dictionary)
            return new JsonObject
            {
                ["type"] = "object", ["key"] = Shape(dictionary[0]),
                ["values"] = Shape(dictionary[1], info?.GenericTypeArguments.ElementAtOrDefault(1)),
            };
        if (ElementType(type) is { } element)
            return new JsonObject
            {
                ["type"] = "array", ["items"] = Shape(element, info?.ElementType ?? info?.GenericTypeArguments.FirstOrDefault()),
            };
        if (!type.IsEnum && !IsRecordType(type)) return Unrepresented(type);
        var name = TypeName(type);
        if (!_types.ContainsKey(name))
        {
            _types.Add(name, null);
            if (type.IsEnum)
                _types[name] = new JsonObject
                {
                    ["kind"] = "enum", ["flags"] = type.IsDefined(typeof(FlagsAttribute)),
                    ["values"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                };
            else
                _types[name] = new JsonObject
                {
                    ["kind"] = "record",
                    ["fields"] = new JsonArray(Properties(type).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name,
                        ["required"] = p.IsRequired || RequiredConstructorParameter(type, p),
                        ["type"] = Shape(p.PropertyType, p.AttributeProvider is PropertyInfo property ? _nullability.Create(property) : null),
                    }).ToArray()),
                };
        }
        return new JsonObject { ["ref"] = name };
    }

    private JsonObject Unrepresented(Type type) => new()
    {
        ["type"] = "unsupported", ["clr_type"] = TypeName(type),
        ["reason"] = Unsupported(type, []) ?? "This shape requires a dedicated SDK representation.",
    };

    private static IEnumerable<JsonPropertyInfo> Properties(Type type) => WireJson.Options.GetTypeInfo(type).Properties
        .Where(p => p.Get is not null || p.Set is not null);

    private static bool RequiredConstructorParameter(Type type, JsonPropertyInfo property)
    {
        var constructors = type.GetConstructors();
        var constructor = constructors.FirstOrDefault(c => c.IsDefined(typeof(System.Text.Json.Serialization.JsonConstructorAttribute)))
            ?? (constructors.Length == 1 ? constructors[0] : null);
        var clrName = (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;
        return constructor?.GetParameters().Any(p => !p.HasDefaultValue &&
            string.Equals(p.Name, clrName, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsRecordType(Type type) => !type.IsInterface && !type.IsAbstract && Assemblies.Contains(type.Assembly);

    internal static Type[]? DictionaryTypes(Type type) => type.GetInterfaces().Prepend(type)
        .FirstOrDefault(t => t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
            t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))?.GenericTypeArguments;

    internal static Type? ElementType(Type type) => type.IsArray ? type.GetElementType() : type.GetInterfaces().Prepend(type)
        .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GenericTypeArguments[0];

    private static bool IsInteger(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
        type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static JsonObject? Scalar(Type type)
    {
        if (type == typeof(void)) return new JsonObject { ["type"] = "null" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (IsInteger(type)) return new JsonObject
        {
            ["type"] = "integer", ["format"] = type == typeof(long) ? "int64" : type == typeof(ulong) ? "uint64"
                : type == typeof(int) ? "int32" : type == typeof(uint) ? "uint32" : type == typeof(short) ? "int16"
                : type == typeof(ushort) ? "uint16" : type == typeof(byte) ? "uint8" : "int8",
        };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonObject { ["type"] = "number", ["format"] = type == typeof(decimal) ? "decimal" : type == typeof(float) ? "float32" : "float64" };
        if (type == typeof(string) || type == typeof(char)) return new JsonObject { ["type"] = "string" };
        var format = type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>) ? "base64"
            : type == typeof(DateTime) ? "date_time_utc" : type == typeof(DateTimeOffset) ? "date_time_offset"
            : type == typeof(DateOnly) ? "date" : type == typeof(TimeOnly) ? "time" : type == typeof(TimeSpan) ? "duration"
            : type == typeof(Guid) ? "uuid" : null;
        return format is null ? null : new JsonObject { ["type"] = "string", ["format"] = format };
    }
}
