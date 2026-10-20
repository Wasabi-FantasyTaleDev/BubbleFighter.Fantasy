using System.Text;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Host;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks bounded control input, readiness admission and joined shutdown ownership.</summary>
public sealed class ManagedStdioSessionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>LF/CRLF stop and EOF cancel startup before any readiness marker is admitted.</summary>
    [Theory]
    [InlineData("stop\n")]
    [InlineData("stop\r\n")]
    [InlineData("")]
    public async Task StopBeforeConvergenceCannotPublishReadiness(string input)
    {
        await using var shutdown = new OwnedShutdownSignal();
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        using var output = new StringWriter();
        await using var managed = new ManagedStdioSession(stream, output, shutdown,
            ct => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(stream.ReadByte()); });
        Assert.True(shutdown.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(managed.PublishReady);
        Assert.Equal("", output.ToString());
        await managed.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.False(stream.CanRead);
    }

    /// <summary>Unknown and oversized lines request shutdown and report their errors after ownership drains.</summary>
    [Theory]
    [InlineData("go\n", "Unknown managed-stdio command")]
    [InlineData("\n", "Unknown managed-stdio command")]
    [InlineData("overflow", "exceeds 64 bytes")]
    public async Task InvalidInputIsBoundedAndFailsAfterStopping(string input, string diagnostic)
    {
        if (input == "overflow") input = new string('s', ManagedStdioSession.MaximumCommandBytes + 1);
        await using var shutdown = new OwnedShutdownSignal();
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        using var output = new StringWriter();
        var managed = new ManagedStdioSession(stream, output, shutdown,
            ct => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(stream.ReadByte()); });
        Assert.True(shutdown.Token.IsCancellationRequested);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => managed.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.Contains(diagnostic, error.Message);
        Assert.False(stream.CanRead);
        Assert.Equal("", output.ToString());
    }

    /// <summary>External cancellation joins an idle reader rather than leaving an abandoned input operation.</summary>
    [Fact]
    public async Task ExternalShutdownJoinsIdleInputAndReadinessAppearsExactlyOnce()
    {
        await using var shutdown = new OwnedShutdownSignal();
        var channel = Channel.CreateBounded<int>(1);
        var stream = new MemoryStream();
        using var output = new StringWriter();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var managed = new ManagedStdioSession(stream, output, shutdown, async ct =>
        {
            try { return await channel.Reader.ReadAsync(ct); }
            finally { exited.TrySetResult(); }
        });
        managed.PublishReady();
        Assert.Equal(ManagedStdioSession.ReadyMarker + Environment.NewLine, output.ToString());
        Assert.Throws<InvalidOperationException>(managed.PublishReady);
        shutdown.RequestCancellation();
        await managed.DisposeAsync().AsTask().WaitAsync(Limit);
        await exited.Task.WaitAsync(Limit);
        Assert.False(stream.CanRead);
    }
}
