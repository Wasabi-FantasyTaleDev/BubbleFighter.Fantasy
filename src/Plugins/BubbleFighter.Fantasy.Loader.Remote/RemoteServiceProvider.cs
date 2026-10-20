using System.Reflection;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Validates complete provider interfaces and adapts their native return kinds to owned SDK callbacks.</summary>
internal sealed class RemoteServiceProvider
{
    private static readonly MethodInfo RegisterMethod = typeof(RemoteServiceProvider).GetMethod(nameof(RegisterTyped), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo TaskResultMethod = typeof(RemoteServiceProvider).GetMethod(nameof(TaskResult), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ValueTaskResultMethod = typeof(RemoteServiceProvider).GetMethod(nameof(ValueTaskResult), BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly RemoteActivation _activation;
    private readonly Dictionary<string, MethodInfo> _methods;

    internal RemoteServiceProvider(RemoteActivation activation, Type service, string callback, JsonElement implemented)
    {
        _activation = activation;
        Service = service;
        Callback = callback;
        Validate(service);
        var methods = ContractSchema.Methods(service);
        _methods = methods.ToDictionary(ContractSchema.MethodId, StringComparer.Ordinal);
        foreach (var group in methods.GroupBy(method => method.Name, StringComparer.Ordinal).Where(group => group.Count() == 1))
            _methods.Add(group.Key, group.Single());
        if (implemented.ValueKind != JsonValueKind.Array)
            throw new RpcException(RpcCodes.InvalidParams, "'methods' must list every implemented method name or full schema id");
        var supplied = new HashSet<MethodInfo>();
        foreach (var value in implemented.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !_methods.TryGetValue(value.GetString()!, out var method) || !supplied.Add(method))
                throw new RpcException(RpcCodes.InvalidParams, "'methods' contains an unknown, ambiguous or repeated method");
        }
        if (supplied.Count != methods.Length)
            throw new RpcException(RpcCodes.InvalidParams, $"service '{service.Name}' requires implementations of all {methods.Length} methods");
    }

    internal Type Service { get; }
    internal string Callback { get; }

    internal static void Validate(Type service)
    {
        foreach (var method in ContractSchema.Methods(service))
        {
            var operation = new ContractSchema().Operation(method);
            var reason = operation["provider_unsupported_reason"]?.GetValue<string>();
            if (reason is not null)
                throw new ConfigException($"SDK provider '{service.Name}' cannot implement '{ContractSchema.MethodId(method)}': {reason}");
        }
    }

    internal IDisposable Register(string mode, int priority) =>
        (IDisposable)RegisterMethod.MakeGenericMethod(Service).Invoke(this, [mode, priority])!;

    internal MethodInfo Method(string name) => _methods.TryGetValue(name, out var method) ? method
        : throw new RpcException(RpcCodes.InvalidParams, $"unknown or ambiguous method '{name}' on '{Service.Name}'");

    private IDisposable RegisterTyped<T>(string mode, int priority) where T : class => mode switch
    {
        "singleton" => _activation.Context.Services.Register(Proxy<T>(null)),
        "decoratable" => _activation.Context.Services.RegisterDecoratable(Proxy<T>(null)),
        "many" => _activation.Context.Services.RegisterMany(Proxy<T>(null), priority),
        // The registry builds the wrapper synchronously; construction performs no RPC or inner calls.
        "decorate" => _activation.Context.Services.Decorate<T>(inner => Proxy(inner), priority),
        _ => throw new RpcException(RpcCodes.InvalidParams, $"unknown service registration mode '{mode}'"),
    };

    private T Proxy<T>(T? inner) where T : class
    {
        var proxy = DispatchProxy.Create<T, Forwarder>();
        ((Forwarder)(object)proxy).Target = (method, arguments) =>
        {
            var pending = _activation.InvokeProviderAsync(this, method, arguments ?? [], inner);
            var returns = method.ReturnType;
            if (returns == typeof(Task)) return (Task)pending;
            if (returns == typeof(ValueTask)) return new ValueTask(pending);
            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
                return TaskResultMethod.MakeGenericMethod(returns.GenericTypeArguments[0]).Invoke(null, [pending]);
            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(ValueTask<>))
                return ValueTaskResultMethod.MakeGenericMethod(returns.GenericTypeArguments[0]).Invoke(null, [pending]);
            return pending.GetAwaiter().GetResult();
        };
        return proxy;
    }

    private static async Task<T> TaskResult<T>(Task<object?> pending) => (T)(await pending.ConfigureAwait(false))!;
    private static ValueTask<T> ValueTaskResult<T>(Task<object?> pending) => new(TaskResult<T>(pending));

    internal static JsonElement Arguments(MethodInfo method, object?[] arguments, SessionMap sessions)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var options = RemoteServiceInvoker.SessionOptions(sessions);
        foreach (var (parameter, index) in method.GetParameters().Select((parameter, index) => (parameter, index)))
        {
            if (parameter.ParameterType == typeof(CancellationToken)) continue;
            var value = JsonSerializer.SerializeToElement(arguments[index], parameter.ParameterType, options);
            RemoteServiceInvoker.ValidateNullability(value, parameter.ParameterType, new NullabilityInfoContext().Create(parameter));
            values.Add(JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!), value);
        }
        return JsonSerializer.SerializeToElement(values);
    }

    internal static object? Result(MethodInfo method, JsonElement value, SessionMap sessions)
    {
        var type = ContractSchema.Unwrap(method.ReturnType);
        if (type == typeof(void))
        {
            if (value.ValueKind != JsonValueKind.Null) throw new JsonException("A void service callback must return null.");
            return null;
        }
        var nullability = new NullabilityInfoContext().Create(method.ReturnParameter);
        if (type != method.ReturnType && nullability.GenericTypeArguments.Length == 1)
            nullability = nullability.GenericTypeArguments[0];
        RemoteServiceInvoker.ValidateNullability(value, type, nullability);
        return value.Deserialize(type, RemoteServiceInvoker.SessionOptions(sessions));
    }

    /// <summary>The non-sealed base from which DispatchProxy emits the declared service interface.</summary>
    public class Forwarder : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Target { get; set; } = null!;

        /// <summary>Routes an interface invocation while preserving its declared synchronous or asynchronous return type.</summary>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Target(targetMethod ?? throw new InvalidOperationException("The service proxy received no method."), args);
    }
}
