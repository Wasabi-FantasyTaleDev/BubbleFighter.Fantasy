using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks remote identity admission and cleanup independently of guest transports.</summary>
public sealed class ResourceAdmissionTests
{
    /// <summary>Parent disposal retires child identities and returns their bounded remote capacity.</summary>
    [Fact]
    public async Task ParentDisposalReturnsDescendantCapacity()
    {
        using var fixture = new Fixture(2);
        var root = await fixture.Activation.CreateResourceScopeAsync(Json(new { name = "root" }), default);
        var rootId = root.GetProperty("scope").GetString()!;
        var child = await fixture.Activation.CreateResourceScopeAsync(Json(new { name = "child", parent = rootId }), default);
        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.Activation.CreateResourceScopeAsync(Json(new { name = "excess" }), default));
        Assert.Equal(RpcCodes.Overloaded, error.Code);
        await fixture.Activation.DisposeResourceScopeAsync(root, default);
        error = await Assert.ThrowsAsync<RpcException>(() => fixture.Activation.DisposeResourceScopeAsync(child, default));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        var replacement = await fixture.Activation.CreateResourceScopeAsync(Json(new { name = "replacement" }), default);
        await fixture.Activation.DisposeResourceScopeAsync(replacement, default);
    }

    /// <summary>Malformed parents cannot silently acquire a root scope and cancellation precedes admission.</summary>
    [Fact]
    public async Task RejectedScopesDoNotConsumeCapacity()
    {
        using var fixture = new Fixture(1);
        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.Activation.CreateResourceScopeAsync(Json(new { name = "bad", parent = 42 }), default));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Activation.CreateResourceScopeAsync(Json(new { name = "canceled" }), canceled.Token));
        var root = await fixture.Activation.CreateResourceScopeAsync(Json(new { name = "valid" }), default);
        await fixture.Activation.DisposeResourceScopeAsync(root, default);
    }

    /// <summary>Invalid middleware priorities fail before registering or reserving a callback.</summary>
    [Theory]
    [InlineData("\"high\"")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("null")]
    public void MalformedMiddlewarePriorityIsRejected(string priority)
    {
        using var fixture = new Fixture(1);
        var request = JsonDocument.Parse("{\"protocol\":\"test/game\",\"type\":\"LoginRequested\",\"priority\":" + priority + "}");
        using (request)
            Assert.Equal(RpcCodes.InvalidParams, Assert.Throws<RpcException>(() => fixture.Activation.Use(request.RootElement)).Code);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private sealed class Fixture : IDisposable
    {
        private readonly PluginContext _context;
        internal RemoteActivation Activation { get; }

        internal Fixture(int capacity)
        {
            var definition = RemoteDefinition.Parse(Json(new { id = "test.resources", version = "1.0.0" }), "test", 2);
            _context = new(new PluginRow { Id = "resources", Plugin = "test.resources", Source = "test" },
                new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
            Activation = new(null!, definition, _context, 8, capacity, TimeSpan.FromSeconds(10));
            Activation.GoLive();
        }

        public void Dispose() => _context.Unwind();
    }
}
