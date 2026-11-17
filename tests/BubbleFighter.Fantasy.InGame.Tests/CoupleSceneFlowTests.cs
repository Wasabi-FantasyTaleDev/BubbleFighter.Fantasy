using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Relationship changes update only already installed native scene members.</summary>
public sealed partial class CoupleSceneFlowTests
{
    [Fact]
    public async Task RoomEntryAndChangesKeepTheCommittedPartnerWithoutRecreatingSeats()
    {
        await using var rig = new Rig();
        var host = Player(11, "host", new(22, "guest"));
        var guest = Player(22, "guest", new(11, "host"));
        var id = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
        Assert.Equal(new CoupleSceneState(22, "guest"), rig.Entered.To(guest).Single().Room.Seats.Single(s => s.UserNum == 11).Couple);
        Assert.Equal(new CoupleSceneState(11, "host"), rig.SeatUpdated.To(host).Single().Seat.Couple);
        rig.Entered.Clear();
        rig.SeatUpdated.Clear();

        await rig.ChangeAsync(host, null);

        Assert.Null(Assert.Single(rig.RoomCouple.To(host)).Member.Couple);
        Assert.Null(Assert.Single(rig.RoomCouple.To(guest)).Member.Couple);
        Assert.Empty(rig.Entered.All());
        Assert.Empty(rig.SeatUpdated.All());
        var later = Player(33, "later");
        await rig.Flow.HandleJoinAsync(later, new(id, ""), default);
        Assert.Null(rig.Entered.To(later).Single().Room.Seats.Single(s => s.UserNum == 11).Couple);
    }

    [Fact]
    public async Task PlazaChangeKeepsTheActorAndMovementAndUpdatesLaterEntrants()
    {
        await using var rig = new Rig();
        var owner = Player(11, "owner");
        var peer = Player(22, "peer");
        await rig.Flow.HandlePlazaEnterAsync(owner, new(), default);
        await rig.Flow.HandlePlazaEnterAsync(peer, new(), default);
        await rig.Flow.HandlePlazaPositionAsync(owner, new(new byte[] { 1, 2, 3 }), default);
        var position = Assert.Single(rig.Position.To(peer));
        var entry = rig.PlazaEntered.To(peer).Single().Members.Single();
        rig.MemberEntered.Clear();
        rig.Position.Clear();
        var link = new Link();
        using var binding = rig.Flow.BindPlazaFederation(link);
        link.Changes.Clear();

        await rig.ChangeAsync(owner, new(22, "peer"));

        var updated = Assert.Single(rig.PlazaCouple.To(peer)).Member;
        Assert.Equal(entry.EntrySeed, updated.EntrySeed);
        Assert.Equal(new CoupleSceneState(22, "peer"), updated.Couple);
        Assert.Single(rig.PlazaCouple.To(owner));
        Assert.Empty(rig.MemberEntered.All());
        Assert.Empty(rig.Position.All());
        Assert.Equal(updated.Couple, Assert.Single(link.Changes).Member!.Couple);
        var later = Player(33, "later");
        await rig.Flow.HandlePlazaEnterAsync(later, new(), default);
        Assert.Equal(updated.Couple, rig.PlazaEntered.To(later).Single().Members.Single(s => s.UserNumber == 11).Couple);
        Assert.Equal(position, Assert.Single(rig.Position.To(later)));
    }

    [Fact]
    public async Task FailedFullRoomInstallCannotAuthorizeANarrowUpdateForEitherSide()
    {
        await using var rig = new Rig();
        var host = Player(11, "host");
        var guest = Player(22, "guest");
        var id = await rig.CreateAsync(host);
        rig.Entered.FailSession = guest.Id;
        await Assert.ThrowsAsync<IOException>(() => rig.Flow.HandleJoinAsync(guest, new(id, ""), default).AsTask());
        rig.Entered.FailSession = null;

        await rig.ChangeAsync(guest, new(11, "host"));

        Assert.Empty(rig.RoomCouple.All());
        // The guest is retained server-side but neither observer received its complete installation.
        await rig.CreateAsync(guest);
        await rig.ChangeAsync(guest, null);
        Assert.Single(rig.RoomCouple.To(guest));
        Assert.Empty(rig.RoomCouple.To(host));
        await rig.CreateAsync(host);
        await rig.ChangeAsync(guest, new(11, "host"));
        Assert.Single(rig.RoomCouple.To(host));
    }

