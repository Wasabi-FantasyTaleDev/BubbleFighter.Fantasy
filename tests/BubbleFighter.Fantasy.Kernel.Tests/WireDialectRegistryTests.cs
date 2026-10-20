using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class WireDialectRegistryTests
{
    [Fact]
    public void ExactBuildResolutionRejectsMissingResourcesAndDuplicateIdentity()
    {
        var registry = new WireDialectRegistry(4);
        var dialect = new Stub("kr-a", new("kr", 1256, 1304));
        using var registration = registry.Register(dialect);
        Assert.Same(dialect, registry.Get("kr-a"));
        Assert.Same(dialect, registry.Resolve(new("kr", 1256, 1304)));
        Assert.Throws<ConfigException>(() => registry.Resolve(new("kr", 1256, 1305)));
        Assert.Throws<ConfigException>(() => registry.Resolve(new("id", 1256, 1304)));
        Assert.Throws<ConfigException>(() => registry.Register(dialect));
        Assert.Throws<ConfigException>(() => registry.Register(new Stub("kr-b", new("kr", 1256, 1304))));
        Assert.Throws<ConfigException>(() => registry.Get("missing"));
    }

    [Fact]
    public void CapacitySnapshotAndRepeatedDisposePreserveReplacement()
    {
        var registry = new WireDialectRegistry(1);
        var dialect = new Stub("kr-a", new("kr", 1256, 1304));
        var first = registry.Register(dialect);
        var snapshot = registry.All;
        Assert.Throws<ConfigException>(() => registry.Register(new Stub("id-a", new("id", 1, 0))));
        first.Dispose();
        Assert.Empty(registry.All);
        using var second = registry.Register(dialect);
        first.Dispose();
        Assert.Same(dialect, registry.Get("kr-a"));
        Assert.Same(dialect, Assert.Single(snapshot));
    }

    [Fact]
    public async Task NoProviderDoesNotInjectLibraryDefaults()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new() { Id = "registry", Plugin = "core.wire-dialects", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            await new WireDialectRegistryPlugin().ActivateAsync(context, CancellationToken.None);
            Assert.Empty(services.Get<IWireDialectRegistry>().All);
            Assert.Throws<ConfigException>(() => services.Get<IWireDialectRegistry>().Get("kr-1256"));
        }
        finally { context.Unwind(); }
        Assert.False(services.TryGet<IWireDialectRegistry>(out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidRegistryCapacityFailsBeforePublication(int limit)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new()
        {
            Id = "registry", Plugin = "core.wire-dialects", Source = "test", Config = new Dictionary<string, object?> { ["max_dialects"] = limit },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        await Assert.ThrowsAsync<ConfigException>(() => new WireDialectRegistryPlugin().ActivateAsync(context, CancellationToken.None).AsTask());
        Assert.False(services.TryGet<IWireDialectRegistry>(out _));
        context.Unwind();
    }

    private sealed class Stub(string id, ClientBuild build) : IWireDialect
    {
        public string Id => id;
        public ClientProfile Profile { get; } = new(id, build.Region, build.Major, 0, build.Resource, "Test dialect");
        public string Description => "Test dialect";
        public int MaxFrameOverheadBytes => 8;
        public int PreambleLength(PacketDirection direction) => 0;
        public byte[] BuildServerPreamble(ushort status, string commandLine) => [];
        public IStreamTransform CreateDecryptor(PacketDirection direction) => throw new NotSupportedException();
        public IStreamTransform CreateEncryptor(PacketDirection direction) => throw new NotSupportedException();
        public bool TryReadFrame(ref ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame) => throw new NotSupportedException();
        public byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload) => throw new NotSupportedException();
    }
}
