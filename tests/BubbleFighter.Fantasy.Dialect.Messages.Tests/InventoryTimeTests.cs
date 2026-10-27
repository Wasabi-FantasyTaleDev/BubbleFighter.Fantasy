using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class InventoryTimeTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncrementalUpdateKeepsOriginalBaselineAndClientSubtractsElapsedTimeOnce()
    {
        var expiry = InventoryLifetime.Until(Baseline.AddDays(7));
        var payloadDays = InventoryTime.ExpiringDaysAtBaseline(expiry, Baseline);
        var clientElapsedDays = 0.5;
        Assert.Equal(7, payloadDays);
        Assert.Equal(6.5, payloadDays - clientElapsedDays);
        // A new full snapshot on reconnect establishes a fresh origin, but never extends ownership.
        Assert.Equal(6.5, InventoryTime.ExpiringDaysAtBaseline(expiry, Baseline.AddDays(0.5)));
    }

    [Fact]
    public void ExpiredDeadlineStaysExpiredAndOffsetDoesNotChangeWireTime()
    {
        var expiry = InventoryLifetime.Until(Baseline.AddHours(-12));
        Assert.Equal(-0.5, InventoryTime.ExpiringDaysAtBaseline(expiry, Baseline));
        Assert.Equal(-0.5, InventoryTime.ExpiringDaysAtBaseline(expiry, Baseline.ToOffset(TimeSpan.FromHours(9))));
    }

    [Fact]
    public void UnknownAndPermanentTermsCannotAccidentallyBecomeFiniteTimers()
    {
        Assert.Throws<ArgumentException>(() => InventoryTime.ExpiringDaysAtBaseline(default, Baseline));
        Assert.Throws<ArgumentException>(() => InventoryTime.ExpiringDaysAtBaseline(InventoryLifetime.Permanent, Baseline));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryTime.ExpiringDaysAtBaseline(
            InventoryLifetime.Until(DateTimeOffset.MaxValue), DateTimeOffset.MinValue));
    }
}
