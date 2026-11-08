using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The waiting-room chat box (legacy C2S gcpRoomSendChatMsg): the client draws nothing locally (kr-812 sub_8145C0,
/// cn-717 sub_899D30), so the speaker must hear the line back, and a team-only line reaches only the speaker's team.
/// </summary>
public sealed class RoomSessionChatTests
{
    [Fact]
    public async Task AnOpenLineReachesEveryMemberIncludingTheSpeaker()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            await rig.Flow.HandleSessionChatAsync(guest, new(false, "hello"), default);

            var room = rig.Registry.Find(id)!;
            var speaker = room.Seats.Single(seat => seat.Game.Id == guest.Id);
            Assert.Equal(new RoomSessionChatReceived(id, speaker.Slot, false, "hello"), Assert.Single(rig.SessionChat.To(host)));
            Assert.Equal(new RoomSessionChatReceived(id, speaker.Slot, false, "hello"), Assert.Single(rig.SessionChat.To(guest)));
            Assert.Empty(rig.Chat.Sent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task ATeamLineReachesOnlyTheSpeakersTeam()
    {
        var rig = new RoomFlowTests.Rig();
        var players = new[] { RoomFlowTests.Rig.Player(11, "a"), RoomFlowTests.Rig.Player(22, "b"), RoomFlowTests.Rig.Player(33, "c") };
        try
        {
            var id = await rig.CreateAsync(players[0]);
            await rig.Flow.HandleJoinAsync(players[1], new(id, ""), default);
            await rig.Flow.HandleJoinAsync(players[2], new(id, ""), default);
            var room = rig.Registry.Find(id)!;
            var speaker = room.Seats.Single(seat => seat.Game.Id == players[0].Id);
            lock (room.Gate)
                room.FindByGame(players[2].Id)!.Team = (byte)(speaker.Team == 2 ? 1 : 2);
            Assert.Contains(room.Seats, seat => seat.Team != speaker.Team);

            await rig.Flow.HandleSessionChatAsync(players[0], new(true, "push"), default);

            foreach (var seat in room.Seats)
            {
                var heard = rig.SessionChat.To(players.Single(player => player.Id == seat.Game.Id));
                if (seat.Team == speaker.Team)
                    Assert.Equal(new RoomSessionChatReceived(id, speaker.Slot, true, "push"), Assert.Single(heard));
                else
                    Assert.Empty(heard);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task ASpeakerWithoutASeatIsNotAnswered()
    {
        var rig = new RoomFlowTests.Rig();
        var stranger = RoomFlowTests.Rig.Player(44, "stranger");
        await rig.Flow.HandleSessionChatAsync(stranger, new(false, "anyone"), default);
        Assert.Empty(rig.SessionChat.Sent);
        await rig.Registry.StopAllAsync();
    }
}
