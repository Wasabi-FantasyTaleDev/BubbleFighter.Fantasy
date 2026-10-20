using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks declared event permissions and the shared callback budget without a running SDK process.</summary>
public sealed class EventRegistrationTests
{
    /// <summary>Concurrent providers and listeners use the same bounded reservation, including retained callback targets.</summary>
    [Fact]
    public void MixedRegistrationsCannotOversubscribeTheSharedCallbackBudget()
    {
        using var fixture = new Registration(2);
        var accepted = new ConcurrentBag<JsonElement>();
        var rejected = new ConcurrentBag<RpcException>();
        Parallel.For(0, 16, index =>
        {
            try { accepted.Add(index % 2 == 0 ? fixture.On("callback-" + index) : fixture.Provide("callback-" + index)); }
            catch (RpcException error) { rejected.Add(error); }
        });
        Assert.Equal(2, accepted.Count);
        Assert.Equal(14, rejected.Count);
        Assert.All(rejected, error => Assert.Equal(RpcCodes.Overloaded, error.Code));
        foreach (var handle in accepted) fixture.Activation.DisposeHandle(handle);
        Assert.Equal(RpcCodes.Overloaded, Assert.Throws<RpcException>(() => fixture.On("later")).Code);
    }

    /// <summary>Failed handle admission refunds the listener identity so the same callback can be registered later.</summary>
    [Fact]
    public void RejectedRegistrationDoesNotRetainTheCallbackIdentity()
    {
        using var fixture = new Registration(1);
        var collection = fixture.Activation.GetAllServices(JsonSerializer.SerializeToElement(new { service = "IFighterRepository" }));
        Assert.Equal(RpcCodes.Overloaded, Assert.Throws<RpcException>(() => fixture.On("retry")).Code);
        fixture.Activation.DisposeHandle(collection);
        fixture.Activation.DisposeHandle(fixture.On("retry"));
    }

    /// <summary>Permission checks include both mode and result type and reject malformed priority without leaking capacity.</summary>
    [Fact]
    public void UndeclaredModesAndMalformedPrioritiesCannotRegister()
    {
        using var fixture = new Registration(1);
        Assert.Equal(RpcCodes.TypeNotAllowed, Assert.Throws<RpcException>(() => fixture.Activation.OnEvent(JsonSerializer.SerializeToElement(
            new { type = "EquipmentSelectionApplying", mode = "async", callback = "denied" }))).Code);
        Assert.Equal(RpcCodes.InvalidParams, Assert.Throws<RpcException>(() => fixture.Activation.OnEvent(JsonSerializer.SerializeToElement(
            new { type = "EquipmentSelectionApplying", mode = "sync", callback = "invalid", priority = "10" }))).Code);
        fixture.Activation.DisposeHandle(fixture.On("valid"));
    }

    /// <summary>Event declarations are unavailable in v1 and duplicate operations cannot be silently merged.</summary>
    [Fact]
    public void DefinitionsRequireV2AndUniqueEventOperations()
    {
        var item = new { type = "EquipmentSelectionApplying", mode = "waterfall", result_type = "EquipmentSelectionDecision" };
        var one = JsonSerializer.SerializeToElement(new { id = "example.events", version = "1.0.0", subscribes = new[] { item } });
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(one, "test", 1));
        Assert.Single(RemoteDefinition.Parse(one, "test", 2).Subscribes);
        var duplicate = JsonSerializer.SerializeToElement(new { id = "example.events", version = "1.0.0", subscribes = new[] { item, item } });
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(duplicate, "test", 2));
    }

    private sealed class Registration : IDisposable
    {
        private PluginContext Context { get; }
        internal RemoteActivation Activation { get; }

        internal Registration(int capacity)
        {
            var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
            {
                id = "test.events", version = "1.0.0", provides_many = new[] { nameof(IEquipmentReplyPolicy) },
                optional_many = new[] { nameof(IFighterRepository) },
                subscribes = new[] { new { type = "EquipmentSelectionApplying", mode = "sync" } },
            }), "test", 2);
            Context = new(new PluginRow { Id = "registration", Plugin = "test.events", Source = "test" },
                new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
            Activation = new(null!, definition, Context, 8, capacity, TimeSpan.FromSeconds(10));
            Activation.GoLive();
        }

        internal JsonElement On(string callback) => Activation.OnEvent(JsonSerializer.SerializeToElement(new
            { type = "EquipmentSelectionApplying", mode = "sync", callback }));

        internal JsonElement Provide(string callback) => Activation.Provide(JsonSerializer.SerializeToElement(new
            { service = "IEquipmentReplyPolicy", mode = "many", callback, methods = new[] { "get_ReplyAfterInventory" } }));

        public void Dispose() => Context.Unwind();
    }
}
