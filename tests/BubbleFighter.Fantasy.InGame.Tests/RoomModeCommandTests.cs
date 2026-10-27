using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests.Shared;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The <c>!set &lt;modeid&gt;</c> command and the <c>#id#</c> room title tag, on the typed and the packet room paths.</summary>
public sealed class RoomModeCommandTests
{
    [Fact]
    public async Task SetCommandChangesTheModeAndTellsEveryMemberIncludingTheMaster()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);

        await rig.Flow.HandleChatAsync(host, new(0, "!set 8"), default);

        Assert.Equal(8, rig.Registry.Find(roomId)!.ModeId);
        Assert.Equal(8, rig.SettingsChanged.To(host).Single().Room.ModeId);
        Assert.Equal(8, rig.SettingsChanged.To(guest).Single().Room.ModeId);
        Assert.Equal("room.mode.changed:Zombie Battle", Assert.Single(rig.Notices.To(host)).Text);
        Assert.Equal("room.mode.changed:Zombie Battle", Assert.Single(rig.Notices.To(guest)).Text);
        Assert.Empty(rig.Chat.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task SetCommandNamesAModeWithoutAKnownNameByItsNumberAndCarriesItInTheBuildSettings()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested("room", "", 4, "rel_Village07_Easy02",
            new RoomBuildSettings("test", new byte[] { 1, 2, 3 })), default);
        var room = rig.Registry.TryGet(host.Id)!;

        await rig.Flow.HandleChatAsync(host, new(0, " ！SET   77 "), default);

        Assert.Equal(77, room.ModeId);
        Assert.Equal(77, room.BuildSettings!.Mode);
        Assert.Equal(new byte[] { 1, 2, 3 }, room.BuildSettings.Bytes.ToArray());
        Assert.Equal(77, rig.SettingsChanged.To(host).Single().Room.BuildSettings!.Mode);
        Assert.Equal("room.mode.changed:mode.unnamed:77", Assert.Single(rig.Notices.To(host)).Text);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task TheMastersOwnSettingsChangeEndsAModeTheServerSet()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested("room", "", 4, "rel_Village07_Easy02",
            new RoomBuildSettings("test", new byte[] { 1 })), default);
        await rig.Flow.HandleChatAsync(host, new(0, "!set 8"), default);

        await rig.Flow.HandleSettingsChangeAsync(host,
            new RoomSettingsChangeRequested(3, "", new RoomBuildSettings("test", default, new byte[] { 9 })), default);

        var room = rig.Registry.TryGet(host.Id)!;
        Assert.Equal(3, room.ModeId);
        Assert.Null(room.BuildSettings!.Mode);
        Assert.Equal(new byte[] { 9 }, room.BuildSettings.Change.ToArray());
        await rig.Registry.StopAllAsync();
    }

    [Theory]
    [InlineData("!set")]
    [InlineData("!set abc")]
    [InlineData("!set 0")]
    [InlineData("!set -3")]
    [InlineData("!set 65536")]
    [InlineData("!set 99999999999")]
    [InlineData("!set 8 9")]
    public async Task SetCommandWithoutAValidModeIdTellsOnlyTheSender(string text)
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);

        await rig.Flow.HandleChatAsync(host, new(0, text), default);

        Assert.Equal(4, rig.Registry.Find(roomId)!.ModeId);
        Assert.Equal("command.set.invalid:", Assert.Single(rig.Notices.To(host)).Text);
        Assert.Empty(rig.Notices.To(guest));
        Assert.Empty(rig.SettingsChanged.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task SetCommandIsRefusedForAGuestAndInAStartedRoom()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);

        await rig.Flow.HandleChatAsync(guest, new(0, "!set 8"), default);
        Assert.Equal("command.set.host_only:", Assert.Single(rig.Notices.To(guest)).Text);
        Assert.Empty(rig.Notices.To(host));
        Assert.Equal(4, rig.Registry.Find(roomId)!.ModeId);

        await rig.Flow.HandleChatAsync(host, new(0, "!start"), default);
        Assert.Equal(RoomPhase.Starting, rig.Registry.Find(roomId)!.Phase);
        await rig.Flow.HandleChatAsync(host, new(0, "!set 8"), default);
        Assert.Equal("command.set.waiting_only:", Assert.Single(rig.Notices.To(host)).Text);
        Assert.Equal(4, rig.Registry.Find(roomId)!.ModeId);
        Assert.Empty(rig.SettingsChanged.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task SetCommandOutsideARoomIsRefusedAndSimilarWordsAreOrdinaryChat()
    {
        var rig = new RoomFlowTests.Rig();
        var lonely = RoomFlowTests.Rig.Player(11, "lonely");
        await rig.Flow.HandleChatAsync(lonely, new(0, "!set 8"), default);
        Assert.Equal("command.set.waiting_only:", Assert.Single(rig.Notices.To(lonely)).Text);

        var host = RoomFlowTests.Rig.Player(12, "host");
        var roomId = await rig.CreateAsync(host);
        await rig.Flow.HandleChatAsync(host, new(0, "!settings 8"), default);
        Assert.Equal(4, rig.Registry.Find(roomId)!.ModeId);
        Assert.Empty(rig.Notices.To(host));
        Assert.Single(rig.Chat.To(host));
        await rig.Registry.StopAllAsync();
    }

    [Theory]
    [InlineData("Fun room #8#", "Fun room")]
    [InlineData("#8# Fun room", "Fun room")]
    [InlineData("Fun #8# room", "Fun room")]
    [InlineData("#8#", "")]
    public async Task ARoomCreatedWithAModeTagGetsThatModeAMemberNoticeAndACleanTitle(string title, string shown)
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested(title, "", 4, "rel_Village07_Easy02",
            new RoomBuildSettings("test", new byte[] { 1 })), default);

        var room = rig.Registry.TryGet(host.Id)!;
        Assert.Equal(8, room.ModeId);
        Assert.Equal(shown, room.RoomName);
        Assert.Equal(8, room.BuildSettings!.Mode);
        var entered = Assert.Single(rig.Entered.To(host)).Room;
        Assert.Equal(8, entered.ModeId);
        Assert.Equal(shown, entered.Name);
        Assert.Equal("room.mode.changed:Zombie Battle", Assert.Single(rig.Notices.To(host)).Text);
        await rig.Registry.StopAllAsync();
    }

    [Theory]
    [InlineData("Fun room #0#")]
    [InlineData("Fun room #65536#")]
    [InlineData("Fun room #99999999999#")]
    [InlineData("Fun room #x#")]
    [InlineData("Fun room #8")]
    public async Task AnInvalidModeTagInATitleIsIgnoredAndTheTitleKept(string title)
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested(title, "", 4, "rel_Village07_Easy02"), default);

        var room = rig.Registry.TryGet(host.Id)!;
        Assert.Equal(4, room.ModeId);
        Assert.Equal(title, room.RoomName);
        Assert.Empty(rig.Notices.Sent);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public async Task ATitleTypedAfterTheCreateCanCarryTheModeTagToo()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested("", "", 4, "rel_Village07_Easy02") { UpdateExistingTitle = true }, default);
        await rig.Flow.HandleCreateAsync(host, new RoomCreateRequested("Typed #8#", "", 4, "rel_Village07_Easy02") { UpdateExistingTitle = true }, default);

        var room = rig.Registry.TryGet(host.Id)!;
        Assert.Equal("Typed", room.RoomName);
        Assert.Equal(8, room.ModeId);
        Assert.Equal(8, rig.Entered.To(host)[^1].Room.ModeId);
        Assert.Equal("room.mode.changed:Zombie Battle", Assert.Single(rig.Notices.To(host)).Text);
        await rig.Registry.StopAllAsync();
    }

    [Fact]
    public void TheTagReaderTakesTheFirstTagAndKeepsTheRestOfTheTitle()
    {
        Assert.Equal(new RoomModeTag.Result("a b #9#", 8, null), RoomModeTag.Extract("a #8# b #9#"));
        Assert.Equal(new RoomModeTag.Result("plain", null, null), RoomModeTag.Extract("plain"));
        Assert.Equal(new RoomModeTag.Result("x #0#", null, "#0#"), RoomModeTag.Extract("x #0#"));
    }

}
