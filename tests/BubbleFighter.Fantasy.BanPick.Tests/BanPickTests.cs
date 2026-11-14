using Xunit;
using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using static BubbleFighter.Fantasy.BanPick.Tests.BanPickFixtures;

namespace BubbleFighter.Fantasy.BanPick.Tests;

public sealed class BanPickPolicyRulesTests
{
    [Fact]
    public void AValidDocumentPasses() => BanPickPolicyRules.Validate(Policy(Entry("comp", BanPickRuleTypes.Block, groups: ["sniper"])));

    [Theory]
    [InlineData("nope", "ban_pick_unknown_type")]
    [InlineData(BanPickRuleTypes.Warn, null)]
    public void RuleTypesAreChecked(string type, string? code)
    {
        var error = Record.Exception(() => BanPickPolicyRules.Validate(Policy(Entry("comp", type, items: [10001]))));
        Assert.Equal(code, (error as BanPickPolicyException)?.Code);
    }

    [Fact]
    public void AnUndeclaredGroupIsRefused() =>
        Assert.Equal("ban_pick_unknown_group", Assert.Throws<BanPickPolicyException>(() =>
            BanPickPolicyRules.Validate(Policy(Entry("comp", BanPickRuleTypes.Block, groups: ["bazooka"])))).Code);

    [Fact]
    public void TwoTypesNamingOneTargetConflict()
    {
        var entry = new BanPickPolicyEntry("comp", null, null, BanPickScope.Everywhere,
            [new("a", BanPickRuleTypes.Block, [10001], []), new("b", BanPickRuleTypes.Warn, [10001], [])]);
        var error = Assert.Throws<BanPickPolicyException>(() => BanPickPolicyRules.Validate(Policy(entry)));
        Assert.Equal(("ban_pick_conflict", "comp", "a", "b"), (error.Code, error.Policy, error.Rule, error.Other));
    }

    [Theory]
    [InlineData("everyone")]
    [InlineData("")]
    public void AnUnknownRoomScopeIsRefused(string rooms) =>
        Assert.Equal("ban_pick_invalid", Assert.Throws<BanPickPolicyException>(() =>
            BanPickPolicyRules.Validate(Policy(Entry("comp", BanPickRuleTypes.Block, new BanPickScope([], [], rooms), [10001])))).Code);

    [Fact]
    public void AnEmptyRuleIsRefused() =>
        Assert.Equal("ban_pick_invalid", Assert.Throws<BanPickPolicyException>(() =>
            BanPickPolicyRules.Validate(Policy(Entry("comp", BanPickRuleTypes.Block)))).Code);
}

public sealed class BanPickEvaluatorTests
{
    private static readonly BanPickItemCatalog Items = Catalog();

    [Fact]
    public void AGroupSelectsByTheBuildsOwnSubCategory()
    {
        Assert.True(Items.InGroup(10001, Sniper));
        Assert.True(Items.InGroup(10002, Sniper));
        Assert.False(Items.InGroup(10003, Sniper));
        Assert.True(Items.InGroup(301001, Pet));
    }

    [Fact]
    public void TheStrongestRuleWins()
    {
        var policy = Policy(Entry("a", BanPickRuleTypes.Warn, groups: ["sniper"]), Entry("b", BanPickRuleTypes.Block, items: [10001]));
        var hit = BanPickEvaluator.Check(policy, Items, new("kr-812", 1, false), BanPickRoomList.Empty, 10001);
        Assert.Equal((BanPickRuleTypes.Block, "b"), (hit!.Type.Name, hit.Policy));
        Assert.Equal(BanPickRuleTypes.Warn, BanPickEvaluator.Check(policy, Items, new("kr-812", 1, false), BanPickRoomList.Empty, 10002)!.Type.Name);
    }

    [Fact]
    public void ARoomBanIsAConfirmButNeverWeakensAServerBlock()
    {
        var room = new BanPickRoomList(new HashSet<int> { 10001, 10003 }, new HashSet<string>());
        var policy = Policy(Entry("b", BanPickRuleTypes.Block, items: [10001]));
        Assert.Equal(BanPickRuleTypes.Block, BanPickEvaluator.Check(policy, Items, new("kr-812", 1, false), room, 10001)!.Type.Name);
        var confirm = BanPickEvaluator.Check(policy, Items, new("kr-812", 1, false), room, 10003)!;
        Assert.Equal((BanPickRuleTypes.Confirm, (string?)null), (confirm.Type.Name, confirm.Policy));
    }

