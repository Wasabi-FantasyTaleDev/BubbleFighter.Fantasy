using BubbleFighter.Fantasy.Admin.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class LogRingTests
{
    [Fact]
    public void RingKeepsOnlyTheNewestLines()
    {
        var ring = new LogRing(3);
        for (var i = 1; i <= 10; i++)
            ring.Append(LogLevel.Information, "t", $"line {i}");

        var (entries, next) = ring.Read(0, 100);
        Assert.Equal([8L, 9, 10], entries.Select(e => e.Seq));
        Assert.Equal("line 8", entries[0].Message);
        Assert.Equal(10, next);
        Assert.Equal(3, ring.Capacity);
    }

    [Fact]
    public void ReadingFromACursorReturnsOnlyNewerLinesAndLimitsTheBatch()
    {
        var ring = new LogRing(100);
        for (var i = 1; i <= 10; i++)
            ring.Append(LogLevel.Information, "t", $"line {i}");

        var (page, next) = ring.Read(2, 3);
        Assert.Equal([3L, 4, 5], page.Select(e => e.Seq));
        Assert.Equal(5, next);
        var (rest, last) = ring.Read(next, 100);
        Assert.Equal(5, rest.Count);
        Assert.Equal(10, last);
        var (none, same) = ring.Read(last, 100);
        Assert.Empty(none);
        Assert.Equal(10, same);
    }

    [Fact]
    public void ACursorBeyondTheNewestLineRestartsFromTheBeginning()
    {
        var ring = new LogRing(5);
        ring.Append(LogLevel.Warning, "t", "only");
        var (entries, next) = ring.Read(999, 10);
        Assert.Single(entries);
        Assert.Equal(1, next);
    }

    [Fact]
    public void LevelsMapToTheNamesTheInterfaceFilters()
    {
        var ring = new LogRing(8);
        ring.Append(LogLevel.Trace, "t", "a");
        ring.Append(LogLevel.Debug, "t", "b");
        ring.Append(LogLevel.Information, "t", "c");
        ring.Append(LogLevel.Warning, "t", "d");
        ring.Append(LogLevel.Error, "t", "e");
        ring.Append(LogLevel.Critical, "t", "f");
        Assert.Equal(["debug", "debug", "info", "warn", "error", "error"], ring.Read(0, 10).Entries.Select(e => e.Level));
    }

    [Fact]
    public void OverlongMessagesAreCut()
    {
        var ring = new LogRing(2);
        ring.Append(LogLevel.Information, "t", new string('x', LogRing.MaxMessageLength * 2));
        var message = ring.Read(0, 1).Entries[0].Message;
        Assert.Equal(LogRing.MaxMessageLength, message.Length);
        Assert.EndsWith("…", message);
    }

    [Fact]
    public void ACapacityBelowOneIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRing(0));
    }

    [Fact]
    public async Task ConcurrentAppendsNeverExceedTheCapacityOrReuseASequence()
    {
        var ring = new LogRing(50);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                ring.Append(LogLevel.Information, "t", "x");
        })));
        var (entries, next) = ring.Read(0, 1000);
        Assert.Equal(50, entries.Count);
        Assert.Equal(4000, next);
        Assert.Equal(Enumerable.Range(3951, 50).Select(i => (long)i), entries.Select(e => e.Seq));
    }
}
