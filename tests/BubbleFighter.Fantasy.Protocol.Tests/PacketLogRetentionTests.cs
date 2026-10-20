using System.Globalization;
using BubbleFighter.Fantasy.Diagnostics;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Checks timestamped log retention and protection of files outside the sink's filename family.</summary>
public sealed class PacketLogRetentionTests
{
    /// <summary>Reserves the current run's slot and keeps older runs by filename time, including their rotations.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public async Task NewRunKeepsNewestHistoricalRunsAndTheirRotatedSiblings(int maxFiles)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Creation and modification order differ from timestamp order in the filenames.
            int[] order = [7, 2, 9, 1, 5, 8, 3, 6, 4];
            foreach (var day in order)
            {
                var path = HistoricalPath(directory, day);
                await File.WriteAllTextAsync(path, "old run");
                await File.WriteAllTextAsync(path + ".1", "old rotation");
                File.SetLastWriteTimeUtc(path, new DateTime(2000, 2, 10 - day, 0, 0, 0, DateTimeKind.Utc));
            }

            using (var sink = new PacketLogPlugin.RollingFileSink(Path.Combine(directory, "packets_{timestamp}.log"),
                "UTC", 1024, maxFiles))
            {
                await sink.WriteLineAsync(DateTime.UtcNow, "new run");
                await sink.FlushAsync();
            }

            var historicalKept = Math.Min(order.Length, maxFiles - 1);
            for (var day = 1; day <= order.Length; day++)
            {
                var retained = day > order.Length - historicalKept;
                Assert.Equal(retained, File.Exists(HistoricalPath(directory, day)));
                Assert.Equal(retained, File.Exists(HistoricalPath(directory, day) + ".1"));
            }
            Assert.Equal(historicalKept + 1, Directory.EnumerateFiles(directory, "*.log").Count());
            var current = Assert.Single(Directory.EnumerateFiles(directory, "*.log"),
                path => !Path.GetFileName(path).StartsWith("packets_2000", StringComparison.Ordinal));
            Assert.Contains("new run", await File.ReadAllTextAsync(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Deletes only matching timestamped files while protecting malformed names, other formats and directories.</summary>
    [Fact]
    public async Task RetentionLeavesMalformedNamesOtherFamiliesAndDirectoriesUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] protectedNames = ["packets_20001301_000000.log", "packets_not-a-timestamp.log",
                "packets_20000101_000000.log.extra", "other_20000101_000000.log", "packets_20000101_000000.jsonl",
                "PACKETS_20000103_000000.log", "packets_20000104_000000.LOG"];
            foreach (var name in protectedNames)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name), "protected");
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".1"), "protected rotation");
            }
            var child = Path.Combine(directory, "packets_20000102_000000.log");
            Directory.CreateDirectory(child);
            await File.WriteAllTextAsync(Path.Combine(child, "keep.txt"), "protected directory");
            var old = HistoricalPath(directory, 1);
            await File.WriteAllTextAsync(old, "old run");
            await File.WriteAllTextAsync(old + ".1", "old rotation");

            using var sink = new PacketLogPlugin.RollingFileSink(Path.Combine(directory, "packets_{timestamp}.log"),
                "UTC", 1024, 1);

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(old + ".1"));
            foreach (var name in protectedNames)
            {
                Assert.Equal("protected", await File.ReadAllTextAsync(Path.Combine(directory, name)));
                Assert.Equal("protected rotation", await File.ReadAllTextAsync(Path.Combine(directory, name + ".1")));
            }
            Assert.Equal("protected directory", await File.ReadAllTextAsync(Path.Combine(child, "keep.txt")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string HistoricalPath(string directory, int day) => Path.Combine(directory,
        "packets_" + new DateTime(2000, 1, day).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log");
}