    [Fact]
    public async Task EventInsideRoomWorkIsDeferredAndOldStateCannotOverrideNewState()
    {
        await using var rig = new Rig();
        var host = Player(11, "host");
        var peer = Player(22, "peer");
        var id = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(peer, new(id, ""), default);
        var room = rig.Registry.RequireGame(host).Room;
        await room.Executor.ExecuteAsync(_ =>
        {
            Profile(host).Scene = new(22, "peer");
            rig.Flow.OnCoupleSceneChanged(new(host, Profile(host).Scene));
            Profile(host).Scene = null;
            rig.Flow.OnCoupleSceneChanged(new(host, null));
            return ValueTask.CompletedTask;
        });
        await rig.Flow.FlushCoupleChangesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(rig.RoomCouple.To(peer));
        Assert.All(rig.RoomCouple.All(), update => Assert.Null(update.Message.Member.Couple));
    }

    [Fact]
    public async Task DepartedAndReplacementSessionsDoNotInheritAnInstalledRoster()
    {
        await using var rig = new Rig();
        var owner = Player(11, "owner");
        var original = Player(22, "peer");
        await rig.Flow.HandlePlazaEnterAsync(owner, new(), default);
        await rig.Flow.HandlePlazaEnterAsync(original, new(), default);
        await rig.Flow.HandlePlazaMenuReturnAsync(original, new(), default);
        await rig.ChangeAsync(owner, new(22, "peer"));
        Assert.Empty(rig.PlazaCouple.To(original));
        var replacement = Player(22, "peer");
        rig.PlazaEntered.FailSession = replacement.Id;
        await Assert.ThrowsAsync<IOException>(() => rig.Flow.HandlePlazaEnterAsync(replacement, new(), default).AsTask());
        await rig.ChangeAsync(owner, null);
        Assert.Empty(rig.PlazaCouple.To(replacement));
        rig.PlazaEntered.FailSession = null;
        await rig.Flow.HandlePlazaEnterAsync(replacement, new(), default);
        await rig.ChangeAsync(owner, new(22, "peer"));
        Assert.Single(rig.PlazaCouple.To(replacement));
    }

    [Fact]
    public async Task StaleRelationshipEventIsIgnoredAfterASceneTransition()
    {
        await using var rig = new Rig();
        var owner = Player(11, "owner");
        var plazaPeer = Player(22, "plaza");
        await rig.Flow.HandlePlazaEnterAsync(owner, new(), default);
        await rig.Flow.HandlePlazaEnterAsync(plazaPeer, new(), default);
        var id = await rig.CreateAsync(owner);
        var roomPeer = Player(33, "room");
        await rig.Flow.HandleJoinAsync(roomPeer, new(id, ""), default);
        Profile(owner).Scene = new(33, "room");
        rig.Flow.OnCoupleSceneChanged(new(owner, new(22, "plaza")));
        await rig.Flow.FlushCoupleChangesAsync();
        Assert.Empty(rig.RoomCouple.All());
        Assert.Empty(rig.PlazaCouple.All());
        await rig.ChangeAsync(owner, new(33, "room"));
        Assert.Single(rig.RoomCouple.To(roomPeer));
        Assert.Empty(rig.PlazaCouple.To(plazaPeer));
    }

    [Fact]
    public async Task BuildWithoutCoupleSendersDoesNotScheduleScenePublications()
    {
        await using var rig = new Rig(couples: false);
        var owner = Player(11, "owner");
        await rig.CreateAsync(owner);
        await rig.ChangeAsync(owner, new(22, "peer"));
        Assert.False(rig.Flow.CoupleScenesBound);
        Assert.Empty(rig.RoomCouple.All());
    }

    [Fact]
    public async Task LoginTakeoverRejectsTheOldConnectionBeforeItsSeatIsRemoved()
    {
        var online = new OnlineDirectory();
        await using var rig = new Rig(online: online);
        var owner = Player(11, "owner");
        var peer = Player(22, "peer");
        online.Sessions[11] = owner;
        online.Sessions[22] = peer;
        var id = await rig.CreateAsync(owner);
        await rig.Flow.HandleJoinAsync(peer, new(id, ""), default);
        var replacement = Player(11, "owner");
        online.Sessions[11] = replacement;

        await rig.ChangeAsync(owner, new(22, "peer"));
        await rig.ChangeAsync(replacement, new(22, "peer"));

        Assert.Empty(rig.RoomCouple.All());
        Assert.Same(owner, rig.Registry.RequireGame(owner).Seat.Game);
    }
}
