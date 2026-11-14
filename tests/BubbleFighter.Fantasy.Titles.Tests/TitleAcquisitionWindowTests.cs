using BubbleFighter.Fantasy.Titles.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Checks the native annual acquisition window without confusing it with a title's item expiry.</summary>
public sealed class TitleAcquisitionWindowTests
{
    /// <summary>Native acquisition uses inclusive local minute bounds and ignores the resource's year.</summary>
    [Theory]
    [InlineData(34, false)]
    [InlineData(35, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void NativeMinuteBoundsIgnoreYear(int minute, bool expected)
    {
        var window = TitleAcquisitionWindow.Parse("2012/03/28/16:35~2012/03/28/16:50")!;
        Assert.Equal(expected, window.Contains(new(2026, 3, 28, 16, minute, 59, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    /// <summary>The configured zone, rather than the host's ambient timezone, determines acquisition eligibility.</summary>
    [Fact]
    public void ExplicitTimeZoneControlsTheWindow()
    {
        var window = TitleAcquisitionWindow.Parse("2012/03/28/16:35~2012/03/28/16:50")!;
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-eight", TimeSpan.FromHours(8), "test", "test");
        var instant = new DateTimeOffset(2026, 3, 28, 8, 40, 0, TimeSpan.Zero);
        Assert.True(window.Contains(instant, zone));
        Assert.False(window.Contains(instant, TimeZoneInfo.Utc));
    }

    /// <summary>Invalid resource dates fail parsing; an end before the start does not invent a year-wrapping window.</summary>
    [Fact]
    public void MalformedAndReversedWindowsDoNotExpandAdmission()
    {
        Assert.Throws<InvalidDataException>(() => TitleAcquisitionWindow.Parse("2012/02/30/16:35~2012/03/28/16:50"));
        var reversed = TitleAcquisitionWindow.Parse("2012/12/31/00:00~2013/01/01/23:59")!;
        Assert.False(reversed.Contains(new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }
}
