using System.Buffers.Binary;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The typed round's life rules on the seats' own state: a pop that does not fit the rules scores nothing and
/// sends nothing on any game connection, and a character reset is granted only to a dead, cleaned-up owner
/// whose round is still running. Every case dispatches the client's own C2S frame through the round feed, so
/// the reader's grammar is exercised with the flow's checks.
/// </summary>
public sealed partial class MatchActionRelayTests
{
    [Theory]
    [InlineData("alive")]
    [InlineData("same-team")]
    [InlineData("self")]
    [InlineData("unknown-victim")]
    [InlineData("other-mode")]
    [InlineData("unsuccessful")]
    public async Task InvalidPangDoesNotAwardPoints(string state)
    {
        using var h = await LifeHarnessAsync();
        h.HostSeat.Life = SeatLife.Bubble;
        var body = Pang();
        switch (state)
        {
            case "alive": h.HostSeat.Life = SeatLife.Alive; break;
            case "same-team": h.GuestSeat.Team = h.HostSeat.Team; break;
            case "self": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1100000); break;
            case "unknown-victim": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1000004); break;
            case "other-mode": h.Room.ModeId = 7; break;
            case "unsuccessful": BinaryPrimitives.WriteInt32LittleEndian(body, 0); break;
        }
        await h.DispatchAsync(38277, body, h.Guest);
        Assert.Equal(0, h.Room.Scoring.TeamTotals[0] + h.Room.Scoring.TeamTotals[1]);
        Assert.Empty(h.GuestGame.Sent);
    }

    /// <summary>
    /// The typed grant follows the death presentation (kr-1256 sub_6F5E50), not the match link's state: a dead,
    /// cleaned-up owner is granted even when its relay never finished registration or its main connection already
    /// closed - the relay-era row refused both - because the grant rides the game connection and the pending
    /// state only needs the relay link the confirmation will name.
    /// </summary>
    [Theory]
    [InlineData("alive")]
    [InlineData("bubble")]
    [InlineData("no-cleanup")]
    [InlineData("result")]
    [InlineData("relay-closed")]
    [InlineData("other-mode")]
    [InlineData("relay-sender")]
    [InlineData("uninitialized-relay-still-grants")]
    [InlineData("closed-main-still-grants")]
    public async Task ResetRequiresADeadOwnerWithCleanup(string state)
    {
        using var h = await LifeHarnessAsync();
        h.HostSeat.Life = SeatLife.Dead;
        h.HostSeat.DeathCleaned = true;
        var sender = h.HostGame;
        var body = Convert.FromHexString("40420F00BABA00000000");
        var granted = false;
        switch (state)
        {
            case "alive": h.HostSeat.Life = SeatLife.Alive; break;
            case "bubble": h.HostSeat.Life = SeatLife.Bubble; break;
            case "no-cleanup": h.HostSeat.DeathCleaned = false; break;
            case "result": h.Room.MarkResult(); break;
            case "relay-closed": await h.Host.Connection.CloseAsync("test"); break;
            case "other-mode": h.Room.ModeId = 7; break;
            case "relay-sender": sender = h.Host; break;
            case "uninitialized-relay-still-grants": h.HostSeat.RelayInitialized = false; granted = true; break;
            case "closed-main-still-grants": await h.HostGame.Connection.CloseAsync("test"); granted = true; break;
        }
        await h.DispatchAsync(602, body, sender, "kr-1256/game");
        if (granted)
        {
            Assert.Equal((ushort)632, Assert.Single(h.HostGame.Sent).Opcode);
            Assert.Empty(h.GuestGame.Sent);
        }
        else
        {
            Assert.Empty(h.HostGame.Sent);
            Assert.Empty(h.GuestGame.Sent);
        }
    }

    /// <summary>An actor no seat owns is dropped with a warning, while naming another seat's character is refused loudly.</summary>
    [Fact]
    public async Task AResetNamingAnEntityNoSeatOwnsIsDroppedAndAnotherSeatsCharacterIsRefused()
    {
        using var h = await LifeHarnessAsync();
        h.HostSeat.Life = SeatLife.Dead;
        h.HostSeat.DeathCleaned = true;
        var body = Convert.FromHexString("40420F00BABA00000000");
        BinaryPrimitives.WriteInt32LittleEndian(body, 1000004);
        await h.DispatchAsync(602, body, h.HostGame, "kr-1256/game");
        Assert.Empty(h.HostGame.Sent);
        BinaryPrimitives.WriteInt32LittleEndian(body, 1100000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.DispatchAsync(602, body, h.HostGame, "kr-1256/game"));
        Assert.Empty(h.HostGame.Sent);
    }

    [Fact]
    public async Task NewRoundClearsScoresAndLifeWithoutPublishingOldUpdates()
    {
        using var h = await LifeHarnessAsync();
        h.HostSeat.Life = SeatLife.Bubble;
        h.Host.BeforeSend = () =>
        {
            h.Room.ResetToWaiting(); h.Room.MarkStarting();
            return Task.CompletedTask;
        };
        await h.DispatchAsync(38277, Pang(), h.Guest);
        Assert.Equal(0, h.Room.Scoring.TeamTotals[0] + h.Room.Scoring.TeamTotals[1]);
        Assert.Equal(SeatLife.Alive, h.HostSeat.Life);
        Assert.Empty(h.HostGame.Sent);
    }

    private static byte[] Pang()
    {
        var body = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1000000);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1100000);
        return body;
    }

    private static async Task<Harness> LifeHarnessAsync()
    {
        var h = await Harness.CreateAsync();
        h.Room.ModeId = 1;
        h.HostSeat.Team = 1;
        h.GuestSeat.Team = 2;
        return h;
    }
}
