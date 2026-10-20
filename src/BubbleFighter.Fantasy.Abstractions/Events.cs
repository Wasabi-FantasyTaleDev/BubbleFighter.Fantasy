namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Marker interface for event payloads carried by <see cref="IEventBus"/>.</summary>
public interface IEvent;

/// <summary>
/// Typed event bus with explicit dispatch modes. Higher priority handlers run first;
/// equal priorities run in registration order. Disposing a subscription stops later calls,
/// cancels the calls already inside it, and waits for them within a bounded drain limit;
/// a handler disposing its own subscription does not wait for itself.
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribes a synchronous handler invoked by <see cref="Emit{TEvent}"/>.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="priority">Higher runs first; ties keep registration order.</param>
    /// <returns>A disposer that removes the subscription.</returns>
    IDisposable On<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : IEvent;

    /// <summary>Subscribes an asynchronous handler invoked by <see cref="SerialAsync{TEvent}"/>
    /// and <see cref="ParallelAsync{TEvent}"/>.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="priority">Higher runs first; ties keep registration order.</param>
    /// <returns>A disposer that removes the subscription.</returns>
    IDisposable OnAsync<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, int priority = 0) where TEvent : IEvent;

    /// <summary>Subscribes an around-middleware invoked by <see cref="WaterfallAsync{TEvent, TResult}"/>.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <typeparam name="TResult">The pipeline result type.</typeparam>
    /// <param name="middleware">Receives the event and a <c>next</c> delegate; not calling
    /// <c>next</c> short-circuits the pipeline.</param>
    /// <param name="priority">Higher wraps outer (runs first); ties keep registration order.</param>
    /// <returns>A disposer that removes the subscription.</returns>
    IDisposable Intercept<TEvent, TResult>(Func<TEvent, Func<CancellationToken, ValueTask<TResult>>, CancellationToken, ValueTask<TResult>> middleware, int priority = 0) where TEvent : IEvent;

    /// <summary>Subscribes a result-producing handler invoked by <see cref="Bail{TEvent, TResult}"/>.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <typeparam name="TResult">The result type; first non-<see langword="null"/> wins.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="priority">Higher runs first; ties keep registration order.</param>
    /// <returns>A disposer that removes the subscription.</returns>
    IDisposable OnBail<TEvent, TResult>(Func<TEvent, TResult?> handler, int priority = 0)
        where TEvent : IEvent where TResult : class;

    /// <summary>Invokes every synchronous handler for the event. Handler exceptions are
    /// logged, never thrown.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <param name="e">The event payload.</param>
    void Emit<TEvent>(TEvent e) where TEvent : IEvent;

    /// <summary>Awaits every async handler one by one, in priority order.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <param name="e">The event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SerialAsync<TEvent>(TEvent e, CancellationToken ct) where TEvent : IEvent;

    /// <summary>Runs every async handler concurrently and awaits them all.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <param name="e">The event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ParallelAsync<TEvent>(TEvent e, CancellationToken ct) where TEvent : IEvent;

    /// <summary>
    /// Runs the around-middleware pipeline for the event, innermost around
    /// <paramref name="terminal"/>. A middleware that does not call its <c>next</c> delegate
    /// short-circuits the pipeline.
    /// </summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <typeparam name="TResult">The pipeline result type.</typeparam>
    /// <param name="e">The event payload.</param>
    /// <param name="terminal">The innermost operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pipeline result.</returns>
    ValueTask<TResult> WaterfallAsync<TEvent, TResult>(TEvent e, Func<CancellationToken, ValueTask<TResult>> terminal, CancellationToken ct) where TEvent : IEvent;

    /// <summary>Invokes bail handlers in priority order and returns the first non-<see langword="null"/> result.</summary>
    /// <typeparam name="TEvent">The event payload type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="e">The event payload.</param>
    /// <returns>The first non-<see langword="null"/> result, or <see langword="null"/>.</returns>
    TResult? Bail<TEvent, TResult>(TEvent e) where TEvent : IEvent where TResult : class;
}
