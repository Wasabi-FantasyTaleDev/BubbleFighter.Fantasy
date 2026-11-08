using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks that selected source release retires only its obsolete bootstrap without hiding other failures.</summary>
public sealed class AutomaticCenterCompletionTests
{
    /// <summary>Successful selected release cancels and drains its bootstrap without cancelling the parent or another row.</summary>
    [Fact]
    public async Task Redirect_release_retires_only_the_same_row_bootstrap()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        await AutomaticCenterListener.RunTogetherAsync(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { drained = true; }
        }, async ct =>
        {
            await started.Task.WaitAsync(ct);
            return AutomaticCenterOutcome.RedirectReleased;
        }, parent.Token);
        Assert.True(drained);
        Assert.False(parent.IsCancellationRequested);
    }

    /// <summary>Unselected or unavailable auxiliaries do not cancel the manual bootstrap, which remains independently completable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonredirect_completion_preserves_manual_bootstrap(bool unavailable)
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var automaticCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBootstrap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken bootstrapToken = default;
        var work = AutomaticCenterListener.RunTogetherAsync(async ct =>
        {
            bootstrapToken = ct;
            await releaseBootstrap.Task.WaitAsync(ct);
        }, _ =>
        {
            automaticCompleted.SetResult();
            return Task.FromResult(unavailable ? AutomaticCenterOutcome.Unavailable : AutomaticCenterOutcome.UnselectedReleased);
        }, parent.Token);
        await automaticCompleted.Task.WaitAsync(parent.Token);
        Assert.False(bootstrapToken.IsCancellationRequested);
        Assert.False(work.IsCompleted);
        releaseBootstrap.SetResult();
        await work.WaitAsync(parent.Token);
    }

    /// <summary>A genuine bootstrap exception remains a row failure even when selected source release occurs concurrently.</summary>
    [Fact]
    public async Task Successful_redirect_does_not_hide_a_bootstrap_transport_failure()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var expected = new IOException("Bootstrap fixture failure.");
        var error = await Assert.ThrowsAsync<IOException>(() => AutomaticCenterListener.RunTogetherAsync(
            _ => Task.FromException(expected), _ => Task.FromResult(AutomaticCenterOutcome.RedirectReleased), parent.Token));
        Assert.Same(expected, error);
    }

    /// <summary>A redirect failure cancels its bootstrap and propagates the actual protocol or transport error.</summary>
    [Fact]
    public async Task Automatic_failure_drains_bootstrap_and_remains_an_error()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var expected = new IOException("Automatic fixture failure.");
        var drained = false;
        var error = await Assert.ThrowsAsync<IOException>(() => AutomaticCenterListener.RunTogetherAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { drained = true; }
        }, _ => Task.FromException<AutomaticCenterOutcome>(expected), parent.Token));
        Assert.Same(expected, error);
        Assert.True(drained);
    }
}
