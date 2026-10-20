using System.Collections.Concurrent;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ServiceRealmTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Flag(bool Enabled) : IFlag;
    private static ServiceRequirement Need<T>() => new(typeof(T), ServiceCardinality.Single, true);

    [Fact]
    public void IsolatedBranchesOverrideOnlySelectedContractsAndDescendantsInherit()
    {
        var runtime = new ServiceRuntime(8);
        var root = runtime.RootScope;
        var left = root.CreateChild([typeof(IValue)]);
        var right = root.CreateChild([typeof(IValue)]);
        var nested = left.CreateChild([typeof(IFlag)]);
        Assert.NotEqual(root.Resolve(typeof(IValue)), left.Resolve(typeof(IValue)));
        Assert.NotEqual(left.Resolve(typeof(IValue)), right.Resolve(typeof(IValue)));
        Assert.Equal(root.Resolve(typeof(IFlag)), left.Resolve(typeof(IFlag)));
        Assert.Equal(left.Resolve(typeof(IValue)), nested.Resolve(typeof(IValue)));
        Assert.NotEqual(root.Resolve(typeof(IFlag)), nested.Resolve(typeof(IFlag)));
        Assert.Equal(root.Resolve(typeof(IValue)), root.CreateChild([]).Resolve(typeof(IValue)));

        var provider = runtime.NewActivation("provider");
        var snapshot = new ServiceBindingSnapshot(runtime, [
            ServiceBinding.Create(root, provider, typeof(IValue), new Value(1)),
            ServiceBinding.Create(left, provider, typeof(IValue), new Value(2)),
            ServiceBinding.Create(right, provider, typeof(IValue), new Value(3)),
            ServiceBinding.Create(root, provider, typeof(IFlag), new Flag(false)),
            ServiceBinding.Create(nested, provider, typeof(IFlag), new Flag(true))]);
        CommittedServiceView View(ServiceScope scope) => new DeclaredServiceView(scope, [Need<IValue>(), Need<IFlag>()])
            .Commit(runtime.NewActivation("consumer"), snapshot);
        Assert.Equal(1, View(root).Get<IValue>().Number);
        Assert.Equal(2, View(left).Get<IValue>().Number);
        Assert.Equal(3, View(right).Get<IValue>().Number);
        Assert.Equal(2, View(nested).Get<IValue>().Number);
        Assert.False(View(left).Get<IFlag>().Enabled);
        Assert.True(View(nested).Get<IFlag>().Enabled);
    }

    [Fact]
    public void EmptyIsolatedRealmDoesNotFallBackToParentProvider()
    {
        var runtime = new ServiceRuntime(4);
        var snapshot = new ServiceBindingSnapshot(runtime, [ServiceBinding.Create(runtime.RootScope,
            runtime.NewActivation("provider"), typeof(IValue), new Value(1))]);
        var isolated = runtime.RootScope.CreateChild([typeof(IValue)]);
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ServiceUnavailableException>(() =>
            new DeclaredServiceView(isolated, [Need<IValue>()]).Commit(runtime.NewActivation("consumer"), snapshot));
    }

    [Fact]
    public void RuntimeBoundariesRejectForeignProvidersSnapshotsAndConsumers()
    {
        var first = new ServiceRuntime(4);
        var second = new ServiceRuntime(4);
        var foreign = ServiceBinding.Create(second.RootScope, second.NewActivation("same-entry"), typeof(IValue), new Value(7));
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RootScope.Resolve(typeof(IValue)), second.RootScope.Resolve(typeof(IValue)));
        Assert.Throws<InvalidOperationException>(() => ServiceBinding.Create(first.RootScope, foreign.Registration.Provider, typeof(IValue), new Value(1)));
        Assert.Throws<InvalidOperationException>(() => new ServiceBindingSnapshot(first, [foreign]));
        var declared = new DeclaredServiceView(first.RootScope, [new(typeof(IValue), ServiceCardinality.Single, false)]);
        Assert.Throws<InvalidOperationException>(() => declared.Commit(second.NewActivation("reader"), new ServiceBindingSnapshot(first, [])));
        Assert.Throws<InvalidOperationException>(() => declared.Commit(first.NewActivation("reader"), new ServiceBindingSnapshot(second, [])));
    }

    [Fact]
    public void ConcurrentActivationAndRegistrationIdentitiesNeverReuseAnOrdinal()
    {
        var runtime = new ServiceRuntime(4);
        var identities = new ConcurrentBag<long>();
        Parallel.For(0, 500, _ =>
        {
            var activation = runtime.NewActivation("same-entry");
            identities.Add(activation.Generation);
            identities.Add(ServiceBinding.Create(runtime.RootScope, activation, typeof(IValue), new Value(1)).Registration.Id);
        });
        Assert.Equal(1000, identities.Distinct().Count());
        Assert.All(identities, id => Assert.True(id > 0));
    }

    [Fact]
    public void IsolationInputsAreCopiedAndInheritedMapsStayBounded()
    {
        var runtime = new ServiceRuntime(1);
        var input = new[] { typeof(IValue) };
        var child = runtime.RootScope.CreateChild(input);
        input[0] = typeof(IFlag);
        Assert.NotEqual(runtime.RootScope.Resolve(typeof(IValue)), child.Resolve(typeof(IValue)));
        Assert.Equal(runtime.RootScope.Resolve(typeof(IFlag)), child.Resolve(typeof(IFlag)));
        Assert.Throws<InvalidOperationException>(() => child.CreateChild([typeof(IFlag)]));
        Assert.NotEqual(child.Resolve(typeof(IValue)), child.CreateChild([typeof(IValue)]).Resolve(typeof(IValue)));
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(IEnumerable<>))]
    public void ServiceKeysRejectConcreteAndOpenGenericContracts(Type invalid)
    {
        var runtime = new ServiceRuntime(4);
        Assert.Throws<ArgumentException>(() => runtime.RootScope.Resolve(invalid));
        Assert.Throws<ArgumentException>(() => runtime.RootScope.CreateChild([invalid]));
    }

    [Fact]
    public void InvalidCapacityNamesAndDuplicateIsolationFailExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceRuntime(0));
        var runtime = new ServiceRuntime(4);
        Assert.Throws<ArgumentException>(() => runtime.NewActivation(" "));
        Assert.Throws<ArgumentException>(() => runtime.RootScope.CreateChild([typeof(IValue), typeof(IValue)]));
    }
}
