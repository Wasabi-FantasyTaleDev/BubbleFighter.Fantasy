using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>
/// The server runs with invariant globalization, so a bare system lookup accepts only the host's own naming
/// system; the resolver must accept both on every host.
/// </summary>
public sealed class TimeZonesTests
{
    [Theory]
    [InlineData("Asia/Seoul")]
    [InlineData("Korea Standard Time")]
    [InlineData("asia/seoul")]
    public void ResolvesKoreanTimeByEitherName(string id)
    {
        var zone = TimeZones.Resolve(id);

        Assert.Equal(TimeSpan.FromHours(9), zone.GetUtcOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("Asia/Shanghai", 8)]
    [InlineData("China Standard Time", 8)]
    [InlineData("Asia/Taipei", 8)]
    [InlineData("Asia/Jakarta", 7)]
    [InlineData("UTC", 0)]
    public void ResolvesTheServiceRegionsZones(string id, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours),
            TimeZones.Resolve(id).GetUtcOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AnUnknownZoneFailsLoud()
    {
        var failure = Assert.Throws<TimeZoneNotFoundException>(() => TimeZones.Resolve("Nowhere/Invented"));

        Assert.Contains("Nowhere/Invented", failure.Message, StringComparison.Ordinal);
    }
}
