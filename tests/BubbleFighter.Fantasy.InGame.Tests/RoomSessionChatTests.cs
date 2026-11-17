using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The waiting-room chat box (legacy C2S gcpRoomSendChatMsg): the client draws nothing locally (kr-812 sub_8145C0,
/// cn-717 sub_899D30), so the speaker must hear the line back, and a team-only line reaches only the speaker's team.
/// </summary>
public sealed class RoomSessionChatTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMenuLineReachesTheMenuPlayersAsASystemLineWithTheNickname(bool enabled)
    {
        // kr-186 speaks in its main, tutorial and lobby menus with the waiting-room packet.
        var rig = new RoomFlowTests.Rig(options: new InGameOptions { MenuSessionChat = enabled, GameProtocol = "test/game" });
        var speaker = RoomFlowTests.Rig.Player(11, "speaker");
        var listener = RoomFlowTests.Rig.Player(22, "listener");
        var seated = RoomFlowTests.Rig.Player(33, "seated");
        try
        {
            speaker.Features.Get<IPlayerSession>().MoveTo(SessionPhase.MainMenu);
            listener.Features.Get<IPlayerSession>().MoveTo(SessionPhase.MainMenu);
            await rig.CreateAsync(seated);
            rig.Flow.Sessions = () => [speaker, listener, seated];

            await rig.Flow.HandleSessionChatAsync(speaker, new(false, "hi"), default);

            if (!enabled)
            {
                Assert.Empty(rig.SessionChat.To(listener));
                return;
            }
            var heard = Assert.Single(rig.SessionChat.To(listener));
            Assert.Equal(heard, Assert.Single(rig.SessionChat.To(speaker)));
            Assert.Equal(-1, heard.Slot);
            Assert.Equal("speaker: hi", heard.Text);
            Assert.Empty(rig.SessionChat.To(seated));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task APlazaLineReachesEveryPlazaPlayerByThePlazaSeat()
    {
        // A build without the chat-module packet (kr-186) speaks in the plaza with the waiting-room packet.
        var rig = new RoomFlowTests.Rig(plaza: true);
        var first = RoomFlowTests.Rig.Player(11, "first");
        var second = RoomFlowTests.Rig.Player(22, "second");
        try
        {
            await rig.Flow.HandlePlazaEnterAsync(first, new(), default);
            await rig.Flow.HandlePlazaEnterAsync(second, new(), default);

            await rig.Flow.HandleSessionChatAsync(second, new(false, "hi"), default);

            var heardByFirst = Assert.Single(rig.SessionChat.To(first));
            var heardBySecond = Assert.Single(rig.SessionChat.To(second));
            Assert.Equal(heardByFirst, heardBySecond);
            Assert.Equal(0, heardByFirst.RoomId);
            Assert.False(heardByFirst.TeamOnly);
            Assert.Equal("hi", heardByFirst.Text);
            Assert.NotEqual(-1, heardByFirst.Slot);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

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
