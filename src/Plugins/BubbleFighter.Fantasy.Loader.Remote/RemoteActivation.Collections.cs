using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

internal sealed partial class RemoteActivation
{
    private readonly ConcurrentDictionary<string, ServiceCollection> _serviceCollections = new(StringComparer.Ordinal);

    /// <summary>Selects the activation's ordered service projection and owns its opaque collection until release.</summary>
    public JsonElement GetAllServices(JsonElement parameters)
    {
        var service = RequiredString(parameters, "service");
        if (!Definition.RequiresManyServices.TryGetValue(service, out var type) &&
            !Definition.OptionalManyServices.TryGetValue(service, out type))
            throw new RpcException(RpcCodes.TypeNotAllowed, $"service '{service}' is not declared in requires_many or optional_many");
        var collection = Guid.NewGuid().ToString("N");
        var count = 0;
        var handle = AddHandle(ownedHandle =>
        {
            object[] values;
            try
            {
                var getAll = typeof(IServiceRegistry).GetMethod(nameof(IServiceRegistry.GetAll))!.MakeGenericMethod(type);
                // Hosted GetAll preserves the selected provider identities and their native priority/tie order.
                values = ((IEnumerable)getAll.Invoke(Context.Services, null)!).Cast<object>().ToArray();
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            count = values.Length;
            _serviceCollections[collection] = new ServiceCollection(type, values);
            try { return Context.Effect(() => { _serviceCollections.TryRemove(collection, out _); }); }
            catch { _serviceCollections.TryRemove(collection, out _); throw; }
        });
        return JsonSerializer.SerializeToElement(new { handle = handle.GetProperty("handle").GetString(), collection, count });
    }

    private (Type Type, object Instance)? SelectCollectionMember(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("collection", out var token))
        {
            if (parameters.TryGetProperty("index", out _))
                throw new RpcException(RpcCodes.InvalidParams, "'index' requires a service collection");
            return null;
        }
        if (token.ValueKind != JsonValueKind.String || !_serviceCollections.TryGetValue(token.GetString()!, out var selection))
            throw new RpcException(RpcCodes.InvalidParams, "the service collection is unknown, foreign or disposed");
        if (RequiredString(parameters, "service") != selection.Type.Name)
            throw new RpcException(RpcCodes.TypeNotAllowed, "the collection belongs to a different service interface");
        if (!parameters.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
            !index.TryGetInt32(out var ordinal) || ordinal < 0 || ordinal >= selection.Values.Length)
            throw new RpcException(RpcCodes.InvalidParams, "the service collection index is out of range");
        return (selection.Type, selection.Values[ordinal]);
    }

    private sealed record ServiceCollection(Type Type, object[] Values);
}
