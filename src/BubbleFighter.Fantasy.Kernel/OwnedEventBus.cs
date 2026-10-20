using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Owns subscriptions and their in-flight callbacks without changing event dispatch modes.</summary>
internal sealed class OwnedEventBus(PluginContext owner, IEventBus inner) : IEventBus
{
    public IDisposable On<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return owner.OwnCallbackRegistration(registration => inner.On<TEvent>(value =>
        {
            if (!registration.TryEnter(out var invocation)) return;
            using (invocation) handler(value);
        }, priority));
    }

    public IDisposable OnAsync<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return owner.OwnCallbackRegistration(registration => inner.OnAsync<TEvent>(async (value, ct) =>
        {
            if (!registration.TryEnter(out var invocation)) return;
            using (invocation) await handler(value, ct).ConfigureAwait(false);
        }, priority));
    }

    public IDisposable Intercept<TEvent, TResult>(Func<TEvent, Func<CancellationToken, ValueTask<TResult>>, CancellationToken, ValueTask<TResult>> middleware, int priority = 0) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return owner.OwnCallbackRegistration(registration => inner.Intercept<TEvent, TResult>(async (value, next, ct) =>
        {
            if (!registration.TryEnter(out var invocation)) return await next(ct).ConfigureAwait(false);
            using (invocation) return await middleware(value, next, ct).ConfigureAwait(false);
        }, priority));
    }

    public IDisposable OnBail<TEvent, TResult>(Func<TEvent, TResult?> handler, int priority = 0) where TEvent : IEvent where TResult : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return owner.OwnCallbackRegistration(registration => inner.OnBail<TEvent, TResult>(value =>
        {
            if (!registration.TryEnter(out var invocation)) return null;
            using (invocation) return handler(value);
        }, priority));
    }

    public void Emit<TEvent>(TEvent value) where TEvent : IEvent => inner.Emit(value);
    public ValueTask SerialAsync<TEvent>(TEvent value, CancellationToken ct) where TEvent : IEvent => inner.SerialAsync(value, ct);
    public ValueTask ParallelAsync<TEvent>(TEvent value, CancellationToken ct) where TEvent : IEvent => inner.ParallelAsync(value, ct);
    public ValueTask<TResult> WaterfallAsync<TEvent, TResult>(TEvent value, Func<CancellationToken, ValueTask<TResult>> terminal, CancellationToken ct) where TEvent : IEvent => inner.WaterfallAsync(value, terminal, ct);
    public TResult? Bail<TEvent, TResult>(TEvent value) where TEvent : IEvent where TResult : class => inner.Bail<TEvent, TResult>(value);
}
