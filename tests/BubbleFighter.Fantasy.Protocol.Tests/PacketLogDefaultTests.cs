using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Diagnostics;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>The packet log stays off unless asked for, and asking for it still works.</summary>
public sealed class PacketLogDefaultTests
{
    /// <summary>
    /// A row left at its defaults opens no file sink, even with a file path configured; turning packet lines on opens
    /// the sink again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PacketLinesOpenTheFileSinkOnlyWhenEnabled(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "bff-packet-log-default");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var config = new Dictionary<string, object?>
        {
            ["file"] = Path.Combine(directory, "packets_{timestamp}.log"),
            ["catalog_directory"] = "",
            ["file_time_zone"] = "UTC",
        };
        if (enabled) config["packets_enabled"] = true;
        var rows = new List<PluginRow> { new() { Id = "packet-log", Plugin = "diagnostics.packet-log", Source = "test", Config = config } };
        var host = new PluginHost(rows, PluginCatalog.ScanAssemblies([typeof(PacketLogPlugin).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            Assert.Equal(enabled, Directory.Exists(directory));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
