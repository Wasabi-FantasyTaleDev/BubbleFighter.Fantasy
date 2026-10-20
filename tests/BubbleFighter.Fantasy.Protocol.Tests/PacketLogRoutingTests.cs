using BubbleFighter.Fantasy.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class PacketLogRoutingTests
{
    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    public void PacketFilesRemainCompleteWithoutEnteringTheConsoleProvider(LogLevel level)
    {
        var logger = new RejectingLogger();
        var lines = new List<string>();
        for (var i = 0; i < 10000; i++)
            PacketLogPlugin.WriteLog(logger, lines.Add, false, true, level, $"packet {i}");
        Assert.Equal(10000, lines.Count);
        Assert.Equal("packet 9999", lines[^1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ConnectionEventsAndExplicitConsoleTrafficKeepTheirLogLevel(bool consolePackets, bool packet)
    {
        var logger = new RecordingLogger();
        var lines = new List<string>();
        PacketLogPlugin.WriteLog(logger, lines.Add, consolePackets, packet, LogLevel.Warning, "event");
        Assert.Equal(["event"], lines);
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Equal("event", logger.Message);
    }

    private sealed class RejectingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("A packet entered the slow console path.");
    }

    private sealed class RecordingLogger : ILogger
    {
        internal LogLevel Level { get; private set; }
        internal string? Message { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
        }
    }
}
