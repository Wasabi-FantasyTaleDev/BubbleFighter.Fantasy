using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class DecoratorTests
{
    public interface IMessage
    {
        string Read();
        ValueTask<string> ReadAsync();
    }

    public sealed class Message : IMessage
    {
        public string Read() => "base";
        public ValueTask<string> ReadAsync() => ValueTask.FromResult(Read());
    }

    public sealed class Wrapper(IMessage inner, string label) : IMessage
    {
        public string Read() => $"{label}({inner.Read()})";
        public async ValueTask<string> ReadAsync() => $"{label}({await inner.ReadAsync()})";
    }

    public sealed class Delayed(IMessage inner, Task ready, TaskCompletionSource entered) : IMessage
    {
        public string Read() => inner.Read();
        public async ValueTask<string> ReadAsync()
        {
            entered.TrySetResult();
            await ready;
            return $"delayed({await inner.ReadAsync()})";
        }
    }

    [Fact]
    public void ExistingReferenceSeesPriorityChangesAndRemovalOfTheMiddleLayer()
    {
        var registry = new ServiceRegistry();
        using var basis = registry.RegisterDecoratable<IMessage>(new Message());
        var captured = registry.Get<IMessage>();
        var factories = 0;
        using var outer = registry.Decorate<IMessage>(inner => { factories++; return new Wrapper(inner, "outer"); }, 10);
        var middle = registry.Decorate<IMessage>(inner => { factories++; return new Wrapper(inner, "middle"); }, 5);
        using var low = registry.Decorate<IMessage>(inner => { factories++; return new Wrapper(inner, "low"); }, -1);
        Assert.Same(captured, registry.Get<IMessage>());
        Assert.Equal("outer(middle(low(base)))", captured.Read());
        middle.Dispose();
        middle.Dispose();
        Assert.Equal("outer(low(base))", captured.Read());
        outer.Dispose();
        Assert.Equal("low(base)", captured.Read());
        Assert.Equal(3, factories);
    }

    [Fact]
    public void EqualPrioritiesRetainRegistrationOrderAndFactoriesFailAtomically()
    {
        var registry = new ServiceRegistry();
        using var basis = registry.RegisterDecoratable<IMessage>(new Message());
        using var first = registry.Decorate<IMessage>(inner => new Wrapper(inner, "first"));
        using var second = registry.Decorate<IMessage>(inner => new Wrapper(inner, "second"));
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IMessage>(_ => throw new InvalidOperationException("factory")));
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IMessage>(_ => null!));
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IMessage>(inner => inner));
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IMessage>(inner => { basis.Dispose(); return new Wrapper(inner, "bad"); }));
        Assert.Equal("first(second(base))", registry.Get<IMessage>().Read());
    }

    [Fact]
    public async Task AwaitingCallsKeepTheirSnapshotWhileFutureCallsSeeTheEditedChain()
    {
        var registry = new ServiceRegistry();
        using var basis = registry.RegisterDecoratable<IMessage>(new Message());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayed = registry.Decorate<IMessage>(inner => new Delayed(inner, resume.Task, entered), 10);
        var middle = registry.Decorate<IMessage>(inner => new Wrapper(inner, "middle"));
        var service = registry.Get<IMessage>();
        var pending = service.ReadAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        middle.Dispose();
        Assert.Equal("base", service.Read());
        resume.SetResult();
        Assert.Equal("delayed(middle(base))", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("delayed(base)", await service.ReadAsync());
    }

    [Fact]
    public void BaseRemovalInvalidatesOldReferencesAndCannotAffectAReplacement()
    {
        var registry = new ServiceRegistry();
        var basis = registry.RegisterDecoratable<IMessage>(new Message());
        var old = registry.Get<IMessage>();
        var layer = registry.Decorate<IMessage>(inner => new Wrapper(inner, "old"));
        basis.Dispose();
        Assert.Throws<ServiceUnavailableException>(() => old.Read());
        using var replacement = registry.RegisterDecoratable<IMessage>(new Message());
        layer.Dispose();
        basis.Dispose();
        Assert.Equal("base", registry.Get<IMessage>().Read());
        Assert.Throws<ServiceUnavailableException>(() => old.Read());
    }

    public interface IUnsupported { void Apply(Span<byte> bytes); }
    public sealed class Unsupported : IUnsupported { public void Apply(Span<byte> bytes) { } }

    [Fact]
    public void DecorationRequiresExplicitOptInAndUnsupportedSignaturesFailBeforeRegistration()
    {
        var registry = new ServiceRegistry();
        using var plain = registry.Register<IMessage>(new Message());
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IMessage>(inner => new Wrapper(inner, "bad")));
        Assert.Throws<NotSupportedException>(() => registry.RegisterDecoratable<IUnsupported>(new Unsupported()));
        Assert.False(registry.TryGet<IUnsupported>(out _));
    }

    public sealed class Throws : IMessage
    {
        public string Read() => throw new ArgumentException("original");
        public ValueTask<string> ReadAsync() => ValueTask.FromException<string>(new ArgumentException("async original"));
    }

    [Fact]
    public async Task ForwardingPreservesOriginalSynchronousAndAsynchronousExceptions()
    {
        var registry = new ServiceRegistry();
        using var registration = registry.RegisterDecoratable<IMessage>(new Throws());
        Assert.Equal("original", Assert.Throws<ArgumentException>(() => registry.Get<IMessage>().Read()).Message);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => registry.Get<IMessage>().ReadAsync().AsTask());
        Assert.Equal("async original", error.Message);
    }
}
