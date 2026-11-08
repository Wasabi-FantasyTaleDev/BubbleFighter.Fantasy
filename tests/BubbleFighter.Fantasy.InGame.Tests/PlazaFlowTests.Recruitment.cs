using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class PlazaFlowTests
{
    /// <summary>A separate text request remains pending until start, then cancellation clears the visible sign.</summary>
    [Fact]
    public async Task RecruitmentTextThenTogglePublishesAndCancelsForAllPeers()
    {
        await using var setup = new RecruitmentRig();
        var leader = setup.Player(11);
        var peer = setup.Player(22);
        await EnterAsync(setup.Scenes, leader);
        await EnterAsync(setup.Scenes, peer);
        await setup.Scenes.Flow.HandlePlazaRecruitmentTextAsync(leader, new(" 123321 "), default);
        Assert.Empty(setup.Changed.Sent);
        Assert.False(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(true), default);
        Assert.True(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
        Assert.Equal("123321", Assert.Single(setup.Text.To(peer)).Text);
        Assert.Equal(new PlazaRecruitmentState(true, true, "123321") { MemberLimit = 12 },
            Assert.Single(setup.Changed.To(leader)).Member.Recruitment);
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(false), default);
        Assert.False(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
        Assert.False(setup.Changed.To(peer)[^1].Member.Recruitment!.Active);
        Assert.Equal(string.Empty, setup.Changed.To(peer)[^1].Member.Recruitment!.Text);
    }

    /// <summary>A member cannot recruit; handover and explicit departure revoke an old leader's sign.</summary>
    [Fact]
    public async Task MembershipChangesRefreshLeaderOnlyRecruitment()
    {
        await using var setup = new RecruitmentRig();
        var leader = setup.Player(11);
        var follower = setup.Player(22);
        await EnterAsync(setup.Scenes, leader);
        await EnterAsync(setup.Scenes, follower);
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(true, "join"), default);
        setup.Directory.Party = new([new(11, "player11"), new(22, "player22")]);
        await setup.Scenes.Flow.RefreshPlazaPartyMembershipAsync(new("test/game"), default);
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(follower, new(true, "bad"), default);
        Assert.Single(setup.Rejected.To(follower));
        Assert.False(setup.Changed.To(follower)[^1].Member.Recruitment!.Allowed);
        setup.Directory.Party = new([new(22, "player22"), new(11, "player11")]);
        await setup.Scenes.Flow.RefreshPlazaPartyMembershipAsync(new("test/game"), default);
        Assert.False(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(follower, new(true, "next"), default);
        setup.Directory.Party = null;
        await setup.Scenes.Flow.RefreshPlazaPartyMembershipAsync(new("test/game") { DepartedUsers = [22] }, default);
        Assert.False(follower.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
        Assert.True(setup.Changed.To(follower)[^1].Member.Recruitment!.Allowed);
    }

    /// <summary>Invalid document settings and replaced connections cannot change the installed recruitment state.</summary>
    [Fact]
    public async Task RecruitmentRejectsInvalidDocumentsAndStaleConnections()
    {
        await using var setup = new RecruitmentRig();
        var leader = setup.Player(11);
        await EnterAsync(setup.Scenes, leader);
        PlazaRecruitmentRequested[] invalid =
        [
            new(true, ""), new(true, new string('x', 129)), new(true, "line\nline"),
            new(true, "join") { MemberLimit = 1 }, new(true, "join") { MemberLimit = 13 },
            new(true, "join") { Category = 0 }, new(true, "join") { Category = 6 },
        ];
        foreach (var request in invalid)
            await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, request, default);
        Assert.Equal(invalid.Length, setup.Rejected.To(leader).Length);
        Assert.Empty(setup.Changed.Sent);
        setup.Directory.Online[11] = Rig.Player(33, "replacement");
        await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(true, "stale"), default);
        Assert.False(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
    }

    /// <summary>The native document list is local, filtered, paginated and carries committed member counts.</summary>
    [Fact]
    public async Task RecruitmentDocumentsUseTenEntryPagesAndExcludeRemoteSigns()
    {
        await using var setup = new RecruitmentRig();
        var viewer = setup.Player(99);
        for (var user = 1; user <= 12; user++)
        {
            var leader = setup.Player(user);
            await EnterAsync(setup.Scenes, leader);
            await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(true, "join" + user)
                { MemberLimit = 4, Category = user == 12 ? (byte)2 : (byte)1 }, default);
        }
        setup.Directory.Party = new([new(1, "player1"), new(99, "player99")]);
        await setup.Scenes.Flow.ApplyPlazaFederationAsync(new(false,
            [new(PlazaFederationEventKind.Entered, 77, new PlazaMember(0, 77, "remote", 1, 300001, [300001])
                { Recruitment = new(true, true, "remote") { MemberLimit = 4 } }, default)]), default);
        await setup.Scenes.Flow.HandlePlazaRecruitmentListAsync(viewer, new(0, 0), default);
        var first = setup.List.To(viewer)[^1];
        Assert.Equal((12, 0u, 10), (first.Total, first.Page, first.Entries.Count));
        Assert.Equal(2, first.Entries[0].MemberCount);
        Assert.DoesNotContain(first.Entries, row => row.Member.UserNumber == 77);
        await setup.Scenes.Flow.HandlePlazaRecruitmentListAsync(viewer, new(0, uint.MaxValue), default);
        Assert.Equal(new[] { 11, 12 }, setup.List.To(viewer)[^1].Entries.Select(row => row.Member.UserNumber));
        Assert.Equal(1u, setup.List.To(viewer)[^1].Page);
        await setup.Scenes.Flow.HandlePlazaRecruitmentListAsync(viewer, new(2, 0), default);
        Assert.Equal(12, Assert.Single(setup.List.To(viewer)[^1].Entries).Member.UserNumber);
    }

    /// <summary>Reentry, tutorial selection and unload remove the previous sign and its admission feature.</summary>
    [Fact]
    public async Task SceneReplacementAndUnloadClearRecruitment()
    {
        var setup = new RecruitmentRig();
        var leader = setup.Player(11);
        var peer = setup.Player(22);
        try
        {
            await EnterAsync(setup.Scenes, leader);
            await EnterAsync(setup.Scenes, peer);
            await setup.Scenes.Flow.HandlePlazaRecruitmentAsync(leader, new(true, "join"), default);
            var previous = leader.Features.Get<IPlazaRecruitmentStatus>();
            await EnterAsync(setup.Scenes, leader);
            Assert.False(previous.IsRecruiting);
            Assert.False(leader.Features.Get<IPlazaRecruitmentStatus>().IsRecruiting);
            Assert.Single(setup.Scenes.MemberLeft.To(peer));
            await setup.Scenes.Flow.HandleTutorialSelectionOpeningAsync(leader, new(), default);
            Assert.False(leader.TryFeature<IPlazaRecruitmentStatus>(out _));
            Assert.Equal(2, setup.Scenes.MemberLeft.To(peer).Length);
        }
        finally { await setup.DisposeAsync(); }
        Assert.False(peer.TryFeature<IPlazaRecruitmentStatus>(out _));
    }

    /// <summary>A same-entry remote appearance refresh preserves its reported position and does not create a departure.</summary>
    [Fact]
    public async Task RemoteAppearanceRefreshKeepsMovementHistory()
    {
        await using var rig = new Rig();
        var observer = Rig.Player(11, "observer");
        await EnterAsync(rig, observer);
        var remote = new PlazaMember(0, 77, "remote", 9, 300001, [300001], 42);
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, remote, default)]), default);
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Moved, 77, null, Upload)]), default);
        var last = Assert.Single(rig.Position.To(observer));
        rig.Position.Sent.Clear();
        rig.MemberEntered.Sent.Clear();
        var updated = remote with { Level = 15, ColorIndex = 4, EquippedItemIds = [300001, 100111] };
        await rig.Flow.ApplyPlazaFederationAsync(new(false, [new(PlazaFederationEventKind.Entered, 77, updated, default)]), default);
        Assert.Empty(rig.MemberLeft.Sent);
        Assert.Equal(15, Assert.Single(rig.MemberEntered.To(observer)).Member.Level);
        Assert.Equal(last, Assert.Single(rig.Position.To(observer)));
    }

    private sealed class RecruitmentRig : IAsyncDisposable
    {
        internal RecruitmentDirectory Directory { get; } = new();
        internal Rig Scenes { get; }
        internal Sink<PlazaRecruitmentChanged> Changed { get; } = new();
        internal Sink<PlazaRecruitmentTextReceived> Text { get; } = new();
        internal Sink<PlazaRecruitmentListReceived> List { get; } = new();
        internal Sink<PlazaRecruitmentRejected> Rejected { get; } = new();

        internal RecruitmentRig()
        {
            Scenes = new Rig(new InGameOptions(), Directory, Directory);
            Scenes.Flow.ConfigurePlazaRecruitment(Changed, Text, List, Rejected);
        }

        internal RecordingSession Player(int user)
        {
            var player = Rig.Player(user, "player" + user);
            Directory.Online[user] = player;
            return player;
        }

        public ValueTask DisposeAsync() => Scenes.DisposeAsync();
    }

    private sealed class RecruitmentDirectory : IPartyDirectory, IOnlinePlayers
    {
        internal Dictionary<int, ISession> Online { get; } = [];
        internal PartySnapshot? Party { get; set; }
        public int MaximumMembers => 12;
        public PartySnapshot? PartyOf(int userNum) => Party?.Members.Any(row => row.UserNum == userNum) == true ? Party : null;
        public ISession? FindByUserNum(int userNum) => Online.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => Online.Values.FirstOrDefault(session =>
            session.Feature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>().Identity.Nickname == nickname);
    }
}
