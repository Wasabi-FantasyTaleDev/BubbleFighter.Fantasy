using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Custom options remain authoritative room state across requests, membership changes and native mode defaults.</summary>
public sealed class RoomCustomRulesTests
{
    /// <summary>The full immutable settings reach every member before success or peer feedback, including after a reset.</summary>
    [Fact]
    public async Task AcceptedOptionsPublishCompleteSettingsBeforeFeedback()
    {
        await using var rig = new Rig();
        var source = new RoomBuildSettings("kr-1256", new byte[] { 1, 2 }, new byte[] { 3 })
        {
            SessionPrefix = new byte[] { 4, 5 },
            RoundSelections = [new(1, new byte[] { 6 })],
        };
        var room = await rig.CreateAsync(1, source);
        await rig.JoinAsync();
        rig.Sent.Clear();
        uint[] speed = [150];
        var values = new List<RoomCustomRuleValue> { new(1, speed), Value(12, 2, 4) };
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, values), default);
        speed[0] = 200;
        values.Clear();

        Assert.Equal(new[] { rig.Host.Id, rig.Guest.Id, rig.Host.Id, rig.Guest.Id }, rig.Sent.Select(row => row.Session));
        var first = Assert.IsType<RoomSettingsChanged>(rig.Sent[0].Message).Room;
        Assert.Same(first, Assert.IsType<RoomSettingsChanged>(rig.Sent[1].Message).Room);
        Assert.Equal((ushort)0, Assert.IsType<RoomCustomRulesReplied>(rig.Sent[2].Message).Result);
        Assert.Equal((ushort)4, Assert.IsType<RoomCustomRulesReplied>(rig.Sent[3].Message).Result);
        var saved = Assert.IsType<RoomBuildSettings>(first.BuildSettings);
        Assert.Equal(source.Bytes.ToArray(), saved.Bytes.ToArray());
        Assert.Equal(source.Change.ToArray(), saved.Change.ToArray());
        Assert.Equal(source.SessionPrefix.ToArray(), saved.SessionPrefix.ToArray());
        Assert.Same(source.RoundSelections, saved.RoundSelections);
        Assert.Equal(new uint[] { 150 }, saved.CustomRules![0].Values);
        Assert.Equal(new uint[] { 2, 4 }, saved.CustomRules[1].Values);
        Assert.Null(source.CustomRules);

        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", true, []), default);
        Assert.Empty(room.BuildSettings!.CustomRules!);
        Assert.Equal(2, saved.CustomRules.Count);
    }

    /// <summary>A guest, a loading master or a replaced session cannot commit a custom-option vector.</summary>
    [Theory]
    [InlineData("guest")]
    [InlineData("starting")]
    [InlineData("stale-session")]
    public async Task WaitingMasterAuthorityIsRequired(string scenario)
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        await rig.JoinAsync();
        var actor = scenario == "guest" ? rig.Guest : scenario == "stale-session"
            ? RoomFlowTests.Rig.Player(rig.Host.Id, "replacement") : rig.Host;
        if (scenario == "starting")
        {
            await rig.Flow.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Flow.HandleStartAsync(rig.Host, new(), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
        }
        var original = room.BuildSettings;
        rig.Sent.Clear();
        await rig.Flow.HandleCustomRulesChangeAsync(actor, new("kr-1256", false, [Value(1, 150)]), default);
        Assert.Same(original, room.BuildSettings);
        var response = Assert.Single(rig.Sent);
        Assert.Equal(actor.Id, response.Session);
        Assert.Equal((ushort)1, Assert.IsType<RoomCustomRulesReplied>(response.Message).Result);
    }

    /// <summary>Invalid domains, foreign layouts and inconsistent native default markers leave the room untouched.</summary>
    [Fact]
    public async Task InvalidRequestsNeverPublishOrPartiallyCommit()
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        var original = room.BuildSettings;
        RoomCustomRulesChangeRequested[] invalid =
        [
            new("cn-774", false, [Value(1, 150)]),
            new("kr-1256", false, [Value(1, 151)]),
            new("kr-1256", false, [Value(1, 150), Value(1, 160)]),
            new("kr-1256", false, [Value(12, 2), Value(13, 0)]),
            new("kr-1256", true, [Value(1, 150)]),
            new("kr-1256", false, []),
        ];
        foreach (var request in invalid)
        {
            rig.Sent.Clear();
            await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, request, default);
            Assert.Same(original, room.BuildSettings);
            Assert.Equal((ushort)2, Assert.IsType<RoomCustomRulesReplied>(Assert.Single(rig.Sent).Message).Result);
        }
    }

    /// <summary>The request owns a copy before it waits for the room executor, and completed snapshots stay unchanged.</summary>
    [Fact]
    public async Task QueuedRequestCopiesNestedValuesBeforeWaiting()
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = room.Executor.ExecuteAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }, default);
        try
        {
            await entered.Task;
            uint[] speed = [150];
            var values = new List<RoomCustomRuleValue> { new(1, speed) };
            var pending = rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, values), default).AsTask();
            speed[0] = 151;
            values.Clear();
            release.SetResult();
            await pending;
            Assert.Equal(new uint[] { 150 }, Assert.Single(room.BuildSettings!.CustomRules!).Values);
        }
        finally { release.TrySetResult(); await barrier; }
    }

    /// <summary>Authority is rechecked after earlier queued work transfers ownership, while the new master keeps the saved options.</summary>
    [Fact]
    public async Task OwnershipTransferPreservesOptionsAndInvalidatesQueuedOldMaster()
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, [Value(1, 150)]), default);
        var saved = room.BuildSettings;
        await rig.JoinAsync();
        Assert.Same(saved, rig.Entered.To(rig.Guest)[^1].Room.BuildSettings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = room.Executor.ExecuteAsync(async _ => { entered.SetResult(); await release.Task; }, default);
        try
        {
            await entered.Task;
            var transfer = rig.Flow.HandleMasterChangeAsync(rig.Host, new("guest"), default).AsTask();
            var stale = rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, [Value(1, 160)]), default).AsTask();
            rig.Sent.Clear();
            release.SetResult();
            await transfer;
            await stale;
            Assert.Same(saved, room.BuildSettings);
            Assert.Equal((ushort)1, Assert.IsType<RoomCustomRulesReplied>(Assert.Single(rig.Sent).Message).Result);
            await rig.Flow.HandleCustomRulesChangeAsync(rig.Guest, new("kr-1256", false, [Value(1, 170)]), default);
            Assert.Equal(new uint[] { 170 }, Assert.Single(room.BuildSettings!.CustomRules!).Values);
            var changed = room.BuildSettings;
            await rig.Flow.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Same(changed, room.BuildSettings);
            Assert.Equal(room.FindByGame(rig.Host.Id)!.Slot, room.MasterSlot);
        }
        finally { release.TrySetResult(); await barrier; }
    }

    /// <summary>Map edits preserve options, mode edits restore only FirstChanges, and recreating the room does not reuse old options.</summary>
    [Fact]
    public async Task ModeChangesAndNewRoomsRestoreTheirNativeDefaults()
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, [Value(1, 150)]), default);
        var custom = room.BuildSettings!.CustomRules;
        await rig.Flow.HandleSettingsChangeAsync(rig.Host, new(1, "new-map", new("kr-1256", default, new byte[] { 8 })), default);
        Assert.Same(custom, room.BuildSettings.CustomRules);
        await rig.Flow.HandleSettingsChangeAsync(rig.Host, new(37, ""), default);
        Assert.Equal(new ushort[] { 31, 32 }, room.BuildSettings.CustomRules!.Select(row => row.ValueId));
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host,
            new("kr-1256", true, [Value(32, 1), Value(31, 1)]), default);
        Assert.Equal((ushort)0, Assert.IsType<RoomCustomRulesReplied>(rig.Sent[^1].Message).Result);
        await rig.Flow.HandleSettingsChangeAsync(rig.Host, new(30, ""), default);
        Assert.Empty(room.BuildSettings.CustomRules!);
        await rig.Flow.HandleSettingsChangeAsync(rig.Host, new(43, ""), default);
        Assert.Empty(room.BuildSettings.CustomRules!);
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", true, []), default);
        Assert.Equal((ushort)2, Assert.IsType<RoomCustomRulesReplied>(rig.Sent[^1].Message).Result);

        await rig.Flow.HandleCreateAsync(rig.Host, new("room #37#", "", 1, "ignored") { UpdateExistingTitle = true }, default);
        Assert.Equal(37, room.ModeId);
        Assert.Equal(new ushort[] { 31, 32 }, room.BuildSettings.CustomRules!.Select(row => row.ValueId));
        await rig.Flow.HandleLeaveAsync(rig.Host, new(), default);
        var replacement = await rig.CreateAsync(30);
        Assert.NotSame(room, replacement);
        Assert.Empty(replacement.BuildSettings!.CustomRules!);
    }

    /// <summary>A server-created room initializes defaults when it first receives native settings, even without changing mode.</summary>
    [Fact]
    public async Task FirstNativeSettingsCannotInjectUnvalidatedCustomOptions()
    {
        await using var rig = new Rig();
        await rig.Flow.HandleCreateAsync(rig.Host, new("room", "", 37, "map"), default);
        var room = rig.Registry.TryGet(rig.Host.Id)!;
        Assert.Null(room.BuildSettings);
        await rig.Flow.HandleSettingsChangeAsync(rig.Host, new(37, "", new("kr-1256", default)
        {
            CustomRules = [Value(1, 9999)],
        }), default);
        Assert.Equal(new ushort[] { 31, 32 }, room.BuildSettings!.CustomRules!.Select(row => row.ValueId));
    }

    /// <summary>A failed settings transport closes only that member and cannot be followed by misleading success feedback.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSettingsSendDoesNotBlockHealthyMembersOrAcknowledgeTheFailedMember(bool masterFails)
    {
        await using var rig = new Rig();
        var room = await rig.CreateAsync();
        await rig.JoinAsync();
        var failed = masterFails ? rig.Host : rig.Guest;
        var healthy = masterFails ? rig.Guest : rig.Host;
        rig.Settings.FailedSession = failed.Id;
        rig.Sent.Clear();
        await rig.Flow.HandleCustomRulesChangeAsync(rig.Host, new("kr-1256", false, [Value(1, 150)]), default);
        Assert.True(failed.Connection.IsClosed);
        Assert.False(healthy.Connection.IsClosed);
        Assert.Equal(new uint[] { 150 }, Assert.Single(room.BuildSettings!.CustomRules!).Values);
        Assert.All(rig.Sent, row => Assert.Equal(healthy.Id, row.Session));
        Assert.IsType<RoomSettingsChanged>(rig.Sent[0].Message);
        Assert.Equal(masterFails ? (ushort)4 : (ushort)0, Assert.IsType<RoomCustomRulesReplied>(rig.Sent[1].Message).Result);
        Assert.Equal(2, rig.Sent.Count);
    }

    private static RoomCustomRuleValue Value(ushort id, params uint[] values) => new(id, values);

    private sealed class Rig : IAsyncDisposable
    {
        internal RoomRegistry Registry { get; } = new(new InGameOptions { MaxPlayersPerMatch = 4 });
        internal RecordingSession Host { get; } = RoomFlowTests.Rig.Player(201, "host");
        internal RecordingSession Guest { get; } = RoomFlowTests.Rig.Player(202, "guest");
        internal RoomFlowTests.Sink<RoomEntered> Entered { get; } = new();
        internal List<(long Session, IMessage Message)> Sent { get; } = [];
        internal Sender<RoomSettingsChanged> Settings { get; }
        internal RoomFlow Flow { get; }

        internal Rig()
        {
            Settings = new(Sent);
            Flow = new(Registry, new(Entered, new RoomFlowTests.Sink<RoomSeatUpdated>(),
                new RoomFlowTests.Sink<RoomSeatVacated>(), new RoomFlowTests.Sink<RoomMasterChanged>(),
                Left: new RoomFlowTests.Sink<RoomLeft>(), MatchStarting: new RoomFlowTests.Sink<RoomMatchStarting>(),
                SettingsChanged: Settings, MasterChangeReply: new RoomFlowTests.Sink<RoomMasterChangeReplied>()), NullLogger.Instance)
            {
                CustomRules = CustomGameRulesCatalog.Load(new WorkbookData(), "Config/CustomGameMode.xml", 1048576, 1024, 64, 4096),
                CustomRulesReply = new Sender<RoomCustomRulesReplied>(Sent),
            };
        }

        internal async Task<PlayRoom> CreateAsync(int mode = 1, RoomBuildSettings? source = null)
        {
            await Flow.HandleCreateAsync(Host, new("room", "", mode, "map", source ?? new("kr-1256", new byte[] { 1 })), default);
            return Registry.TryGet(Host.Id)!;
        }

        internal ValueTask JoinAsync() => Flow.HandleJoinAsync(Guest, new(Registry.TryGet(Host.Id)!.Id, ""), default);

        public async ValueTask DisposeAsync()
        {
            await Flow.StopScenesAsync();
            await Registry.StopAllAsync();
        }
    }

    private sealed class Sender<T>(List<(long Session, IMessage Message)> sent) : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        internal long? FailedSession { get; set; }
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (session.Id == FailedSession) throw new IOException("The settings transport failed.");
            sent.Add((session.Id, message));
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class WorkbookData : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/CustomGameMode.xml";
        public byte[] Read(string path) => Exists(path) ? Encoding.UTF8.GetBytes(CustomGameRulesCatalogTests.Sample())
            : throw new FileNotFoundException("The synthetic client workbook does not contain this resource.", path);
    }
}
