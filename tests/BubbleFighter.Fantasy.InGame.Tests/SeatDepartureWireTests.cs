using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins the room departure and join-failure bodies byte for byte.</summary>
public sealed class SeatDepartureWireTests
{
    // The S2C 511 the host receives for seat 1.
    private const string CapturedVacatedSeat1 =
        "01000000000000000000000000000000FF000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FFFFFFFF000000000000000000000000FFFFFFFF000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000BA00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void VacatedSeatMatchesTheCapturedFrameByteForByte()
    {
        var body = GameToClient.SeatVacated(1);
        Assert.Equal(204, body.Length);
        Assert.Equal(CapturedVacatedSeat1, Convert.ToHexString(body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(20)]
    public void VacatedSeatDiffersOnlyInTheSlotIndex(int slot)
    {
        var body = GameToClient.SeatVacated(slot);
        Assert.Equal(slot, BitConverter.ToInt32(body, 0));
        Assert.Equal(CapturedVacatedSeat1[8..], Convert.ToHexString(body)[8..]);
    }

    [Fact]
    public void MasterChangedIsTheNewMasterSlot()
    {
        // The remaining member at seat 1 becomes master.
        Assert.Equal("01000000", Convert.ToHexString(GameToClient.MasterChanged(1)));
    }

    [Theory]
    [InlineData(GameToClient.JoinResultNoRoom, "0500000000000000")]
    [InlineData(GameToClient.JoinResultRoomFull, "0300000000000000")]
    [InlineData(GameToClient.JoinResultRoomStarted, "0100000000000000")]
    public void JoinResultIsEightBytes(int result, string hex)
    {
        var body = GameToClient.JoinResult(result);
        Assert.Equal(8, body.Length);
        Assert.Equal(hex, Convert.ToHexString(body));
    }
}
