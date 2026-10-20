using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Exercises registration rollback and concurrent resource admission without invoking remote callbacks.</summary>
public sealed class ServiceProviderRegistrationTests
{
    /// <summary>Concurrent registrations cannot exceed the activation's retained callback or live handle budget.</summary>
    [Fact]
    public void ConcurrentProvidersCannotOversubscribeTheirActivation()
    {
        using var fixture = new Registration(2);
        var accepted = new ConcurrentBag<JsonElement>();
        var rejected = new ConcurrentBag<RpcException>();
        Parallel.For(0, 16, index =>
        {
            try { accepted.Add(fixture.Provide("callback-" + index)); }
            catch (RpcException error) { rejected.Add(error); }
        });
        Assert.Equal(2, accepted.Count);
        Assert.Equal(14, rejected.Count);
        Assert.All(rejected, error => Assert.Equal(RpcCodes.Overloaded, error.Code));
        Assert.Equal(2, fixture.Registry.GetAll<IEquipmentReplyPolicy>().Count);
        foreach (var handle in accepted) fixture.Release(handle);
        Assert.Empty(fixture.Registry.GetAll<IEquipmentReplyPolicy>());
        // Existing native projections can still own both callback targets until activation teardown.
        Assert.Equal(RpcCodes.Overloaded, Assert.Throws<RpcException>(() => fixture.Provide("another")).Code);
    }

    /// <summary>A collection consumes the same handle capacity, and rejected provider admission leaves no callback id reserved.</summary>
    [Fact]
    public void FailedAdmissionCanReuseItsCallbackAfterTheCollectionIsReleased()
    {
        using var fixture = new Registration(1);
        var collection = fixture.Activation.GetAllServices(JsonSerializer.SerializeToElement(new { service = "IFighterRepository" }));
        Assert.Equal(RpcCodes.Overloaded, Assert.Throws<RpcException>(() => fixture.Provide("retry")).Code);
        Assert.Empty(fixture.Registry.GetAll<IEquipmentReplyPolicy>());
        fixture.Release(collection);
        fixture.Provide("retry");
        Assert.Single(fixture.Registry.GetAll<IEquipmentReplyPolicy>());
    }

    /// <summary>A native registry conflict rolls back both callback reservation and handle capacity before a later retry.</summary>
    [Fact]
    public void RegistryFailureDoesNotConsumeProviderCapacity()
    {
        using var fixture = new Registration(1);
        using var conflicting = fixture.Registry.Register<IEquipmentReplyPolicy>(new Policy());
        Assert.Throws<InvalidOperationException>(() => fixture.Provide("retry"));
        conflicting.Dispose();
        fixture.Provide("retry");
        Assert.Single(fixture.Registry.GetAll<IEquipmentReplyPolicy>());
    }

    private sealed class Registration : IDisposable
    {
        internal ServiceRegistry Registry { get; } = new();
        private PluginContext Context { get; }
        internal RemoteActivation Activation { get; }

        internal Registration(int capacity)
        {
            var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
            {
                id = "test.registration", version = "1.0.0",
                provides_many = new[] { nameof(IEquipmentReplyPolicy) },
                optional_many = new[] { nameof(IFighterRepository) },
            }), "test", 2);
            Context = new(new PluginRow { Id = "registration", Plugin = "test.registration", Source = "test" },
                Registry, new EventBus(NullLogger.Instance), NullLogger.Instance);
            Activation = new(null!, definition, Context, 8, capacity, TimeSpan.FromSeconds(10));
            Activation.GoLive();
        }

        internal JsonElement Provide(string callback) => Activation.Provide(JsonSerializer.SerializeToElement(new
        {
            service = nameof(IEquipmentReplyPolicy), mode = "many", callback,
            methods = new[] { "get_ReplyAfterInventory" },
        }));

        internal void Release(JsonElement handle) => Activation.DisposeHandle(handle);
        public void Dispose() => Context.Unwind();
    }

    private sealed class Policy : IEquipmentReplyPolicy { public bool ReplyAfterInventory => false; }
}
