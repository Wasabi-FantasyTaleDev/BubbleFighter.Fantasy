using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks collection identity, declaration boundaries, disposal and budget reuse through the activation RPC adapter.</summary>
public sealed class ServiceCollectionTests
{
    /// <summary>Selections retain provider identity and order, reject foreign use, and release their capacity on disposal.</summary>
    [Fact]
    public async Task CollectionIdentityAndLifetimeAreActivationScoped()
    {
        using var first = new Selection();
        using var second = new Selection();
        using var low = first.Registry.RegisterMany<IEquipmentReplyPolicy>(new Policy(true));
        using var high = first.Registry.RegisterMany<IEquipmentReplyPolicy>(new Policy(false), 10);
        var selected = first.GetAll();
        Assert.Equal(2, selected.GetProperty("count").GetInt32());
        Assert.False((await first.Call(selected, 0)).GetBoolean());
        Assert.True((await first.Call(selected, 1)).GetBoolean());
        high.Dispose();
        Assert.False((await first.Call(selected, 0)).GetBoolean());
        var foreign = await Assert.ThrowsAsync<RpcException>(() => second.Call(selected, 0));
        Assert.Equal(RpcCodes.InvalidParams, foreign.Code);
        first.Release(selected);
        var expired = await Assert.ThrowsAsync<RpcException>(() => first.Call(selected, 0));
        Assert.Equal(RpcCodes.InvalidParams, expired.Code);
        var fresh = first.GetAll();
        Assert.Equal(1, fresh.GetProperty("count").GetInt32());
        Assert.True((await first.Call(fresh, 0)).GetBoolean());
    }

    /// <summary>Capacity is reserved before acquiring a selection and becomes available only after releasing it.</summary>
    [Fact]
    public void CollectionHandlesShareTheActivationBudget()
    {
        using var selection = new Selection();
        var first = selection.GetAll();
        Assert.Equal(0, first.GetProperty("count").GetInt32());
        Assert.Equal(RpcCodes.Overloaded, Assert.Throws<RpcException>(() => selection.GetAll()).Code);
        selection.Release(first);
        Assert.NotEqual(first.GetProperty("collection").GetString(), selection.GetAll().GetProperty("collection").GetString());
    }

    /// <summary>Malformed indices and attempts to repurpose a collection cannot reach the provider.</summary>
    [Theory]
    [InlineData(-1, "IEquipmentReplyPolicy", RpcCodes.InvalidParams)]
    [InlineData(1, "IEquipmentReplyPolicy", RpcCodes.InvalidParams)]
    [InlineData(0, "IFighterRepository", RpcCodes.TypeNotAllowed)]
    public async Task CollectionMembersCannotEscapeTheirDeclaredInterface(int index, string service, int code)
    {
        using var selection = new Selection();
        using var registration = selection.Registry.RegisterMany<IEquipmentReplyPolicy>(new Policy(true));
        var error = await Assert.ThrowsAsync<RpcException>(() => selection.Call(selection.GetAll(), index, service));
        Assert.Equal(code, error.Code);
    }

    /// <summary>Singleton reads and undeclared collection interfaces cannot silently choose a many provider.</summary>
    [Fact]
    public async Task ManyDeclarationsDoNotGrantSingletonAccess()
    {
        using var selection = new Selection();
        Assert.Equal(RpcCodes.TypeNotAllowed, Assert.Throws<RpcException>(() => selection.Activation.GetAllServices(
            JsonSerializer.SerializeToElement(new { service = "IFighterRepository" }))).Code);
        var error = await Assert.ThrowsAsync<RpcException>(() => selection.Activation.CallServiceAsync(
            JsonSerializer.SerializeToElement(new { service = "IEquipmentReplyPolicy", method = "get_ReplyAfterInventory" }), default));
        Assert.Equal(RpcCodes.TypeNotAllowed, error.Code);
    }

    /// <summary>Cardinality declarations are protocol-v2-only and reject conflicting singleton access.</summary>
    [Theory]
    [InlineData("requires_many")]
    [InlineData("optional_many")]
    public void ManyDeclarationsPreserveNativeMetadata(string field)
    {
        var values = new Dictionary<string, object> { ["id"] = "test.many", ["version"] = "1.0.0", [field] = new[] { "IEquipmentReplyPolicy" } };
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(JsonSerializer.SerializeToElement(values), "test", 1));
        var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(values), "test", 2);
        Assert.Contains(typeof(IEquipmentReplyPolicy), field == "requires_many" ? definition.Declaration.RequiresMany : definition.Declaration.OptionalMany);
        values["requires"] = new[] { "IEquipmentReplyPolicy" };
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(JsonSerializer.SerializeToElement(values), "test", 2));
    }

    private sealed class Selection : IDisposable
    {
        internal ServiceRegistry Registry { get; } = new();
        private PluginContext Context { get; }
        internal RemoteActivation Activation { get; }

        internal Selection()
        {
            var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
                { id = "test.many", version = "1.0.0", optional_many = new[] { "IEquipmentReplyPolicy" } }), "test", 2);
            Context = new(new PluginRow { Id = "many", Plugin = "test.many", Source = "test" }, Registry, new EventBus(NullLogger.Instance), NullLogger.Instance);
            Activation = new(null!, definition, Context, 8, 1, TimeSpan.FromSeconds(10));
            Activation.GoLive();
        }

        internal JsonElement GetAll() => Activation.GetAllServices(JsonSerializer.SerializeToElement(new { service = "IEquipmentReplyPolicy" }));
        internal void Release(JsonElement selected) => Activation.DisposeHandle(JsonSerializer.SerializeToElement(new { handle = selected.GetProperty("handle").GetString() }));
        internal Task<JsonElement> Call(JsonElement selected, int index, string service = "IEquipmentReplyPolicy") => Activation.CallServiceAsync(
            JsonSerializer.SerializeToElement(new { service, method = "get_ReplyAfterInventory", collection = selected.GetProperty("collection").GetString(), index }), default);
        public void Dispose() => Context.Unwind();
    }

    private sealed class Policy(bool value) : IEquipmentReplyPolicy
    {
        public bool ReplyAfterInventory => value;
    }
}