    [Theory]
    [InlineData(BanPickRoomScopes.Public, false, true)]
    [InlineData(BanPickRoomScopes.Public, true, false)]
    [InlineData(BanPickRoomScopes.Password, true, true)]
    [InlineData(BanPickRoomScopes.All, true, true)]
    public void PasswordRoomsFollowTheScope(string rooms, bool hasPassword, bool applies) =>
        Assert.Equal(applies, BanPickEvaluator.Applies(new BanPickScope([], [], rooms), new("kr-812", 1, hasPassword)));

    [Fact]
    public void ModeAndBuildScopesNarrowAPolicy()
    {
        var scope = new BanPickScope(["kr-812"], [19]);
        Assert.True(BanPickEvaluator.Applies(scope, new("kr-812", 19, false)));
        Assert.False(BanPickEvaluator.Applies(scope, new("kr-812", 20, false)));
        Assert.False(BanPickEvaluator.Applies(scope, new("cn-324", 19, false)));
        // Outside a room only an unconditional scope applies.
        Assert.False(BanPickEvaluator.Applies(scope, new("kr-812", null, false)));
        Assert.True(BanPickEvaluator.Applies(BanPickScope.Everywhere, new("kr-812", null, false)));
    }

    [Fact]
    public void TheCharacterCountsAsWorn()
    {
        var policy = Policy(Entry("c", BanPickRuleTypes.Confirm, groups: ["character"]));
        var room = new RoomPolicyRoom(Guid.NewGuid(), 1, 1, false, true, [Seat(1, 0, "Dao", 300001, true)]);
        var hits = Assert.Single(BanPickEvaluator.Evaluate(policy, Items, "kr-812", room, BanPickRoomList.Empty));
        Assert.Equal(300001, Assert.Single(hits.Hits).ItemId);
    }
}

public sealed class BanPickItemCatalogTests
{
    [Fact]
    public void CategoryNamesBecomeTheSheetsIds()
    {
        var file = Workbook(
            Row("itemId", "itemName", "mainCategory", "subCategory", "function") + Row("10001", "Long Sniper", "Weapons", "Weapon_Sniper", "0")
                + Row("301001", "Cat", "Etc", "Followers", "10001") + Row("", "", "", "", ""),
            Row("id", "name") + Row("300", "Weapons") + Row("500", "Etc"),
            Row("id", "name") + Row("304", "Weapon_Sniper") + Row("504", "Followers"));
        var catalog = BanPickItemCatalog.Load(file, 1 << 20, "max");
        Assert.Equal(2, catalog.Count);
        Assert.Equal(new BanPickItem(10001, "Long Sniper", 300, 304, 0), catalog.Find(10001));
        Assert.True(catalog.InGroup(301001, Pet));
    }

    [Fact]
    public void AMissingColumnFailsLoud()
    {
        var file = Workbook(Row("itemId", "itemName") + Row("1", "x"), Row("id", "name"), Row("id", "name"));
        Assert.Contains("mainCategory", Assert.Throws<InvalidOperationException>(() => BanPickItemCatalog.Load(file, 1 << 20, "max")).Message);
    }
}

public sealed class BanPickRoomRulesTests
{
    private const long Host = 1, Member = 2;
    private readonly FakeRooms _rooms = new();
    private readonly RecordingNotices _notices = new();
    private readonly ManualClock _clock = new();

    public BanPickRoomRulesTests()
    {
        _rooms.Sessions[Host] = new(Host);
        _rooms.Sessions[Member] = new(Member);
        Wear([], []);
    }

    private void Wear(int[] hostItems, int[] memberItems, bool password = false, int mode = 1) =>
        _rooms.Room = new(_rooms.Room?.Instance ?? Guid.NewGuid(), 7, mode, password, true,
            [Seat(Host, 0, "Host", 300001, true, hostItems), Seat(Member, 1, "Dao", 300001, false, memberItems)]);

    private static async Task<CommandResult> Command(BanPickRoomRules rules, FakeRooms rooms, long session, params string[] arguments) =>
        await rules.RoomCommandAsync(rooms.Sessions[session], "en", arguments, CancellationToken.None);

