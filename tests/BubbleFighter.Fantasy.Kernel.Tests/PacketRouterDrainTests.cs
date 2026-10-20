using System.Diagnostics;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PacketRouterDrainTests
{
    private static readonly InboundPacket Packet = new(7, ReadOnlyMemory<byte>.Empty);

    private static PacketRouterPlugin.PacketRouter Router(TimeSpan drain) =>
        new(new EventBus(NullLogger.Instance), drain, NullLogger.Instance);

    /// <summary>Both handled and unhandled frames await asynchronous observers before dispatch returns.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAwaitsObserversBeforeTheHandlerAndBufferReuse(bool handled)
    {
        var events = new EventBus(NullLogger.Instance);
        var router = new PacketRouterPlugin.PacketRouter(events, TimeSpan.FromSeconds(3), NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        using var observer = events.OnAsync<PacketReceivedEvent>(async (received, ct) =>
        {
            Assert.Equal(handled, received.HasHandler);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            finished = true;
        });
        using var registration = handled ? router.Register("p", 7, (_, _, _) =>
        {
            Assert.True(finished);
            return ValueTask.CompletedTask;
        }) : null;
        var call = router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(call.IsCompleted);
        release.SetResult();
        await call.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(finished);
    }

    [Fact]
    public async Task RemovalCancelsAndWaitsForTheRunningCall()
    {
        var router = Router(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var registration = router.Register("p", 7, async (_, _, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            await Task.Delay(100, CancellationToken.None);
            finished = true;
        });
        var call = router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, CancellationToken.None).AsTask();
        await entered.Task;

        await Task.Run(registration.Dispose);

        Assert.True(finished);
        await call;
    }

    [Fact]
    public async Task RemovedHandlerReceivesNoNewCalls()
    {
        var router = Router(TimeSpan.FromSeconds(10));
        var calls = 0;
        var registration = router.Register("p", 7, (_, _, _) => { calls++; return ValueTask.CompletedTask; });
        await router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, CancellationToken.None);
        registration.Dispose();
        await router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, CancellationToken.None);
        Assert.Equal(1, calls);
        router.Register("p", 7, (_, _, _) => ValueTask.CompletedTask).Dispose();
    }

    [Fact]
    public async Task HandlerRemovingItsOwnRegistrationDoesNotWaitForItself()
    {
        var router = Router(TimeSpan.FromSeconds(30));
        IDisposable? registration = null;
        registration = router.Register("p", 7, (_, _, _) =>
        {
            registration!.Dispose();
            return ValueTask.CompletedTask;
        });
        var watch = Stopwatch.StartNew();
        await router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandlerIgnoringCancellationIsLeftBehindAfterTheDrainLimit()
    {
        var router = Router(TimeSpan.FromMilliseconds(200));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = router.Register("p", 7, async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        var call = router.DispatchAsync("p", new MessageRouterTests.Session(), Packet, CancellationToken.None).AsTask();
        await entered.Task;

        var watch = Stopwatch.StartNew();
        await Task.Run(registration.Dispose);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));

        release.SetResult();
        await call;
    }
}
