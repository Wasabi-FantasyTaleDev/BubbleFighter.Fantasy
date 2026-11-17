using System.Buffers.Binary;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class MatchActionRelayTests
{
    /// <summary>Zombie Escape's rescue precursor reaches the victim's bound match link without granting rescue itself.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZombieEscapeRescueInitiationReachesTheVictimWithoutSenderEcho(bool relayInput)
    {
        using var h = await Harness.CreateAsync();
        h.Room.ModeId = 32;
        h.HostSeat.Team = h.GuestSeat.Team = 1;
        Assert.True(h.GuestSeat.Life.Bubble());
        foreach (var value in new byte[] { 0, 1, 3, 4, 0xE7 })
        {
            var body = RescueInitiationBody(1100000, 1000000, value);
            await h.DispatchAsync(33280, body, relayInput ? h.Host : h.HostGame,
                relayInput ? "kr-1256/relay" : "kr-1256/game");
            Assert.Equal((ushort)33280, h.Guest.Sent[^1].Opcode);
            Assert.Equal(body, h.Guest.Sent[^1].Body);
            Assert.Equal(SeatLife.Bubble, h.GuestSeat.Life.State);
            Assert.Equal(0, h.Room.Scoring.Stats(h.HostSeat.Slot).Rescues);
        }
        Assert.Equal(5, h.Guest.Sent.Count);
        Assert.Empty(h.Host.Sent);
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
        Assert.Equal(RoomPhase.Playing, h.Room.Phase);
    }

    /// <summary>A participant-owned precursor is accepted in either actor position, while another seat alone cannot authorize it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescueInitiationChecksBothActorsAndRejectsUnownedReports(bool relayInput)
    {
        using var h = await Harness.CreateAsync();
        var protocol = relayInput ? "kr-1256/relay" : "kr-1256/game";
        var sender = relayInput ? h.Host : h.HostGame;
        await h.DispatchAsync(33280, RescueInitiationBody(1100000, 1100000, 0), sender, protocol);
        await h.DispatchAsync(33280, RescueInitiationBody(-1, -1, 0), sender, protocol);
        await h.DispatchAsync(33280, RescueInitiationBody(1100000, 1000000, 0), new RecordingSession(99), protocol);
        Assert.Empty(h.Host.Sent);
        Assert.Empty(h.Guest.Sent);

        // kr-1256 sub_50A4D0 originates from the rescuer; kr-1256 sub_485BE0 also has a victim-side special-hit path.
        foreach (var body in new[] { RescueInitiationBody(1100000, 1000000, 0), RescueInitiationBody(1000000, 1100000, 3) })
        {
            await h.DispatchAsync(33280, body, sender, protocol);
            Assert.Equal(body, h.Guest.Sent[^1].Body);
        }
        Assert.Equal(2, h.Guest.Sent.Count);
        Assert.Empty(h.Host.Sent);
    }

    /// <summary>Only the victim's subsequent release report changes life and credits a rescue.</summary>
    [Fact]
    public async Task RescueInitiationPrecedesTheVictimsSeparateReleaseReport()
    {
        using var h = await LifeHarnessAsync();
        h.GuestSeat.Team = h.HostSeat.Team;
        Assert.True(h.GuestSeat.Life.Bubble());
        await h.DispatchAsync(33280, RescueInitiationBody(1100000, 1000000, 4), h.Host);
        Assert.Equal((ushort)33280, Assert.Single(h.Guest.Sent).Opcode);
        Assert.Empty(h.Host.Sent);
        Assert.Equal(SeatLife.Bubble, h.GuestSeat.Life.State);
        Assert.Equal(0, h.Room.Scoring.Stats(h.HostSeat.Slot).Rescues);

        var release = Convert.FromHexString("E0C8100040420F000000C03F04");
        await h.DispatchAsync(38273, release, h.Guest);
        Assert.Equal(new ushort[] { 33280, 38273 }, h.Guest.Sent.Select(frame => frame.Opcode));
        Assert.Equal((ushort)38273, Assert.Single(h.Host.Sent).Opcode);
        Assert.Equal(SeatLife.Alive, h.GuestSeat.Life.State);
        Assert.Equal(1, h.Room.Scoring.Stats(h.HostSeat.Slot).Rescues);
    }

    /// <summary>Truncated or overlong precursor bodies fail before reaching any peer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescueInitiationRejectsWrongLengthsBeforePeerDelivery(bool relayInput)
    {
        using var h = await Harness.CreateAsync();
        var protocol = relayInput ? "kr-1256/relay" : "kr-1256/game";
        var sender = relayInput ? h.Host : h.HostGame;
        var body = RescueInitiationBody(1100000, 1000000, 4);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            await Assert.ThrowsAsync<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() =>
                h.DispatchAsync(33280, malformed, sender, protocol));
        Assert.Empty(h.Host.Sent);
        Assert.Empty(h.Guest.Sent);
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
    }

    private static byte[] RescueInitiationBody(int victim, int rescuer, byte unknown)
    {
        var body = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(body, victim);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), rescuer);
        body[8] = unknown;
        return body;
    }
}
