using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Tests for <see cref="EventBus"/> dispatch modes.</summary>
public sealed class EventBusTests
{
    private sealed record Ping(int Value) : IEvent;

    private static EventBus CreateBus() => new(NullLogger.Instance);

    [Fact]
    public void EmitRunsHigherPriorityFirstAndTiesInRegistrationOrder()
    {
        var bus = CreateBus();
        var order = new List<string>();
        bus.On<Ping>(_ => order.Add("low"), priority: 0);
        bus.On<Ping>(_ => order.Add("high"), priority: 10);
        bus.On<Ping>(_ => order.Add("low2"), priority: 0);
        bus.Emit(new Ping(1));
        Assert.Equal(["high", "low", "low2"], order);
    }

    [Fact]
    public void EmitLogsAndContinuesOnHandlerException()
    {
        var bus = CreateBus();
        var ran = false;
        bus.On<Ping>(_ => throw new InvalidOperationException("boom"), priority: 10);
        bus.On<Ping>(_ => ran = true, priority: 0);
        bus.Emit(new Ping(1));
        Assert.True(ran);
    }

    [Fact]
    public async Task SerialAsyncRunsInPriorityOrderOneByOne()
    {
        var bus = CreateBus();
        var order = new List<string>();
        bus.OnAsync<Ping>(async (_, _) => { await Task.Delay(20); order.Add("slow-high"); }, priority: 10);
        bus.OnAsync<Ping>((_, _) => { order.Add("fast-low"); return ValueTask.CompletedTask; }, priority: 0);
        await bus.SerialAsync(new Ping(1), CancellationToken.None);
        Assert.Equal(["slow-high", "fast-low"], order);
    }

    [Fact]
    public async Task ParallelAsyncRunsAllHandlers()
    {
        var bus = CreateBus();
        var count = 0;
        bus.OnAsync<Ping>((_, _) => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
        bus.OnAsync<Ping>((_, _) => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
        await bus.ParallelAsync(new Ping(1), CancellationToken.None);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task WaterfallAsyncMiddlewareWrapsTerminal()
    {
        var bus = CreateBus();
        var trace = new List<string>();
        bus.Intercept<Ping, string>(async (e, next, ct) =>
        {
            trace.Add("before-outer");
            var result = await next(ct);
            trace.Add("after-outer");
            return result + "-outer";
        }, priority: 10);
        bus.Intercept<Ping, string>(async (e, next, ct) =>
        {
            trace.Add("before-inner");
            var result = await next(ct);
            trace.Add("after-inner");
            return result + "-inner";
        }, priority: 0);
        var result = await bus.WaterfallAsync(new Ping(1), _ => ValueTask.FromResult("end"), CancellationToken.None);
        Assert.Equal("end-inner-outer", result);
        Assert.Equal(["before-outer", "before-inner", "after-inner", "after-outer"], trace);
    }

    [Fact]
    public async Task WaterfallAsyncMiddlewareCanShortCircuit()
    {
        var bus = CreateBus();
        var terminalRan = false;
        bus.Intercept<Ping, string>((e, next, ct) => ValueTask.FromResult("blocked"), priority: 10);
        var result = await bus.WaterfallAsync(new Ping(1), _ => { terminalRan = true; return ValueTask.FromResult("end"); }, CancellationToken.None);
        Assert.Equal("blocked", result);
        Assert.False(terminalRan);
    }

    [Fact]
    public void BailReturnsFirstNonNullInPriorityOrder()
    {
        var bus = CreateBus();
        bus.OnBail<Ping, string>(_ => null, priority: 20);
        bus.OnBail<Ping, string>(_ => "second", priority: 0);
        bus.OnBail<Ping, string>(_ => "first", priority: 10);
        bus.OnBail<Ping, string>(_ => "never", priority: -10);
        Assert.Equal("first", bus.Bail<Ping, string>(new Ping(1)));
    }

    [Fact]
    public void BailReturnsNullWhenNoHandlerProducesResult()
    {
        var bus = CreateBus();
        bus.OnBail<Ping, string>(_ => null);
        Assert.Null(bus.Bail<Ping, string>(new Ping(1)));
    }

    [Fact]
    public void DisposalRemovesHandlers()
    {
        var bus = CreateBus();
        var count = 0;
        var sub = bus.On<Ping>(_ => count++);
        bus.Emit(new Ping(1));
        sub.Dispose();
        bus.Emit(new Ping(1));
        Assert.Equal(1, count);
    }
}
