using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Direct byte-length pin for S2C 505 <see cref="RoomSessionBlob.RoomMapUpdate"/> (A1 gap).
/// The codec XML states the body is exactly one <c>MapMeta</c>; the empty form is the
/// documented 155-byte all-zero minimum already pinned for <see cref="MapMetaBlob"/>.
///
/// </summary>
public sealed class RoomMapUpdateCodecLengthTests
{
    [Fact]
    public void RoomMapUpdatePinsTheOneHundredFiftyFiveByteEmptyMapMeta()
    {
        // RoomMapUpdate XML: "exactly one MapMeta". Empty MapMeta is the 155-byte all-zero form (see
        // MapMetaBlobPinsTheOneHundredFiftyFiveByteAllZeroMinimum).
        var payload = RoomSessionBlob.RoomMapUpdate(MapMetaBlob.Empty());
        Assert.Equal(155, payload.Length);
        Assert.Equal(new byte[155], payload);
    }
}
