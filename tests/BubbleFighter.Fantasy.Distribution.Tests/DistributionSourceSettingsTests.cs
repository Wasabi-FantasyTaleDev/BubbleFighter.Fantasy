using BubbleFighter.Fantasy.Contracts.Distribution;
using Xunit;

namespace BubbleFighter.Fantasy.Distribution.Tests;

public sealed class DistributionSourceSettingsTests
{
    private static readonly DistributionSource One = new("one", "First", "http", "https://one.example/", "source.toml");
    private static readonly DistributionSource Two = new("two", "Second", "github", "https://github.com/owner/store/", "source.toml");

    [Fact]
    public async Task SourceEditsAndOrderSurviveRestartWithoutChangingYamlDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-distribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient();
            var options = new DistributionOptions { StatePath = Path.Combine(directory, "sources.json") };
            var service = new DistributionSources(http, options);
            Assert.Equal(0, service.Settings.Revision);
            var first = await service.SaveSourcesAsync(0, [One, Two]);
            Assert.Equal(1, first.Revision);
            var second = await service.SaveSourcesAsync(1, [Two, One]);
            Assert.Equal(new[] { "two", "one" }, second.Sources.Select(source => source.Id));
            var reopened = new DistributionSources(http, options);
            Assert.Equal(2, reopened.Settings.Revision);
            Assert.Equal(second.Sources, reopened.Sources);
            Assert.Empty(options.Sources);
            await reopened.SaveSourcesAsync(2, [One]);
            Assert.Single(new DistributionSources(http, options).Sources);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StaleOrInvalidEditCannotReplaceSavedSourceSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-distribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient();
            var options = new DistributionOptions { StatePath = Path.Combine(directory, "sources.json") };
            var service = new DistributionSources(http, options);
            await service.SaveSourcesAsync(0, [One]);
            var before = await File.ReadAllBytesAsync(options.StatePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSourcesAsync(0, [Two]).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSourcesAsync(1, [Two with { BaseUrl = "https://user:secret@github.com/owner/store" }]).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSourcesAsync(1, [One, One]).AsTask());
            Assert.Equal(before, await File.ReadAllBytesAsync(options.StatePath));
            Assert.Equal(One, Assert.Single(service.Sources));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedDiskWriteLeavesCurrentSettingsAndRevisionUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-distribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blockedParent = Path.Combine(directory, "file-instead-of-folder");
            await File.WriteAllTextAsync(blockedParent, "unrelated data");
            using var http = new HttpClient();
            var service = new DistributionSources(http, new() { StatePath = Path.Combine(blockedParent, "sources.json") });
            await Assert.ThrowsAnyAsync<IOException>(() => service.SaveSourcesAsync(0, [One]).AsTask());
            Assert.Equal(0, service.Settings.Revision);
            Assert.Empty(service.Sources);
            Assert.Equal("unrelated data", await File.ReadAllTextAsync(blockedParent));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
