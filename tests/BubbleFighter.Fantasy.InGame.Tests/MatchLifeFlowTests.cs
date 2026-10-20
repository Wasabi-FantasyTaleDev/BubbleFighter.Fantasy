using BubbleFighter.Fantasy.MatchRelay;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class MatchActionRelayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PangUpdatesBothPlayersOnceAndNativeResetAllowsTheNextLife(bool victimReports)
    {
        using var h = await LifeHarnessAsync();
        await h.DispatchAsync(38281, BitConverter.GetBytes(1000000));
        await h.DispatchAsync(38277, Pang(), victimReports ? h.Host : h.Guest);
        await h.DispatchAsync(38277, Pang(), h.Guest);
        Assert.Equal((ushort)1, h.Room.RedScore);
        Assert.Equal(CharacterLife.Dead, h.HostState.Life);
        foreach (var game in new[] { h.HostGame, h.GuestGame })
        {
            Assert.Equal(new ushort[] { 985, 985 }, game.Sent.Select(p => p.Opcode));
            Assert.Equal(Convert.FromHexString("020100FFFF000000000000000000000000"), game.Sent[1].Body);
        }
        await h.DispatchAsync(32836, BitConverter.GetBytes(1000000));
        var request = Convert.FromHexString("40420F00BABA00000000");
        await h.DispatchAsync(602, request, h.HostGame, "kr-1256/game");
        Assert.Equal((ushort)632, h.HostGame.Sent.Last().Opcode);
        Assert.Equal(Convert.FromHexString("40420F00BABA0000000000000000"), h.HostGame.Sent.Last().Body);
        Assert.Equal(CharacterLife.ResetPending, h.HostState.Life);
        await h.DispatchAsync(602, request, h.HostGame, "kr-1256/game");
        Assert.Single(h.HostGame.Sent, p => p.Opcode == 632);
        await h.DispatchAsync(32926, ResetBody(false, false));
        Assert.Equal(CharacterLife.Alive, h.HostState.Life);
        Assert.False(h.HostState.DeathCleanupReceived);
        Assert.Equal((ushort)32926, h.Guest.Sent.Last().Opcode);
        await h.DispatchAsync(38281, BitConverter.GetBytes(1000000));
        await h.DispatchAsync(38277, Pang(), h.Guest);
        Assert.Equal((ushort)2, h.Room.RedScore);
    }

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
        h.HostState.Life = CharacterLife.Bubble;
        var body = Pang();
        switch (state)
        {
            case "alive": h.HostState.Life = CharacterLife.Alive; break;
            case "same-team": h.GuestSeat.Team = h.HostSeat.Team; break;
            case "self": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1100000); break;
            case "unknown-victim": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1000004); break;
            case "other-mode": h.Room.ModeId = 7; break;
            case "unsuccessful": BinaryPrimitives.WriteInt32LittleEndian(body, 0); break;
        }
        await h.DispatchAsync(38277, body, h.Guest);
        Assert.Equal((ushort)0, h.Room.RedScore);
        Assert.Empty(h.GuestGame.Sent);
    }

    [Theory]
    [InlineData("alive")]
    [InlineData("bubble")]
    [InlineData("no-cleanup")]
    [InlineData("result")]
    [InlineData("relay-uninitialized")]
    [InlineData("relay-closed")]
    [InlineData("main-closed")]
    [InlineData("other-mode")]
    [InlineData("relay-sender")]
    [InlineData("unknown-entity")]
    public async Task ResetRequiresAnInitializedDeadOwnerWithCleanup(string state)
    {
        using var h = await LifeHarnessAsync();
        h.HostState.Life = CharacterLife.Dead;
        h.HostState.DeathCleanupReceived = true;
        var sender = h.HostGame;
        var body = Convert.FromHexString("40420F00BABA00000000");
        switch (state)
        {
            case "alive": h.HostState.Life = CharacterLife.Alive; break;
            case "bubble": h.HostState.Life = CharacterLife.Bubble; break;
            case "no-cleanup": h.HostState.DeathCleanupReceived = false; break;
            case "result": h.Room.MarkResult(); break;
            case "relay-uninitialized": h.HostSeat.RelayInitialized = false; break;
            case "relay-closed": await h.Host.Connection.CloseAsync("test"); break;
            case "main-closed": await h.HostGame.Connection.CloseAsync("test"); break;
            case "other-mode": h.Room.ModeId = 7; break;
            case "relay-sender": sender = h.Host; break;
            case "unknown-entity": BinaryPrimitives.WriteInt32LittleEndian(body, 1000004); break;
        }
        await h.DispatchAsync(602, body, sender, "kr-1256/game");
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
    }

    [Fact]
    public async Task ResetCannotImpersonateAnotherRegisteredPlayer()
    {
        using var h = await LifeHarnessAsync();
        await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync(602,
            Convert.FromHexString("E0C81000BABA00000000"), h.HostGame, "kr-1256/game"));
    }

    [Theory]
    [InlineData(false, false, 14)]
    [InlineData(true, false, 18)]
    [InlineData(false, true, 18)]
    [InlineData(true, true, 22)]
    public void ResetCodecPinsAllOptionalCombinationsAndRejectsMalformedBodies(bool first, bool second, int replyLength)
    {
        var action = ResetBody(first, second);
        var request = action.Where((_, index) => index != action.Length - 5).ToArray();
        Assert.Equal(1000000, MatchLife1256.ReadReset(request));
        var reply = MatchLife1256.ResetReplyForTimedScore(request);
        Assert.Equal(replyLength, reply.Length);
        Assert.Equal(request, reply[..^4]);
        Assert.Equal(new byte[4], reply[^4..]);
        for (var length = 0; length < request.Length; length++)
            Assert.Throws<PacketFormatException>(() => MatchLife1256.ReadReset(request[..length]));
        Assert.Throws<PacketFormatException>(() => MatchLife1256.ReadReset([.. request, 0]));
        request[4] = 0;
        Assert.Throws<PacketFormatException>(() => MatchLife1256.ReadReset(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResetCanRetryWithoutRollingBackANewerRound(bool newRound)
    {
        using var h = await LifeHarnessAsync();
        h.HostState.Life = CharacterLife.Dead;
        h.HostState.DeathCleanupReceived = true;
        h.HostGame.BeforeSend = async () =>
        {
            await Task.Yield();
            if (newRound) { h.Room.ResetToWaiting(); h.Room.MarkStarting(); }
            throw new IOException("test reset failure");
        };
        var request = Convert.FromHexString("40420F00BABA00000000");
        await Assert.ThrowsAsync<IOException>(() => h.DispatchAsync(602, request, h.HostGame, "kr-1256/game"));
        Assert.Equal(newRound ? CharacterLife.Alive : CharacterLife.Dead, h.HostState.Life);
        if (!newRound)
        {
            h.HostGame.BeforeSend = null;
            await h.DispatchAsync(602, request, h.HostGame, "kr-1256/game");
            Assert.Single(h.HostGame.Sent);
        }
    }

    [Fact]
    public async Task PendingResetCanBeRetriedAfterMatchTransportReplacement()
    {
        using var h = await LifeHarnessAsync();
        h.HostState.Life = CharacterLife.Dead;
        h.HostState.DeathCleanupReceived = true;
        var request = Convert.FromHexString("40420F00BABA00000000");
        await h.DispatchAsync(602, request, h.HostGame, "kr-1256/game");
        var replacement = await h.BindAsync(h.HostSeat, 60);
        // The reset granted on the replaced transport is void, so the same request is granted again.
        await h.DispatchAsync(602, request, h.HostGame, "kr-1256/game");
        Assert.Equal(2, h.HostGame.Sent.Count);
        await h.DispatchAsync(32926, ResetBody(false, false), replacement);
        Assert.Equal(CharacterLife.Alive, h.HostState.Life);
    }

    [Fact]
    public async Task SenderDepartureDuringPangFanoutDoesNotHideTheCommittedScore()
    {
        using var h = await LifeHarnessAsync();
        h.HostState.Life = CharacterLife.Bubble;
        h.Host.BeforeSend = () => { h.Registry.Release(h.Guest); return Task.CompletedTask; };
        await h.DispatchAsync(38277, Pang(), h.Guest);
        Assert.Equal((ushort)1, h.Room.RedScore);
        Assert.Equal(2, h.HostGame.Sent.Count);
        Assert.Equal(2, h.GuestGame.Sent.Count);
    }

    [Fact]
    public async Task NewRoundClearsScoresAndLifeWithoutPublishingOldUpdates()
    {
        using var h = await LifeHarnessAsync();
        h.HostState.Life = CharacterLife.Bubble;
        h.Host.BeforeSend = () =>
        {
            h.Room.ResetToWaiting(); h.Room.MarkStarting();
            return Task.CompletedTask;
        };
        await h.DispatchAsync(38277, Pang(), h.Guest);
        Assert.Equal((ushort)0, h.Room.RedScore);
        Assert.Equal(CharacterLife.Alive, h.HostState.Life);
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
