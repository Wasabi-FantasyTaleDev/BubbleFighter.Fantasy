using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ServiceDependencyViewTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Flag(bool Enabled) : IFlag;
    private sealed class EqualityTrap : IValue
    {
        public int Number => 1;
        public override bool Equals(object? obj) => throw new InvalidOperationException("User equality must not run.");
        public override int GetHashCode() => throw new InvalidOperationException("User hash code must not run.");
    }
    private static ServiceRequirement Need<T>(bool required = true, ServiceCardinality cardinality = ServiceCardinality.Single) =>
        new(typeof(T), cardinality, required);
    private static CommittedServiceView View(ServiceRuntime runtime, ServiceBindingSnapshot snapshot, params ServiceRequirement[] requirements) =>
        new DeclaredServiceView(runtime.RootScope, requirements).Commit(runtime.NewActivation("consumer"), snapshot);

    [Fact]
    public void OldActivationKeepsItsProviderObjectsAndEdgesWhenCurrentBindingsChange()
    {
        var runtime = new ServiceRuntime(8);
        var provider = runtime.NewActivation("provider");
        var original = new Value(1);
        var first = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), original);
        var view = View(runtime, new ServiceBindingSnapshot(runtime, [first]), Need<IValue>());
        var replacementProvider = runtime.NewActivation("provider");
        var replacement = ServiceBinding.Create(runtime.RootScope, replacementProvider, typeof(IValue), new Value(2));
        var current = new ServiceBindingSnapshot(runtime, [replacement]);
        Assert.False(view.Matches(current));
        Assert.Same(original, view.Get<IValue>());
        Assert.Equal(provider, Assert.Single(view.Providers));
        Assert.Equal(2, View(runtime, current, Need<IValue>()).Get<IValue>().Number);
        Assert.False(view.Matches(new ServiceBindingSnapshot(runtime, [])));
        Assert.Same(original, view.Get<IValue>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameValueDoesNotHideRegistrationOrActivationReplacement(bool newActivation)
    {
        var runtime = new ServiceRuntime(4);
        var provider = runtime.NewActivation("provider");
        var value = new EqualityTrap();
        var first = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), value);
        var snapshot = new ServiceBindingSnapshot(runtime, [first]);
        var view = View(runtime, snapshot, Need<IValue>());
        var next = ServiceBinding.Create(runtime.RootScope, newActivation ? runtime.NewActivation("provider") : provider, typeof(IValue), value);
        Assert.True(view.Matches(snapshot));
        Assert.False(view.Matches(new ServiceBindingSnapshot(runtime, [next])));
        Assert.Same(value, view.Get<IValue>());
    }

    [Fact]
    public void OptionalAbsenceIsCommittedAndAvailabilityDoesNotSilentlyChangeReads()
    {
        var runtime = new ServiceRuntime(4);
        var empty = new ServiceBindingSnapshot(runtime, []);
        var absent = View(runtime, empty, Need<IValue>(false));
        var binding = ServiceBinding.Create(runtime.RootScope, runtime.NewActivation("provider"), typeof(IValue), new Value(8));
        var available = new ServiceBindingSnapshot(runtime, [binding]);
        Assert.True(absent.Matches(empty));
        Assert.False(absent.Matches(available));
        Assert.False(absent.TryGet<IValue>(out var missing));
        Assert.Null(missing);
        Assert.Empty(absent.Providers);
        Assert.Throws<ServiceUnavailableException>(() => absent.Get<IValue>());
        var present = View(runtime, available, Need<IValue>(false));
        Assert.False(present.Matches(empty));
        Assert.Equal(8, present.Get<IValue>().Number);
    }

    [Fact]
    public void OptionalAbsenceInDifferentRealmsIsNotTheSameDependencyTarget()
    {
        var runtime = new ServiceRuntime(4);
        var declarations = new[] { Need<IValue>(false) };
        var snapshot = new ServiceBindingSnapshot(runtime, []);
        var root = new DeclaredServiceView(runtime.RootScope, declarations).Commit(runtime.NewActivation("consumer"), snapshot);
        var child = new DeclaredServiceView(runtime.RootScope.CreateChild([typeof(IValue)]), declarations)
            .Commit(runtime.NewActivation("consumer"), snapshot);
        Assert.False(root.HasSameDependencies(child));
    }

    [Fact]
    public void UnrelatedServicesAndOtherRealmsDoNotInvalidateAView()
    {
        var runtime = new ServiceRuntime(8);
        var provider = runtime.NewActivation("provider");
        var binding = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var view = View(runtime, new ServiceBindingSnapshot(runtime, [binding]), Need<IValue>());
        var otherRealm = runtime.RootScope.CreateChild([typeof(IValue)]);
        var changed = new ServiceBindingSnapshot(runtime, [binding,
            ServiceBinding.Create(runtime.RootScope, provider, typeof(IFlag), new Flag(true)),
            ServiceBinding.Create(otherRealm, provider, typeof(IValue), new Value(99))]);
        Assert.True(view.Matches(changed));
        Assert.True(view.HasSameDependencies(View(runtime, changed, Need<IValue>())));
    }

    [Fact]
    public void MultipleProvidersUsePriorityThenRegistrationOrderAndFreezeTheList()
    {
        var runtime = new ServiceRuntime(8);
        ServiceBinding Bind(int value, int priority) => ServiceBinding.Create(runtime.RootScope, runtime.NewActivation($"p{value}"),
            typeof(IValue), new Value(value), ServiceCardinality.Multiple, priority);
        var first = Bind(1, 0);
        var second = Bind(2, 10);
        var third = Bind(3, 10);
        var original = new ServiceBindingSnapshot(runtime, [third, first, second]);
        var view = View(runtime, original, Need<IValue>(cardinality: ServiceCardinality.Multiple));
        var values = view.GetAll<IValue>();
        Assert.Equal(new[] { 2, 3, 1 }, values.Select(value => value.Number));
        Assert.Throws<NotSupportedException>(() => ((IList<IValue>)values)[0] = new Value(7));
        var current = new ServiceBindingSnapshot(runtime, [first, third]);
        Assert.False(view.Matches(current));
        Assert.Equal(new[] { 2, 3, 1 }, view.GetAll<IValue>().Select(value => value.Number));
        Assert.Equal(3, view.Providers.Count);
    }

    [Fact]
    public void UndeclaredReadsAndWrongReadModesFailRatherThanFallingBackToTheGlobalTable()
    {
        var runtime = new ServiceRuntime(4);
        var binding = ServiceBinding.Create(runtime.RootScope, runtime.NewActivation("provider"), typeof(IValue), new Value(1));
        var view = View(runtime, new ServiceBindingSnapshot(runtime, [binding]), Need<IValue>());
        Assert.Throws<InvalidOperationException>(() => view.Get<IFlag>());
        Assert.Throws<InvalidOperationException>(() => view.TryGet<IFlag>(out _));
        Assert.Throws<InvalidOperationException>(() => view.GetAll<IFlag>());
        Assert.Throws<InvalidOperationException>(() => view.GetAll<IValue>());
        var optionalMany = View(runtime, new ServiceBindingSnapshot(runtime, []), Need<IValue>(false, ServiceCardinality.Multiple));
        Assert.Empty(optionalMany.GetAll<IValue>());
        Assert.Throws<InvalidOperationException>(() => optionalMany.TryGet<IValue>(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRequiredDependenciesCannotBeCommitted(bool multiple)
    {
        var runtime = new ServiceRuntime(4);
        Assert.Throws<ServiceUnavailableException>(() => View(runtime, new ServiceBindingSnapshot(runtime, []),
            Need<IValue>(cardinality: multiple ? ServiceCardinality.Multiple : ServiceCardinality.Single)));
    }

    [Fact]
    public void InputArraysCannotChangeACommittedViewAndDeclarationOrderIsIrrelevant()
    {
        var runtime = new ServiceRuntime(4);
        var provider = runtime.NewActivation("provider");
        var value = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(1));
        var flag = ServiceBinding.Create(runtime.RootScope, provider, typeof(IFlag), new Flag(true));
        var input = new[] { value, flag };
        var snapshot = new ServiceBindingSnapshot(runtime, input);
        input[0] = ServiceBinding.Create(runtime.RootScope, provider, typeof(IValue), new Value(2));
        var declarations = new[] { Need<IValue>(), Need<IFlag>() };
        var declared = new DeclaredServiceView(runtime.RootScope, declarations);
        declarations[0] = Need<IFlag>();
        var view = declared.Commit(runtime.NewActivation("consumer"), snapshot);
        Assert.Equal(1, view.Get<IValue>().Number);
        Assert.True(view.HasSameDependencies(View(runtime, snapshot, Need<IFlag>(), Need<IValue>())));
        Assert.Single(view.Providers);
        Assert.Equal(value.Registration, Assert.Single(view.Selections[typeof(IValue)].Bindings).Registration);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Type, ServiceSelection>)view.Selections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ServiceActivation>)view.Providers).Clear());
    }
}
