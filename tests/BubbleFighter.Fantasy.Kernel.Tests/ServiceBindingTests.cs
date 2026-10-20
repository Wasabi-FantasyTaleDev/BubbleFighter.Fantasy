using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ServiceBindingTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Flag(bool Enabled) : IFlag;
    private static CommittedServiceView View(ServiceRuntime runtime, ServiceBinding binding) =>
        new DeclaredServiceView(runtime.RootScope, [new(typeof(IValue), ServiceCardinality.Single, true)])
            .Commit(runtime.NewActivation("consumer"), new ServiceBindingSnapshot(runtime, [binding]));

    [Fact]
    public void DecorationOwnersOrderAndProjectionRevisionParticipateInDependencyIdentity()
    {
        var runtime = new ServiceRuntime(8);
        var provider = runtime.NewActivation("provider");
        var firstOwner = runtime.NewActivation("first-layer");
        var secondOwner = runtime.NewActivation("second-layer");
        var basis = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var first = ServiceDecoration.Create(runtime.RootScope, firstOwner, typeof(IValue), 10);
        var second = ServiceDecoration.Create(runtime.RootScope, secondOwner, typeof(IValue), 20);
        var layers = new[] { first, second };
        var projection = basis.WithDecorations(new Value(3), layers);
        var original = View(runtime, projection);
        Assert.Equal(new[] { second, first }, projection.Decorations);
        Assert.Equal(3, original.Providers.Count);
        Assert.Contains(provider, original.Providers);
        Assert.Contains(firstOwner, original.Providers);
        Assert.Contains(secondOwner, original.Providers);
        layers[0] = second;
        Assert.Equal(first, projection.Decorations[1]);
        var next = basis.WithDecorations(new Value(2), [first]);
        Assert.Equal(projection.Registration, next.Registration);
        Assert.NotEqual(projection.Revision, next.Revision);
        Assert.False(original.Matches(new ServiceBindingSnapshot(runtime, [next])));
        Assert.Equal(3, original.Get<IValue>().Number);
        Assert.Equal(2, View(runtime, next).Get<IValue>().Number);
    }

    [Fact]
    public void RebuiltProjectionIsNotAssumedEqualEvenWhenItsLayerListAndValueAreEqual()
    {
        var runtime = new ServiceRuntime(4);
        var provider = runtime.NewActivation("provider");
        var basis = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var layer = ServiceDecoration.Create(runtime.RootScope, runtime.NewActivation("layer"), typeof(IValue));
        var value = new Value(2);
        var first = basis.WithDecorations(value, [layer]);
        var second = basis.WithDecorations(value, [layer]);
        Assert.False(View(runtime, first).Matches(new ServiceBindingSnapshot(runtime, [second])));
    }

    [Fact]
    public void EqualPriorityLayersRetainRegistrationOrder()
    {
        var runtime = new ServiceRuntime(4);
        var provider = runtime.NewActivation("provider");
        var basis = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var first = ServiceDecoration.Create(runtime.RootScope, provider, typeof(IValue));
        var second = ServiceDecoration.Create(runtime.RootScope, provider, typeof(IValue));
        Assert.Equal(new[] { first, second }, basis.WithDecorations(new Value(1), [second, first]).Decorations);
    }

    [Fact]
    public void ForeignRealmDuplicateAndMultipleServiceDecorationsAreRejected()
    {
        var runtime = new ServiceRuntime(8);
        var provider = runtime.NewActivation("provider");
        var basis = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var layer = ServiceDecoration.Create(runtime.RootScope, provider, typeof(IValue));
        var foreign = ServiceDecoration.Create(runtime.RootScope.CreateChild([typeof(IValue)]), provider, typeof(IValue));
        Assert.Throws<InvalidOperationException>(() => basis.WithDecorations(new Value(2), [layer, layer]));
        Assert.Throws<InvalidOperationException>(() => basis.WithDecorations(new Value(2), [foreign]));
        var multiple = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1), ServiceCardinality.Multiple);
        Assert.Throws<InvalidOperationException>(() => multiple.WithDecorations(new Value(2), [layer]));
    }

    [Fact]
    public void SnapshotCapacityIncludesDecorationContributions()
    {
        var runtime = new ServiceRuntime(2);
        var provider = runtime.NewActivation("provider");
        var basis = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var first = ServiceDecoration.Create(runtime.RootScope, provider, typeof(IValue));
        var second = ServiceDecoration.Create(runtime.RootScope, provider, typeof(IValue));
        var projection = basis.WithDecorations(new Value(2), [first]);
        Assert.Single(new ServiceBindingSnapshot(runtime, [projection]).Resolve(runtime.RootScope.Resolve(typeof(IValue))));
        Assert.Throws<InvalidOperationException>(() => basis.WithDecorations(new Value(3), [first, second]));
        var other = ServiceBinding.Create(runtime.RootScope, provider, typeof(IFlag), new Flag(true));
        Assert.Throws<InvalidOperationException>(() => new ServiceBindingSnapshot(runtime, [projection, other]));
    }

    [Fact]
    public void DuplicateRegistrationsAndMixedCardinalityCannotEnterASnapshot()
    {
        var runtime = new ServiceRuntime(8);
        var provider = runtime.NewActivation("provider");
        var single = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var another = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(2));
        var multiple = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(3), ServiceCardinality.Multiple);
        Assert.Throws<InvalidOperationException>(() => new ServiceBindingSnapshot(runtime, [single, single]));
        Assert.Throws<InvalidOperationException>(() => new ServiceBindingSnapshot(runtime, [single, another]));
        Assert.Throws<InvalidOperationException>(() => new ServiceBindingSnapshot(runtime, [single, multiple]));
    }

    [Fact]
    public void DeclarationCapacityDuplicatesAndPublishedModesAreValidated()
    {
        var runtime = new ServiceRuntime(1);
        var required = new ServiceRequirement(typeof(IValue), ServiceCardinality.Single, true);
        Assert.Throws<InvalidOperationException>(() => new DeclaredServiceView(runtime.RootScope, [required, required]));
        var other = new ServiceRuntime(4);
        Assert.Throws<InvalidOperationException>(() => new DeclaredServiceView(other.RootScope, [required, required]));
        var provider = other.NewActivation("provider");
        var multiple = ServiceBinding.Create(other.RootScope, provider, typeof(IValue), new Value(1), ServiceCardinality.Multiple);
        Assert.Throws<InvalidOperationException>(() => new DeclaredServiceView(other.RootScope, [required])
            .Commit(other.NewActivation("reader"), new ServiceBindingSnapshot(other, [multiple])));
    }

    [Fact]
    public void InvalidValuesAndCardinalityFailBeforeAViewIsPublished()
    {
        var runtime = new ServiceRuntime(4);
        var provider = runtime.NewActivation("provider");
        Assert.Throws<ArgumentException>(() => ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Flag(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1), (ServiceCardinality)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeclaredServiceView(runtime.RootScope, [new(typeof(IValue), (ServiceCardinality)99, true)]));
    }
}
