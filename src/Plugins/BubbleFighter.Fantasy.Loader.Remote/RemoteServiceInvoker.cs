using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Invokes a declared host service from a protocol v2 <c>services.call</c> request.</summary>
internal static class RemoteServiceInvoker
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyDictionary<string, MethodInfo[]>> Methods = new();

    /// <summary>Resolves, binds and invokes one service method under the activation lifetime and request bound.</summary>
    /// <param name="definition">The remote definition that declared the service.</param>
    /// <param name="context">The activation context used to resolve the service.</param>
    /// <param name="sessions">The activation's opaque session map.</param>
    /// <param name="parameters">The JSON-RPC parameters.</param>
    /// <param name="requestTimeout">The configured upper bound for one remote request.</param>
    /// <param name="requestCancellation">The transport cancellation token.</param>
    /// <param name="selection">An activation-validated collection member, or null for a singleton read.</param>
    /// <returns>The JSON result, with <see langword="null"/> for a void method.</returns>
    public static async Task<JsonElement> InvokeAsync(RemoteDefinition definition, IPluginContext context, SessionMap sessions,
        JsonElement parameters, TimeSpan requestTimeout, CancellationToken requestCancellation,
        (Type Type, object Instance)? selection = null)
    {
        var request = RequireObject(parameters);
        var serviceName = RequiredString(request, "service");
        var methodName = RequiredString(request, "method");
        if (!request.TryGetProperty("args", out var args))
            args = JsonSerializer.SerializeToElement(new { });
        if (args.ValueKind != JsonValueKind.Object)
            throw new RpcException(RpcCodes.InvalidParams, "'args' must be an object");

        var serviceType = selection?.Type;
        if (selection is null && !definition.RequiresServices.TryGetValue(serviceName, out serviceType) &&
            !definition.OptionalServices.TryGetValue(serviceName, out serviceType!))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"service '{serviceName}' is not in the definition's requires or optional list");

        object service;
        try
        {
            service = selection?.Instance ?? GetService(context.Services, serviceType!);
        }
        catch (ServiceUnavailableException)
        {
            throw new RpcException(RpcCodes.ServiceUnavailable, $"service '{serviceName}' is not available",
                JsonSerializer.SerializeToElement(new { service = serviceName, required = definition.RequiresServices.ContainsKey(serviceName) }));
        }

        var candidates = MethodsFor(serviceType!).TryGetValue(methodName, out var named) ? named : [];
        if (candidates.Length == 0)
            throw new RpcException(RpcCodes.TypeNotAllowed, $"method '{methodName}' is not callable on service '{serviceName}'");
        var bindings = candidates.Where(method => ParameterNamesMatch(method, args)).ToArray();
        if (bindings.Length == 0)
            throw new RpcException(RpcCodes.InvalidParams, $"arguments do not match method '{serviceName}.{methodName}'");
        if (bindings.Length != 1)
            throw new RpcException(RpcCodes.InvalidParams, $"method '{serviceName}.{methodName}' is ambiguous for these arguments");

        var methodInfo = bindings[0];
        using var timeout = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, context.Lifetime, timeout.Token);
        object?[] bound;
        try
        {
            bound = BindArguments(methodInfo, args, sessions, context, linked.Token);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException or FormatException or OverflowException)
        {
            throw new RpcException(RpcCodes.InvalidParams, $"arguments do not bind to '{serviceName}.{methodName}': {ex.Message}");
        }

        object? value;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            value = methodInfo.Invoke(service, bound);
            value = await AwaitResultAsync(value, methodInfo.ReturnType).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !requestCancellation.IsCancellationRequested && !context.Lifetime.IsCancellationRequested)
        {
            throw new RemoteTimeoutException($"service '{serviceName}.{methodName}' exceeded request_timeout_ms");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException && timeout.IsCancellationRequested &&
            !requestCancellation.IsCancellationRequested && !context.Lifetime.IsCancellationRequested)
        {
            throw new RemoteTimeoutException($"service '{serviceName}.{methodName}' exceeded request_timeout_ms");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ServiceFailure(serviceName, methodName, ex.InnerException);
        }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested && !context.Lifetime.IsCancellationRequested)
        {
            throw new RpcException(RpcCodes.InternalError, $"service '{serviceName}.{methodName}' was cancelled");
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw ServiceFailure(serviceName, methodName, ex);
        }

        if (methodInfo.ReturnType == typeof(void) || methodInfo.ReturnType == typeof(Task) || methodInfo.ReturnType == typeof(ValueTask))
            return JsonSerializer.SerializeToElement((object?)null);
        try
        {
            var resultType = ContractSchema.Unwrap(methodInfo.ReturnType);
            var nullability = new NullabilityInfoContext().Create(methodInfo.ReturnParameter);
            if (resultType != methodInfo.ReturnType && nullability.GenericTypeArguments.Length == 1)
                nullability = nullability.GenericTypeArguments[0];
            if (value is null && nullability.ReadState == NullabilityState.NotNull)
                throw new InvalidOperationException("The provider returned null for a non-nullable result.");
            var result = JsonSerializer.SerializeToElement(value, resultType, SessionOptions(sessions));
            ValidateNullability(result, resultType, nullability);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw ServiceFailure(serviceName, methodName, ex);
        }
    }

    private static IReadOnlyDictionary<string, MethodInfo[]> MethodsFor(Type serviceType) => Methods.GetOrAdd(serviceType, static type =>
        ContractSchema.Methods(type).Select(method => (method, operation: new ContractSchema().Operation(method)))
            .Where(pair => pair.operation["rpc_shape"]?.GetValue<string>() == "supported")
            .SelectMany(pair => new[] { (key: pair.method.Name, pair.method), (key: ContractSchema.MethodId(pair.method), pair.method) })
            .GroupBy(pair => pair.key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.method).ToArray(), StringComparer.Ordinal));

    internal static bool ParameterNamesMatch(MethodInfo method, JsonElement args)
    {
        var expected = method.GetParameters().Where(parameter => !Injected(parameter)).Select(parameter =>
            JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!)).ToHashSet(StringComparer.Ordinal);
        var supplied = args.EnumerateObject().Select(p => p.Name).ToArray();
        return supplied.Distinct(StringComparer.Ordinal).Count() == supplied.Length && supplied.All(expected.Contains) &&
            method.GetParameters().Where(parameter => !Injected(parameter) && !parameter.HasDefaultValue)
                .All(parameter => args.TryGetProperty(JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!), out _));
    }

    internal static object?[] BindArguments(MethodInfo method, JsonElement args, SessionMap sessions, IPluginContext context, CancellationToken cancellation)
    {
        var result = new object?[method.GetParameters().Length];
        foreach (var (parameter, index) in method.GetParameters().Select((parameter, index) => (parameter, index)))
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                result[index] = cancellation;
                continue;
            }
            if (parameter.ParameterType == typeof(IPluginContext))
            {
                result[index] = context;
                continue;
            }
            var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!);
            if (!args.TryGetProperty(name, out var value))
            {
                result[index] = parameter.DefaultValue is Missing or DBNull ? Type.Missing : parameter.DefaultValue;
                continue;
            }
            ValidateNullability(value, parameter.ParameterType, new NullabilityInfoContext().Create(parameter));
            result[index] = value.Deserialize(parameter.ParameterType, SessionOptions(sessions));
        }
        return result;
    }

    private static bool Injected(ParameterInfo parameter) => parameter.ParameterType == typeof(CancellationToken) || parameter.ParameterType == typeof(IPluginContext);

    // The serializer checks record members but does not enforce non-null collection elements.
    internal static void ValidateNullability(JsonElement value, Type type, NullabilityInfo? info, bool unknownIsNonNull = false)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (Nullable.GetUnderlyingType(type) is null && (type.IsValueType || info?.ReadState == NullabilityState.NotNull || (info is null && unknownIsNonNull)))
                throw new JsonException("A non-nullable service value must not be null.");
            return;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (value.ValueKind == JsonValueKind.Object && ContractSchema.DictionaryTypes(type) is { } dictionary)
        {
            foreach (var property in value.EnumerateObject())
                ValidateNullability(property.Value, dictionary[1], info?.GenericTypeArguments.ElementAtOrDefault(1), unknownIsNonNull);
        }
        else if (value.ValueKind == JsonValueKind.Array && ContractSchema.ElementType(type) is { } element)
        {
            foreach (var item in value.EnumerateArray())
                ValidateNullability(item, element, info?.ElementType ?? info?.GenericTypeArguments.FirstOrDefault(), unknownIsNonNull);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in WireJson.Options.GetTypeInfo(type).Properties)
                if (value.TryGetProperty(property.Name, out var member))
                    ValidateNullability(member, property.PropertyType,
                        property.AttributeProvider is PropertyInfo reflected ? new NullabilityInfoContext().Create(reflected) : null, unknownIsNonNull);
        }
    }

    private static object GetService(IServiceRegistry services, Type type)
    {
        var get = typeof(IServiceRegistry).GetMethod(nameof(IServiceRegistry.Get))!.MakeGenericMethod(type);
        try { return get.Invoke(services, null)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    internal static async Task<object?> AwaitResultAsync(object? value, Type returnType)
    {
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(Task))
        {
            await ((Task)value!).ConfigureAwait(false);
            return null;
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var task = (Task)value!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        if (returnType == typeof(ValueTask))
        {
            await ((ValueTask)value!).ConfigureAwait(false);
            return null;
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = returnType.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(value, null)!;
            var task = (Task)asTask;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        return value;
    }

    private static RpcException ServiceFailure(string service, string method, Exception exception) =>
        new(RpcCodes.ServiceError, $"service '{service}.{method}' failed",
            JsonSerializer.SerializeToElement(new { exception = exception.GetType().FullName ?? exception.GetType().Name }));

    private static JsonElement RequireObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value : throw new RpcException(RpcCodes.InvalidParams, "params must be an object");

    private static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()! : throw new RpcException(RpcCodes.InvalidParams, $"'{name}' must be a nonempty string");

    internal static JsonSerializerOptions SessionOptions(SessionMap sessions)
    {
        var options = new JsonSerializerOptions(WireJson.Options) { AllowDuplicateProperties = false };
        options.Converters.Insert(0, new SessionHandleConverter(sessions));
        return options;
    }

    private sealed class SessionHandleConverter(SessionMap sessions) : JsonConverter<ISession>
    {
        public override ISession Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !sessions.TryGet(reader.GetString()!, out var session))
                throw new RpcException(RpcCodes.SessionUnknown, "session handle is unknown or closed");
            return session;
        }

        public override void Write(Utf8JsonWriter writer, ISession value, JsonSerializerOptions options)
        {
            var id = sessions.Add(value);
            writer.WriteStringValue(id);
        }
    }
}
