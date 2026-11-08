using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Installation;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class InstallationDirectoryPublisherTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task WindowsPublicationConflictRetriesOnlyTheMoveAndKeepsItsDestination(int errorCode)
    {
        var calls = 0;
        var waits = 0;
        var options = new InstallationOptions { ClientDataPublishAttempts = 3, ClientDataPublishRetryMs = 17 };
        var publisher = new InstallationDirectoryPublisher((source, destination) =>
        {
            Assert.Equal("staging", source);
            Assert.Equal("destination", destination);
            if (++calls < 3) throw Failure(errorCode);
        }, true, (duration, ct) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(17), duration);
            waits++;
            return Task.CompletedTask;
        });
        await publisher.MoveAsync("staging", "destination", options, default);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(32, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(112, true)]
    [InlineData(183, true)]
    public async Task NonWindowsAndUnrelatedFailuresAreNeverRetried(int code, bool windows)
    {
        var error = Failure(code);
        var calls = 0;
        var publisher = new InstallationDirectoryPublisher((_, _) => { calls++; throw error; }, windows,
            (_, _) => throw new InvalidOperationException("Unexpected retry."));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() =>
            publisher.MoveAsync("staging", "destination", new(), default).AsTask()));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task PermanentDenialStopsAtTheConfiguredAttemptLimit(int attempts)
    {
        var error = Failure(5);
        var calls = 0;
        var waits = 0;
        var publisher = new InstallationDirectoryPublisher((_, _) => { calls++; throw error; }, true,
            (_, _) => { waits++; return Task.CompletedTask; });
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => publisher.MoveAsync("staging", "destination",
            new() { ClientDataPublishAttempts = attempts }, default).AsTask()));
        Assert.Equal(attempts, calls);
        Assert.Equal(attempts - 1, waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportCancellationStopsBeforePublicationOrBeforeItsNextAttempt(bool duringDelay)
    {
        using var cancellation = new CancellationTokenSource();
        if (!duringDelay) cancellation.Cancel();
        var calls = 0;
        var publisher = new InstallationDirectoryPublisher((_, _) => { calls++; throw Failure(32); }, true,
            (_, ct) => { cancellation.Cancel(); return Task.FromCanceled(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.MoveAsync("staging", "destination",
            new(), cancellation.Token).AsTask());
        Assert.Equal(duringDelay ? 1 : 0, calls);
    }

    [Fact]
    public async Task UnauthorizedAccessExceptionsAreNotSwallowedOrRetried()
    {
        var error = new UnauthorizedAccessException("Permanent policy denial.");
        var publisher = new InstallationDirectoryPublisher((_, _) => throw error, true,
            (_, _) => throw new InvalidOperationException("Unexpected retry."));
        Assert.Same(error, await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            publisher.MoveAsync("staging", "destination", new(), default).AsTask()));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(101, 50)]
    [InlineData(20, 0)]
    [InlineData(20, 1001)]
    public void PublicationLimitsRejectUnboundedConfiguration(int attempts, int delay)
    {
        var options = new InstallationOptions { StateDirectory = Path.GetTempPath(),
            ClientDataPublishAttempts = attempts, ClientDataPublishRetryMs = delay };
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), [], true));
    }

    [Fact]
    public async Task FilesRemainIntactWhenAnOpenReaderReleasesPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-publication-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "pending");
        var destination = Path.Combine(root, "published");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "data.xml");
        await File.WriteAllTextAsync(file, "retained-data");
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var conflicts = 0;
        var publisher = new InstallationDirectoryPublisher(Directory.Move, OperatingSystem.IsWindows(), (_, _) =>
        {
            conflicts++;
            reader.Dispose();
            return Task.CompletedTask;
        });
        try
        {
            await publisher.MoveAsync(source, destination, new(), default);
            Assert.Equal("retained-data", await File.ReadAllTextAsync(Path.Combine(destination, "data.xml")));
            Assert.False(Directory.Exists(source));
            if (OperatingSystem.IsWindows()) Assert.True(conflicts > 0);
            else Assert.Equal(0, conflicts);
        }
        finally
        {
            reader.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExistingDestinationIsNeverReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-publication-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "pending");
        var destination = Path.Combine(root, "published");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "data.xml"), "new");
        await File.WriteAllTextAsync(Path.Combine(destination, "data.xml"), "existing");
        var publisher = new InstallationDirectoryPublisher(Directory.Move, OperatingSystem.IsWindows(),
            (_, _) => throw new InvalidOperationException("Existing destination must fail immediately."));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => publisher.MoveAsync(source, destination, new(), default).AsTask());
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(source, "data.xml")));
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "data.xml")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static IOException Failure(int code) => new("Publication failed.", unchecked((int)0x80070000) | code);
}
