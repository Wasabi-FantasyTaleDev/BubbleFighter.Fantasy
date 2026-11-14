using BubbleFighter.Fantasy.Archives;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Archives.Tests;

/// <summary>
/// Streams every member of every archive in the developer's own client data. Each member carries its checksum in
/// the archive index, so a stream that reaches its end without an error has produced exactly the stored file: this
/// covers the storage variants only real archives have, such as members whose first block alone is encrypted.
/// </summary>
[Trait("Category", "LocalClientData")]
public sealed class LocalArchiveStreamTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryMemberOfEveryLocalArchiveStreamsToItsVerifiedEnd()
    {
        var root = Path.Combine(RepositoryRoot(), "client-data");
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Provision the client data under '{root}'.");
        var buffer = new byte[81_920];
        long members = 0, bytes = 0;
        var archives = 0;
        foreach (var build in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var data = Path.Combine(build, "data");
            if (!Directory.Exists(data))
                continue;
            foreach (var file in Directory.EnumerateFiles(data, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".pkn", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bfz", StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                using var archive = Archive.Open(file, Path.GetRelativePath(data, file).Replace('\\', '/'));
                archives++;
                foreach (var entry in archive.Entries)
                {
                    long read = 0;
                    using (var stream = archive.OpenRead(entry.Path))
                    {
                        for (int got; (got = stream.Read(buffer)) > 0;)
                            read += got;
                    }
                    // Duplicate paths resolve to the last member, which is the one the size was asked for.
                    Assert.Equal(archive.Length(entry.Path), read);
                    members++;
                    bytes += read;
                }
            }
        }
        output.WriteLine($"{archives} archives, {members} members, {bytes / (1024 * 1024)} MB streamed and verified");
        Assert.True(members > 0, "No archive member was found.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