    [Fact]
    public async Task TheHostBansAGroupAndEveryoneIsTold()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        var result = await Command(rules, _rooms, Host, "add", "sniper");
        Assert.Equal((CommandStatus.Success, ""), (result.Status, result.Message));
        Assert.Contains("banpick.list.changed|items=banpick.group|name=sniper", _notices.To(Host));
        Assert.Contains("banpick.list.changed|items=banpick.group|name=sniper", _notices.To(Member));
    }

    [Fact]
    public async Task OnlyTheHostChangesTheList()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        Assert.Equal("banpick.command.host_only", (await Command(rules, _rooms, Member, "add", "sniper")).Message);
        Assert.Equal("banpick.list.empty", (await Command(rules, _rooms, Member, "list")).Message);
    }

    [Fact]
    public async Task ANameFindsItsItemAndAnAmbiguousNameListsMatches()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        Assert.Equal("", (await Command(rules, _rooms, Host, "add", "long", "sniper")).Message);
        var ambiguous = (await Command(rules, _rooms, Host, "add", "snip")).Message;
        Assert.StartsWith("banpick.command.ambiguous", ambiguous);
        Assert.Contains("Long Sniper (10001), Short Sniper (10002)", ambiguous);
        Assert.Equal("banpick.command.not_found|text=bazooka", (await Command(rules, _rooms, Host, "add", "bazooka")).Message);
        Assert.Contains("Long Sniper", (await Command(rules, _rooms, Member, "list")).Message);
    }

    [Fact]
    public async Task AStartWithARoomBanWaitsForTheHostToPressAgainInTime()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([], [10001]);
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        Assert.Contains("banpick.start.violators", _notices.To(Member));
        Assert.Contains("banpick.line|items=Long Sniper|nickname=Dao", _notices.To(Member));
        Assert.Contains("banpick.start.reclick|seconds=10", _notices.To(Host));
        _clock.Now += 9_000;
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(_rooms.Room, Host));
        rules.OnStartDecided(_rooms.Room, Host, proceeding: true);
        // The confirmation is used up by the start: the next start is checked again.
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
    }

    /// <summary>
    /// Another policy's replay is not the host pressing again, and the host's real yes survives another policy's hold
    /// until the start actually goes ahead.
    /// </summary>
    [Fact]
    public async Task OnlyTheHostsOwnSecondPressConfirmsAndItSurvivesAnotherPolicysHold()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([], [10001]);
        var replay = _rooms.Room with { ResumedStart = true };
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
        rules.OnStartDecided(_rooms.Room, Host, proceeding: false);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        var announced = _notices.To(Member).Count();

        // A pet write finishing replays the start inside the window: still held, and nothing is announced again.
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(replay, Host));
        rules.OnStartDecided(replay, Host, proceeding: false);
        await rules.OnStartHeldAsync(replay, Host, CancellationToken.None);
        Assert.Equal(announced, _notices.To(Member).Count());

        // The host's real second press is the yes, but another policy still holds the start: the yes is kept.
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(_rooms.Room, Host));
        rules.OnStartDecided(_rooms.Room, Host, proceeding: false);
        _clock.Now += 60_000;
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(replay, Host));
        rules.OnStartDecided(replay, Host, proceeding: true);
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
    }

    [Fact]
    public async Task TheReclickWindowCloses()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        await Command(rules, _rooms, Host, "add", "10001");
        Wear([], [10001]);
        rules.EvaluateStart(_rooms.Room, Host);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        _clock.Now += 10_001;
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
    }

    [Fact]
    public async Task AConfirmationCoversOnlyTheItemsItWasAskedFor()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([], [10001]);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        Wear([], [10002]);
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
    }

    /// <summary>The native automatic start has no host to confirm, so even a server block lets it proceed.</summary>
    [Fact]
    public void TheNativeAutomaticStartIsNeverHeldByABanList()
    {
        var rules = Rules(Policy(Entry("comp", BanPickRuleTypes.Block, groups: ["sniper"])), _rooms, _notices, clock: _clock);
        Wear([], [10001]);
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(_rooms.Room with { AutomaticStart = true }, Host));
    }

    [Fact]
    public async Task AServerBlockCannotBeStartedAnyway()
    {
        var rules = Rules(Policy(Entry("comp", BanPickRuleTypes.Block, groups: ["sniper"])), _rooms, _notices, clock: _clock);
        Wear([], [10001]);
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        Assert.Contains("banpick.start.blocked", _notices.To(Host));
        Assert.DoesNotContain(_notices.To(Host), text => text.StartsWith("banpick.start.reclick", StringComparison.Ordinal));
        Assert.Equal(RoomStartDecision.Hold, rules.EvaluateStart(_rooms.Room, Host));
    }

    [Fact]
    public async Task AWarnRuleNeverHoldsTheStart()
    {
        var rules = Rules(Policy(Entry("w", BanPickRuleTypes.Warn, groups: ["pet"])), _rooms, _notices);
        Wear([], [301001]);
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(_rooms.Room, Host));
        await rules.OnAppearanceChangedAsync(_rooms.Room, Member, CancellationToken.None);
        Assert.Contains("banpick.warn.wearing|items=Cat|nickname=Dao", _notices.To(Host));
        // The same item is not announced again.
        _notices.Sent.Clear();
        await rules.OnAppearanceChangedAsync(_rooms.Room, Member, CancellationToken.None);
        Assert.Empty(_notices.Sent);
    }

    [Fact]
    public async Task ANewcomerHearsTheListAndWhoWearsRestrictedItems()
    {
        var rules = Rules(Policy(Entry("w", BanPickRuleTypes.Warn, groups: ["pet"])), _rooms, _notices);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([301001], []);
        _notices.Sent.Clear();
        await rules.OnMemberEnteredAsync(_rooms.Room, Member, CancellationToken.None);
        var heard = _notices.To(Member).ToArray();
        Assert.Contains("banpick.list.current|items=banpick.group|name=sniper", heard);
        Assert.Contains("banpick.warn.summary", heard);
        Assert.Contains("banpick.line|items=Cat|nickname=Host", heard);
    }

    [Fact]
    public async Task SetReplacesTheWholeListWithOneAnnouncement()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        await Command(rules, _rooms, Host, "add", "10003");
        _notices.Sent.Clear();
        Assert.Equal((CommandStatus.Success, ""), Pair(await Command(rules, _rooms, Host, "set", "sniper", "10001")));
        Assert.Single(_notices.To(Member), text => text.StartsWith("banpick.list.changed", StringComparison.Ordinal));
        var list = await rules.CompanionAsync("game.ban.list.get", new("account", "kr-812", "launch", _rooms.Sessions[Host], "en"), default, CancellationToken.None);
        var room = System.Text.Json.JsonSerializer.SerializeToElement(list).GetProperty("Room");
        Assert.Equal(2, room.GetArrayLength());
        _notices.Sent.Clear();
        // The same list again changes nothing and says nothing.
        await Command(rules, _rooms, Host, "set", "10001", "sniper");
        Assert.Empty(_notices.To(Member));
    }

    [Fact]
    public async Task SetChangesNothingWhenOneWordDoesNotResolveOrTheListIsTooLong()
    {
        var rules = new BanPickRoomRules(new FakeHub(FakeHub.Defaults with { MaxRoomEntries = 2 }, new BanPickPolicyStore(Policy(), null, 1 << 20), null), _rooms, Catalog(),
            _notices, null, new KeyTexts(), null, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await Command(rules, _rooms, Host, "add", "10003");
        _notices.Sent.Clear();
        Assert.StartsWith("banpick.command.not_found", (await Command(rules, _rooms, Host, "set", "10001", "999999")).Message, StringComparison.Ordinal);
        Assert.Equal("banpick.command.full", (await Command(rules, _rooms, Host, "set", "10001", "10002", "sniper")).Message);
        Assert.Equal("banpick.command.host_only", (await Command(rules, _rooms, Member, "set", "10001")).Message);
        Assert.Empty(_notices.To(Member));
        await Command(rules, _rooms, Host, "add", "10001");
        // A refused add leaves the list as it was: the next add of a listed item is "already", not a silent change.
        Assert.StartsWith("banpick.command.full", (await Command(rules, _rooms, Host, "add", "10002")).Message, StringComparison.Ordinal);
        Assert.StartsWith("banpick.command.already", (await Command(rules, _rooms, Host, "add", "10001")).Message, StringComparison.Ordinal);
    }

    private static (CommandStatus, string) Pair(CommandResult result) => (result.Status, result.Message);

    [Fact]
    public async Task ALongListIsCountedNotNamed()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        rules = new BanPickRoomRules(new FakeHub(FakeHub.Defaults with { AnnouncementItems = 2 }, new BanPickPolicyStore(Policy(), null, 1 << 20), null), _rooms, Catalog(),
            _notices, null, new KeyTexts(), null, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await Command(rules, _rooms, Host, "add", "10001");
        await Command(rules, _rooms, Host, "add", "10002");
        _notices.Sent.Clear();
        await Command(rules, _rooms, Host, "add", "10003");
        Assert.Contains("banpick.list.changed_many|count=3", _notices.To(Member));
    }

    [Fact]
    public async Task TheOverlayAsksTheHostAndItsYesIsTheStartCommand()
    {
        var companion = new FakeCompanion(Host);
        var rules = Rules(Policy(), _rooms, _notices, companion, _clock);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([], [10001]);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        var ask = Assert.Single(companion.Sent, sent => sent.Type == BanPickCompanion.StartConfirm);
        Assert.Equal("bp start", JsonDocument.Parse(ask.Json).RootElement.GetProperty("command").GetString());
        Assert.DoesNotContain(_notices.To(Host), text => text.StartsWith("banpick.start.reclick", StringComparison.Ordinal));
        // The overlay runs "bp start" through the launch-bound command channel, exactly as if the host typed it.
        Assert.Equal("", (await Command(rules, _rooms, Host, "start")).Message);
        Assert.Equal((_rooms.Room.Instance, Host), Assert.Single(_rooms.Resumed));
        Assert.Equal(RoomStartDecision.Proceed, rules.EvaluateStart(_rooms.Room, Host));
    }

    [Fact]
    public async Task TheOverlayKnowsTheRoomAndItsHostThroughEnterHandOverAndLeave()
    {
        var companion = new FakeCompanion(Host, Member);
        var rules = Rules(Policy(), _rooms, _notices, companion, _clock);
        // An empty list still tells each member whether they host, so the host is offered the editor at once.
        await rules.OnMemberEnteredAsync(_rooms.Room, Host, CancellationToken.None);
        await rules.OnMemberEnteredAsync(_rooms.Room, Member, CancellationToken.None);
        Assert.Equal((true, true, 0), State(companion, Host));
        Assert.Equal((true, false, 0), State(companion, Member));

        _rooms.Room = _rooms.Room with { Seats = [.. _rooms.Room.Seats.Select(seat => seat with { Host = seat.SessionId == Member })] };
        await rules.OnHostChangedAsync(_rooms.Room, CancellationToken.None);
        Assert.Equal((true, false, 0), State(companion, Host));
        Assert.Equal((true, true, 0), State(companion, Member));

        _rooms.Room = _rooms.Room with { Seats = [.. _rooms.Room.Seats.Where(seat => seat.SessionId == Member)] };
        await rules.OnMemberLeftAsync(_rooms.Room, _rooms.Sessions[Host], CancellationToken.None);
        Assert.Equal((false, false, 0), State(companion, Host));
        Assert.Equal((true, true, 0), State(companion, Member));
    }

    [Fact]
    public async Task AListNoticeCannotRestoreStaleOverlayStateAfterDeparture()
    {
        var companion = new FakeCompanion(Host, Member);
        var rules = Rules(Policy(), _rooms, _notices, companion, _clock);
        _notices.AfterSend = async () =>
        {
            _notices.AfterSend = null;
            _rooms.Room = _rooms.Room with { Seats = [Seat(Member, 1, "Dao", 300001, true)] };
            await rules.OnMemberLeftAsync(_rooms.Room, _rooms.Sessions[Host], CancellationToken.None);
        };

        await Command(rules, _rooms, Host, "add", "sniper");

        Assert.Equal((false, false, 0), State(companion, Host));
        Assert.Equal((true, true, 1), State(companion, Member));
    }

    [Fact]
    public async Task TheOverlayHearsWhenTheRoundStartsAndWhenTheRoomWaitsAgain()
    {
        var companion = new FakeCompanion(Host, Member);
        var rules = Rules(Policy(), _rooms, _notices, companion, _clock);
        await rules.OnMemberEnteredAsync(_rooms.Room, Host, CancellationToken.None);
        await rules.OnMemberEnteredAsync(_rooms.Room, Member, CancellationToken.None);
        Assert.False(Playing(companion, Host));

        // A held start changes nothing; one that goes ahead tells every member the round is under way.
        var before = companion.Sent.Count;
        rules.OnStartDecided(_rooms.Room, Host, proceeding: false);
        Assert.Equal(before, companion.Sent.Count);
        rules.OnStartDecided(_rooms.Room, Host, proceeding: true);
        Assert.True(Playing(companion, Host));
        Assert.True(Playing(companion, Member));
        Assert.Equal((true, true, 0), State(companion, Host));

        await rules.OnWaitingAgainAsync(_rooms.Room, CancellationToken.None);
        Assert.False(Playing(companion, Host));
        Assert.False(Playing(companion, Member));
    }

    private static bool Playing(FakeCompanion companion, long session) =>
        JsonDocument.Parse(companion.Sent.Last(sent => sent.Session == session && sent.Type == BanPickCompanion.ListChanged).Json)
            .RootElement.GetProperty("playing").GetBoolean();

    private static (bool InRoom, bool IsHost, int Count) State(FakeCompanion companion, long session)
    {
        var last = JsonDocument.Parse(companion.Sent.Last(sent => sent.Session == session && sent.Type == BanPickCompanion.ListChanged).Json).RootElement;
        return (last.GetProperty("in_room").GetBoolean(), last.GetProperty("is_host").GetBoolean(), last.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task OnlyTheHostWhosePressWasHeldCanStartAnyway()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        await Command(rules, _rooms, Host, "add", "sniper");
        Wear([], [10001]);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        Assert.Equal("banpick.command.host_only", (await Command(rules, _rooms, Member, "start")).Message);
        Assert.Empty(_rooms.Resumed);
        _clock.Now += 10_001;
        Assert.Equal("banpick.command.nothing_held", (await Command(rules, _rooms, Host, "start")).Message);
        Assert.Empty(_rooms.Resumed);
    }

    [Fact]
    public async Task ATypedStartWorksWithoutTheOverlay()
    {
        var rules = Rules(Policy(), _rooms, _notices, clock: _clock);
        Assert.Equal("banpick.command.nothing_held", (await Command(rules, _rooms, Host, "start")).Message);
        await Command(rules, _rooms, Host, "add", "10001");
        Wear([], [10001]);
        await rules.OnStartHeldAsync(_rooms.Room, Host, CancellationToken.None);
        Assert.Equal("", (await Command(rules, _rooms, Host, "start")).Message);
        Assert.Single(_rooms.Resumed);
    }

    [Fact]
    public async Task ABuildWithoutServerNoticesHearsSystemChatLines()
    {
        var chat = new RecordingChat();
        var rules = new BanPickRoomRules(new FakeHub(FakeHub.Defaults, new BanPickPolicyStore(Policy(), null, 1 << 20), null), _rooms, Catalog(),
            null, chat, new KeyTexts(), null, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await Command(rules, _rooms, Host, "add", "sniper");
        var line = Assert.Single(chat.Sent, sent => sent.Session == Member).Line;
        // The member's own seat, the system name and the coloured channel; no other player is the speaker.
        Assert.Equal((7, 1, "banpick.system_name", 1, "banpick.list.changed|items=banpick.group|name=sniper"),
            (line.RoomId, line.Slot, line.Nickname, line.Channel, line.Text));
    }

    [Fact]
    public async Task AClosedRoomForgetsItsList()
    {
        var rules = Rules(Policy(), _rooms, _notices);
        await Command(rules, _rooms, Host, "add", "sniper");
        rules.OnRoomClosed(_rooms.Room.Instance);
        Assert.Equal("banpick.list.empty", (await Command(rules, _rooms, Host, "list")).Message);
    }

    [Fact]
    public async Task ABlockedItemCannotBeEquippedAndOthersPass()
    {
        var online = new FakeOnline(new() { [(int)Member] = _rooms.Sessions[Member] });
        var rules = Rules(Policy(Entry("comp", BanPickRuleTypes.Block, groups: ["sniper"]), Entry("pub", BanPickRuleTypes.Block,
            new BanPickScope([], [], BanPickRoomScopes.Public), [10003])), _rooms, _notices, online: online);
        ValueTask<EquipmentSelectionDecision> Allowed(CancellationToken _) => ValueTask.FromResult(new EquipmentSelectionDecision(true, 0, true));
        var sniper = await rules.GuardEquipmentAsync(new("kr-812/game", Member, 10001, true), Allowed, CancellationToken.None);
        Assert.False(sniper.Allowed);
        Assert.Contains("banpick.equip.blocked|item=Long Sniper", _notices.To(Member));
        // A public-room-only block refuses in the public room the player sits in, not in a password room.
        Assert.False((await rules.GuardEquipmentAsync(new("kr-812/game", Member, 10003, true), Allowed, CancellationToken.None)).Allowed);
        Wear([], [], password: true);
        Assert.True((await rules.GuardEquipmentAsync(new("kr-812/game", Member, 10003, true), Allowed, CancellationToken.None)).Allowed);
        // Taking a banned item off is always allowed.
        Assert.True((await rules.GuardEquipmentAsync(new("kr-812/game", Member, 10001, false), Allowed, CancellationToken.None)).Allowed);
    }
}

public sealed class BanPickStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "banpick-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task ASaveNamesItsRevisionAndSurvivesARestart()
    {
        var path = Path.Combine(_directory, "banpick.json");
        var store = new BanPickPolicyStore(Policy(), path, 1 << 20);
        var saved = await store.SaveAsync(0, Policy(Entry("comp", BanPickRuleTypes.Warn, items: [10001])), CancellationToken.None);
        Assert.Equal(1, saved.Revision);
        Assert.Equal("ban_pick_revision_conflict", (await Assert.ThrowsAsync<BanPickPolicyException>(async () =>
            await store.SaveAsync(0, Policy(), CancellationToken.None))).Code);
        var reloaded = new BanPickPolicyStore(Policy(), path, 1 << 20).Current;
        Assert.Equal(1, reloaded.Revision);
        Assert.Equal("comp", Assert.Single(reloaded.Policy.Policies).Id);
    }

    [Fact]
    public async Task WithoutAStateFileTheDocumentIsReadOnly() =>
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await new BanPickPolicyStore(Policy(), "", 1 << 20).SaveAsync(0, Policy(), CancellationToken.None));

    [Fact]
    public async Task TheAdminCommandEditsTheDocument()
    {
        var store = new BanPickPolicyStore(Policy(), Path.Combine(_directory, "banpick.json"), 1 << 20);
        var admin = new BanPickAdminCommands(new BanPickHub(FakeHub.Defaults, store, null), new KeyTexts());
        var caller = new CommandCaller(new(PrincipalKind.Console, "root"), new(PrincipalKind.Console, "root")) { Source = CommandSource.Terminal };
        async Task<CommandResult> Run(params string[] tokens) => await admin.ExecuteAsync(new(caller, "gbanpick", []), tokens, CancellationToken.None);

        Assert.Equal("banpick.admin.saved|revision=1", (await Run("add", "comp", "block", "sniper")).Message);
        Assert.Equal("banpick.admin.saved|revision=2", (await Run("scope", "comp", "modes=19,20", "rooms=public")).Message);
        // Moving a target to another type takes it out of the first, so the document stays free of conflicts.
        Assert.Equal("banpick.admin.saved|revision=3", (await Run("add", "comp", "warn", "sniper")).Message);
        var entry = Assert.Single(store.Current.Policy.Policies);
        Assert.Equal(BanPickRuleTypes.Warn, Assert.Single(entry.Rules).Type);
        Assert.Equal([19, 20], entry.Scope.Modes);
        Assert.Equal(BanPickRoomScopes.Public, entry.Scope.Rooms);
        Assert.StartsWith("banpick.admin.rejected|code=ban_pick_group_in_use", (await Run("ungroup", "sniper")).Message);
        Assert.Equal(CommandStatus.InvalidArguments, (await Run("scope", "comp", "colour=red")).Status);
        Assert.Equal("banpick.admin.saved|revision=4", (await Run("remove", "comp", "sniper")).Message);
        Assert.Empty(Assert.Single(store.Current.Policy.Policies).Rules);
    }
}
