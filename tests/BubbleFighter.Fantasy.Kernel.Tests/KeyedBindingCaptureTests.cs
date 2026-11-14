using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>
/// An activation captures only the keys it reads: each such key resolves exactly as in a full capture, and the
/// registrations under other keys are not captured at all.
/// </summary>
public sealed class KeyedBindingCaptureTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Flag(bool Enabled) : IFlag;

    [Fact]
    public void KeyedCaptureResolvesItsKeysLikeAFullCaptureAndNothingElse()
    {
        var registry = new ServiceRegistry(16);
        var root = registry.Runtime.RootScope;
        using var value = registry.Register<IValue>(new Value(7));
        using var flag = registry.Register<IFlag>(new Flag(true));
        var valueKey = root.Resolve(typeof(IValue));
        var flagKey = root.Resolve(typeof(IFlag));

        var full = registry.CaptureBindings();
        var keyed = registry.CaptureBindings(keys: new HashSet<ServiceKey> { valueKey });

        var expected = Assert.Single(full.Resolve(valueKey));
        var actual = Assert.Single(keyed.Resolve(valueKey));
        Assert.Same(expected.Registration, actual.Registration);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Single(full.Resolve(flagKey));
        Assert.Empty(keyed.Resolve(flagKey));
    }
}
