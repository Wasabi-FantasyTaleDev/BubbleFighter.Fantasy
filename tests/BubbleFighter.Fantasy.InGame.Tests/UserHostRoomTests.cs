using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Comprehensive room, escrow, countdown, settlement, cancellation, evacuation, and startup-recovery
/// tests for sponsor (<c>user_host</c>) prize rooms inside <c>InGame</c>.
/// </summary>
public sealed class UserHostRoomTests
{
    private const int PrimaryTicketItemId = 101_164;
    private const int CaptureTicketItemId = 101_859;

    [Theory]
    [InlineData(true, 0u, 100_000u, 10, 12, new[] { 3 }, false)]
    [InlineData(true, 50_000u, 10_000u, 10, 12, new[] { 3 }, false)]
    [InlineData(true, 1_000u, 100_000u, 100, 12, new[] { 3 }, false)]
    [InlineData(true, 1_000u, 100_000u, 10, 11, new[] { 3 }, false)]
    [InlineData(true, 1_000u, 100_000u, 10, 12, new int[0], false)]
    [InlineData(true, 1_000u, 100_000u, 10, 12, new[] { 3, 6 }, true)]
    [InlineData(false, 0u, 0u, 10, 12, new[] { 3 }, true)]
    public void OptionsValidateUserHostConfiguration(
        bool enabled, uint minPrize, uint maxPrize, int feePercent, int playableSeats, int[] allowedModes, bool valid)
    {
        var options = new InGameOptions
        {
            UserHostEnabled = enabled,
            UserHostMinPrize = minPrize,
            UserHostMaxPrize = maxPrize,
            UserHostFeePercent = feePercent,
            UserHostPlayableSeats = playableSeats,
            UserHostAllowedModes = allowedModes,
        };
        Assert.Equal(valid, !options.Validate().Any());
    }

    [Theory]
    [InlineData(36)]
    [InlineData(16)]
    public async Task CreatePrizeRoomSeatsHostAtRefereeSlotReservesTailAndPlacesGuestsInPlayableSlots(byte refereeTeam)
    {
        await using var rig = await Rig.CreateAsync(refereeTeam: refereeTeam);
        var host = await rig.CreatePlayerAsync(101, "Host", lucci: 50_000, tickets: 2);
        var g1 = await rig.CreatePlayerAsync(102, "Guest1", lucci: 0, tickets: 0);
        var g2 = await rig.CreatePlayerAsync(103, "Guest2", lucci: 0, tickets: 0);

        var roomId = await rig.CreatePrizeRoomAsync(host);
        var room = rig.Registry.TryGet(host.Id)!;
        lock (room.Gate)
        {
            Assert.True(room.IsUserHost);
            Assert.Equal(12, room.MasterSlot);
            Assert.Equal(12, room.UserHostRefereeSlot);
            Assert.Equal(refereeTeam, room.UserHostRefereeTeam);
            Assert.Equal(12, room.PlayableMaxSeats);
            var refSeat = Assert.Single(room.Seats);
            Assert.Equal(12, refSeat.Slot);
            Assert.Equal(refereeTeam, refSeat.Team);
        }

        // Published list exposes 12 open playable seats rather than 16.
        var listed = Assert.Single(rig.Registry.SnapshotListedRooms(matchRoomsOnly: false, default));
        Assert.Equal(12, listed.Entry.Capacity);
        Assert.True(listed.Entry.IsUserHost);

        // Reserved referee/trailing slots cannot be locked/closed.
        await rig.Flow.HandleSeatLockAsync(host, new(13, true), default);
        await rig.Flow.HandleSeatLockAsync(host, new(12, true), default);
        lock (room.Gate)
            Assert.DoesNotContain(13, room.ClosedSlots);

        await rig.JoinAsync(g1, roomId);
        await rig.JoinAsync(g2, roomId);
        lock (room.Gate)
        {
            Assert.Equal(0, room.FindByGame(g1.Id)!.Slot);
            Assert.Equal(1, room.FindByGame(g2.Id)!.Slot);
            Assert.NotEqual(refereeTeam, room.FindByGame(g1.Id)!.Team);
        }

        // AI and master transfer are refused in prize rooms.
        await rig.Flow.HandleMasterChangeAsync(host, new("Guest1"), default);
        Assert.Equal(1, Assert.Single(rig.MasterReplies.To(host)).Result);
        Assert.Equal(12, room.MasterSlot);

        await rig.Flow.HandleAiAddAsync(host, new(1, 0, 1, 1), default);
        Assert.Empty(room.Bots);
    }

    [Fact]
    public async Task CreateRequiresEnabledFlagTicketBalanceAndAllowedMode()
    {
        // Disabled fails to create.
        await using (var disabledRig = await Rig.CreateAsync(userHostEnabled: false))
        {
            var host = await disabledRig.CreatePlayerAsync(101, "Host", lucci: 50_000, tickets: 1);
            await disabledRig.Flow.HandleCreateAsync(host, new("Prize", "", 3, "rel_Village07_Easy02") { IsUserHost = true }, default);
            Assert.Null(disabledRig.Registry.TryGet(host.Id));
        }

        await using var rig = await Rig.CreateAsync();
        var poor = await rig.CreatePlayerAsync(201, "Poor", lucci: 500, tickets: 1);
        await rig.Flow.HandleCreateAsync(poor, new("Prize", "", 3, "rel_Village07_Easy02") { IsUserHost = true }, default);
        Assert.Null(rig.Registry.TryGet(poor.Id));

        var noTicket = await rig.CreatePlayerAsync(202, "NoTicket", lucci: 50_000, tickets: 0);
        await rig.Flow.HandleCreateAsync(noTicket, new("Prize", "", 3, "rel_Village07_Easy02") { IsUserHost = true }, default);
        Assert.Null(rig.Registry.TryGet(noTicket.Id));

        var wrongMode = await rig.CreatePlayerAsync(203, "WrongMode", lucci: 50_000, tickets: 1);
        await rig.Flow.HandleCreateAsync(wrongMode, new("Prize", "", 40, "rel_Village07_Easy02") { IsUserHost = true }, default);
        Assert.Null(rig.Registry.TryGet(wrongMode.Id));

        // Expired ticket is rejected while valid capture ticket (101859) is accepted.
        var expired = await rig.CreatePlayerAsync(204, "Expired", lucci: 50_000, tickets: 1,
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));
        await rig.Flow.HandleCreateAsync(expired, new("Prize", "", 3, "rel_Village07_Easy02") { IsUserHost = true }, default);
        Assert.Null(rig.Registry.TryGet(expired.Id));

        var captureHolder = await rig.CreatePlayerAsync(205, "Capture", lucci: 50_000, tickets: 0, captureTickets: 1);
        var createdId = await rig.CreatePrizeRoomAsync(captureHolder);
        Assert.True(createdId > 0);
    }

    [Fact]
    public async Task SettingUpdatesRespectHostBoundsAndQueryActions()
    {
        await using var rig = await Rig.CreateAsync(minPrize: 1_000, maxPrize: 20_000);
        var host = await rig.CreatePlayerAsync(101, "Host", lucci: 50_000, tickets: 1);
        var guest = await rig.CreatePlayerAsync(102, "Guest", lucci: 0, tickets: 0);
        var roomId = await rig.CreatePrizeRoomAsync(host);
        await rig.JoinAsync(guest, roomId);

        // Guest query returns current prize and bounds only to guest.
        await rig.Flow.HandleUserHostSettingAsync(guest, new((int)UserHostSettingAction.GuestQuery, 0), default);
        var gReply = Assert.Single(rig.UserHostSettings.To(guest));
        Assert.Equal((int)UserHostSettingAction.GuestQuery, gReply.Action);
        Assert.Equal(1_000u, gReply.Prize);

        // Guest cannot spoof ConfirmPrize or OpenEdit.
        await rig.Flow.HandleUserHostSettingAsync(guest, new((int)UserHostSettingAction.ConfirmPrize, 15_000), default);
        Assert.Single(rig.UserHostSettings.To(guest));
        Assert.Empty(rig.UserHostSettings.To(host));

        // Out-of-range host ConfirmPrize is rejected and synced back to 1,000.
        await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 999_999), default);
        var hostSync = Assert.Single(rig.UserHostSettings.To(host));
        Assert.Equal((int)UserHostSettingAction.HostQuery, hostSync.Action);
        Assert.Equal(1_000u, hostSync.Prize);

        // Valid ConfirmPrize broadcasts to host and guest.
        await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 12_000), default);
        Assert.Equal(12_000u, rig.UserHostSettings.To(host)[^1].Prize);
        Assert.Equal(12_000u, rig.UserHostSettings.To(guest)[^1].Prize);
        Assert.Equal((int)UserHostSettingAction.ConfirmPrize, rig.UserHostSettings.To(guest)[^1].Action);
    }

    [Fact]
    public async Task StartValidationCoversAllNegativeStatusCodesWithoutConsumingTicketOrLucci()
    {
        await using var rig = await Rig.CreateAsync(minPrize: 1_000, maxPrize: 50_000, countdownMs: 5_000);
        var host = await rig.CreatePlayerAsync(101, "Host", lucci: 5_000, tickets: 1);
        var r1 = await rig.CreatePlayerAsync(102, "Red1");
        var b1 = await rig.CreatePlayerAsync(103, "Blue1");
        var roomId = await rig.CreatePrizeRoomAsync(host);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.JoinAsync(r1, roomId);

        // -3: unbalanced teams (1 Red vs 0 Blue).
        await rig.Flow.HandleReadyAsync(r1, new(true), default);
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.InvalidTeamsOrReady, rig.UserHostStarts.To(host)[^1].Status);

        // -3: balanced teams (1 Red vs 1 Blue), but guest unready.
        await rig.JoinAsync(b1, roomId);
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.InvalidTeamsOrReady, rig.UserHostStarts.To(host)[^1].Status);

        // Ready both -> now -2 InsufficientLucci because prize 10,000 > host wallet 5,000.
        await rig.Flow.HandleReadyAsync(b1, new(true), default);
        await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 10_000), default);
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.InsufficientLucci, rig.UserHostStarts.To(host)[^1].Status);

        // Lower prize to 4,000, remove ticket -> -4 MissingTicket.
        await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 4_000), default);
        await rig.Inventory.ReplaceSlotsAsync(UserNumOf(host), InventoryBag.CharacterBag, [], default);
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.MissingTicket, rig.UserHostStarts.To(host)[^1].Status);

        // -5 PolicyRejected if prize exceeds bounds or mode disallowed; -1 InvalidState for non-master start click.
        lock (room.Gate)
            room.UserHostPrize = 999_999;
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.PolicyRejected, rig.UserHostStarts.To(host)[^1].Status);

        await rig.Flow.HandleStartAsync(r1, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.InvalidState, rig.UserHostStarts.To(r1)[^1].Status);

        // Nothing debited.
        Assert.Equal(5_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);
    }

    [Fact]
    public async Task CountdownCancelsOnUnreadyAndHostWaitingLeaveEvacuatesGuestsWithoutEscrow()
    {
        await using var rig = await Rig.CreateAsync(countdownMs: 5_000);
        var host = await rig.CreatePlayerAsync(101, "Host", lucci: 10_000, tickets: 1);
        var r1 = await rig.CreatePlayerAsync(102, "Red1");
        var b1 = await rig.CreatePlayerAsync(103, "Blue1");
        var roomId = await rig.CreatePrizeRoomAsync(host);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.JoinAsync(r1, roomId);
        await rig.JoinAsync(b1, roomId);
        await rig.Flow.HandleReadyAsync(r1, new(true), default);
        await rig.Flow.HandleReadyAsync(b1, new(true), default);

        // Host starts countdown -> everyone receives Started(0).
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
        Assert.Equal(UserHostStartStatus.Started, Assert.Single(rig.UserHostStarts.To(host)).Status);
        Assert.Equal(UserHostStartStatus.Started, Assert.Single(rig.UserHostStarts.To(r1)).Status);
        Assert.True(room.UserHostCountdownActive);

        // Guest unreadies -> countdown cancels before escrow.
        await rig.Flow.HandleReadyAsync(b1, new(false), default);
        Assert.False(room.UserHostCountdownActive);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Empty(rig.Starting.Sent);

        // Host leaves waiting room -> guests are evacuated cleanly without promoting a new master.
        await rig.Flow.HandleLeaveAsync(host, new(), default);
        Assert.Equal(RoomPhase.Closed, room.Phase);
        Assert.Null(rig.Registry.TryGet(host.Id));
        Assert.Null(rig.Registry.TryGet(r1.Id));
        Assert.Null(rig.Registry.TryGet(b1.Id));
        Assert.Single(rig.Left.To(r1));
        Assert.Single(rig.Left.To(b1));
        Assert.Equal(10_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);
    }

    [Fact]
    public async Task VictorySettlesNetPrizeAcrossOnlineAndOfflineWinnersWithDeterministicRemainder()
    {
        await using var rig = await Rig.CreateAsync(countdownMs: 30, feePercent: 10, minPrize: 100);
        var host = await rig.CreatePlayerAsync(101, "Host", lucci: 50_000, tickets: 2);
        var r1 = await rig.CreatePlayerAsync(102, "Red1", lucci: 1_000);
        var r2 = await rig.CreatePlayerAsync(103, "Red2", lucci: 1_000);
        var b1 = await rig.CreatePlayerAsync(104, "Blue1", lucci: 1_000);
        var b2 = await rig.CreatePlayerAsync(105, "Blue2", lucci: 1_000);

        var roomId = await rig.CreatePrizeRoomAsync(host);
        var room = rig.Registry.TryGet(host.Id)!;
        await rig.JoinAsync(r1, roomId);
        await rig.JoinAsync(b1, roomId);
        await rig.JoinAsync(r2, roomId);
        await rig.JoinAsync(b2, roomId);
        foreach (var g in new[] { r1, r2, b1, b2 })
            await rig.Flow.HandleReadyAsync(g, new(true), default);

        // Choose prize 901 -> 10% fee = 90 -> net pool = 811 -> split 2 ways = 406 (slot 0) + 405 (slot 2).
        await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 901), default);
        await rig.Flow.HandleStartAsync(host, UserHostStart(), default);

        // Wait for countdown completion to move the room to Starting and begin escrow.
        for (var i = 0; i < 60 && room.Phase == RoomPhase.Waiting; i++)
            await Task.Delay(15);

        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.True(room.UserHostMatchEscrowed);
        Assert.Equal(50_000 - 901, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);
        var remainingTickets = (await rig.Inventory.ListSlotsAsync(UserNumOf(host), InventoryBag.CharacterBag, 64, default))
            .Where(s => s.ItemId == PrimaryTicketItemId).Sum(s => s.Quantity);
        Assert.Equal(1, remainingTickets);
        Assert.Contains(rig.UserStats.To(host), s => s.Wallet.Lucci == 50_000 - 901);

        // Load all members to Playing, make Red win (1 kill), and simulate Red2 disconnecting after start while Red1 stays online.
        foreach (var member in new[] { host, r1, r2, b1, b2 })
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        for (var i = 0; i < 60 && room.Phase != RoomPhase.Playing; i++)
            await Task.Delay(10);
        Assert.Equal(RoomPhase.Playing, room.Phase);

        await r2.Connection.CloseAsync("network drop");
        rig.Online.Remove((int)r2.Id);
        await rig.Round.HandleKillCreditReportedAsync(r1, new(0, 1, 1), default);
        await rig.Round.HandleFinishingBlowReportedAsync(r1, new(0) { HasVictimReference = true, Victim = 1 }, default);
        await rig.Round.HandleEndRequestedAsync(host, new(), default);

        // Wait for settlement to finish (online Red1 credited immediately; offline Red2 gets pending claim).
        for (var i = 0; i < 60 && room.UserHostMatchEscrowed; i++)
            await Task.Delay(20);

        Assert.False(room.UserHostMatchEscrowed);
        // Online Red1 wallet: initial 1,000 + 406 share + any normal round lucci (>= 1,406; levels absent -> 1,406 exact!).
        Assert.Equal(1_406, (await rig.Wallets.FindByUserNumAsync(UserNumOf(r1), default))!.Lucci);
        // Offline Red2 wallet still 1,000 until login flush credits 405!
        Assert.Equal(1_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(r2), default))!.Lucci);
        var flush = await rig.Prizes.FlushPendingClaimsAsync("kr-1256", UserNumOf(r2), 10, 5_000, default);
        Assert.Equal(1, flush.ClaimedCount);
        Assert.Equal(405L, flush.CreditedLucci);
        Assert.Equal(1_405, (await rig.Wallets.FindByUserNumAsync(UserNumOf(r2), default))!.Lucci);
    }

    [Fact]
    public async Task DrawAndDisconnectCancellationRefundFullPrizeToOnlineOrOfflineHost()
    {
        // Case A: Draw in settled round refunds full prize to online host.
        await using (var rig = await Rig.CreateAsync(countdownMs: 5_000, feePercent: 10))
        {
            var host = await rig.CreatePlayerAsync(101, "Host", lucci: 10_000, tickets: 1);
            var r1 = await rig.CreatePlayerAsync(102, "Red1");
            var b1 = await rig.CreatePlayerAsync(103, "Blue1");
            var roomId = await rig.CreatePrizeRoomAsync(host);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.JoinAsync(r1, roomId);
            await rig.JoinAsync(b1, roomId);
            await rig.Flow.HandleReadyAsync(r1, new(true), default);
            await rig.Flow.HandleReadyAsync(b1, new(true), default);
            await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 4_000), default);

            // Simulate client handshake: gcpUserHostStart then gcpRoomReqEnterGame during active countdown.
            await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
            Assert.True(room.UserHostCountdownActive);
            await rig.Flow.HandleStartAsync(host, new RoomStartRequested { FromUserHostPacket = false }, default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(6_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);

            foreach (var member in new[] { host, r1, b1 })
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
            for (var i = 0; i < 60 && room.Phase != RoomPhase.Playing; i++)
                await Task.Delay(10);
            Assert.Equal(RoomPhase.Playing, room.Phase);

            // Zero scores -> draw -> full 4,000 Lucci refunded!
            await rig.Round.HandleEndRequestedAsync(host, new(), default);
            for (var i = 0; i < 60 && room.UserHostMatchEscrowed; i++)
                await Task.Delay(20);
            Assert.Equal(10_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);
        }

        // Case B: Host connection drop mid-round cancels & queues offline refund claim flushed on next login.
        await using (var rig = await Rig.CreateAsync(countdownMs: 5_000, cancelRoundOnDisconnect: true))
        {
            var host = await rig.CreatePlayerAsync(201, "HostDrop", lucci: 10_000, tickets: 1);
            var r1 = await rig.CreatePlayerAsync(202, "Red1");
            var b1 = await rig.CreatePlayerAsync(203, "Blue1");
            var roomId = await rig.CreatePrizeRoomAsync(host);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.JoinAsync(r1, roomId);
            await rig.JoinAsync(b1, roomId);
            await rig.Flow.HandleReadyAsync(r1, new(true), default);
            await rig.Flow.HandleReadyAsync(b1, new(true), default);
            await rig.Flow.HandleUserHostSettingAsync(host, new((int)UserHostSettingAction.ConfirmPrize, 3_500), default);
            await rig.Flow.HandleStartAsync(host, UserHostStart(), default);
            await rig.Flow.HandleStartAsync(host, new RoomStartRequested { FromUserHostPacket = false }, default);
            foreach (var member in new[] { host, r1, b1 })
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
            for (var i = 0; i < 60 && room.Phase != RoomPhase.Playing; i++)
                await Task.Delay(10);
            Assert.Equal(6_500, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);

            await host.Connection.CloseAsync("host crashed");
            rig.Online.Remove((int)host.Id);
            rig.Flow.OnConnectionClosed(new ConnectionClosedEvent(host.Connection, "host crashed"));

            for (var i = 0; i < 60 && room.UserHostMatchEscrowed; i++)
                await Task.Delay(20);
            Assert.False(room.UserHostMatchEscrowed);
            var flush = await rig.Prizes.FlushPendingClaimsAsync("kr-1256", UserNumOf(host), 10, 9_000, default);
            Assert.Equal(1, flush.ClaimedCount);
            Assert.Equal(3_500L, flush.CreditedLucci);
            Assert.Equal(10_000, (await rig.Wallets.FindByUserNumAsync(UserNumOf(host), default))!.Lucci);
        }
    }

    [Fact]
    public async Task StartupActivationRecoversActiveEscrowsIntoHostRefundClaims()
    {
        var table = new MemoryPlayerTable(8);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var wallets = new MemoryPlayerWalletRepository(table);
        var prizes = new MemoryUserHostPrizeStore(table);
        var hostRecord = await players.CreateAsync("hostcrash", "HostCrash", default);
        await wallets.GetOrCreateAsync(new(hostRecord.UserNum, 25_000, 0), default);
        await inventory.ReplaceSlotsAsync(hostRecord.UserNum, InventoryBag.CharacterBag,
            [new(hostRecord.UserNum, InventoryBag.CharacterBag, 1L, PrimaryTicketItemId, 1, 0)], default);

        var begin = await prizes.BeginEscrowAsync(new("kr-1256", "crashed-match-1", 42, hostRecord.UserNum, 7_500, 10, PrimaryTicketItemId, 1_000), default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, begin.Outcome);
        Assert.Equal(17_500, (await wallets.FindByUserNumAsync(hostRecord.UserNum, default))!.Lucci);

        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var disposables = new List<IDisposable>
        {
            services.Register<IPlayerRepository>(players),
            services.Register<IInventoryRepository>(inventory),
            services.Register<IPlayerWalletRepository>(wallets),
            services.Register<IUserHostPrizeStore>(prizes),
            services.RegisterMany<IUserHostDialectPolicy>(new FakeDialectPolicy("kr-1256/game", 36, 12)),
            services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new FakeBinding<RoomCreateRequested>()),
            services.RegisterMany<IInboundMessageBinding<RoomJoinRequested>>(new FakeBinding<RoomJoinRequested>()),
            services.RegisterMany<IInboundMessageBinding<RoomLeaveRequested>>(new FakeBinding<RoomLeaveRequested>()),
            services.RegisterMany<IInboundMessageBinding<RoomReadyRequested>>(new FakeBinding<RoomReadyRequested>()),
            services.RegisterMany<IInboundMessageBinding<RoomTeamChangeRequested>>(new FakeBinding<RoomTeamChangeRequested>()),
            services.RegisterMany<IInboundMessageBinding<RoomStartRequested>>(new FakeBinding<RoomStartRequested>()),
            services.RegisterMany<IInboundMessageBinding<UserHostSettingRequested>>(new FakeBinding<UserHostSettingRequested>()),
            services.RegisterMany<IMessageSender<RoomEntered>>(new FakeSender<RoomEntered>()),
            services.RegisterMany<IMessageSender<RoomSeatUpdated>>(new FakeSender<RoomSeatUpdated>()),
            services.RegisterMany<IMessageSender<RoomSeatVacated>>(new FakeSender<RoomSeatVacated>()),
            services.RegisterMany<IMessageSender<RoomMasterChanged>>(new FakeSender<RoomMasterChanged>()),
            services.RegisterMany<IMessageSender<RoomJoinRefused>>(new FakeSender<RoomJoinRefused>()),
            services.RegisterMany<IMessageSender<RoomLeft>>(new FakeSender<RoomLeft>()),
            services.RegisterMany<IMessageSender<RoomMatchStarting>>(new FakeSender<RoomMatchStarting>()),
            services.RegisterMany<IMessageSender<UserHostSettingUpdated>>(new FakeSender<UserHostSettingUpdated>()),
            services.RegisterMany<IMessageSender<UserHostDefaultsPublished>>(new FakeSender<UserHostDefaultsPublished>()),
            services.RegisterMany<IMessageSender<UserHostStartReplied>>(new FakeSender<UserHostStartReplied>()),
            services.RegisterMany<IMessageSender<UserStatUpdated>>(new FakeSender<UserStatUpdated>()),
        };
        var routerCtx = new PluginContext(new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" }, services, events, NullLogger.Instance);
        var inGameCtx = new PluginContext(new PluginRow
        {
            Id = "ingame",
            Plugin = "ingame",
            Source = "test",
            ClientBuild = new ClientBuild("kr", 1256, 0),
            Config = new Dictionary<string, object?>
            {
                ["game_protocol"] = "kr-1256/game",
                ["relay_protocol"] = "kr-1256/relay",
                ["user_host_enabled"] = true,
            },
        }, services, events, NullLogger.Instance);

        try
        {
            await new PacketRouterPlugin().ActivateAsync(routerCtx, default);
            await new MessageRouterPlugin().ActivateAsync(routerCtx, default);
            await new InGamePlugin().ActivateAsync(inGameCtx, default);

            var flushed = await prizes.FlushPendingClaimsAsync("kr-1256", hostRecord.UserNum, 10, 5_000, default);
            Assert.Equal(1, flushed.ClaimedCount);
            Assert.Equal(7_500L, flushed.CreditedLucci);
            Assert.Equal(25_000, (await wallets.FindByUserNumAsync(hostRecord.UserNum, default))!.Lucci);
        }
        finally
        {
            await inGameCtx.UnwindAsync();
            await routerCtx.UnwindAsync();
            foreach (var d in disposables) d.Dispose();
        }
    }

    private static RoomStartRequested UserHostStart() => new() { FromUserHostPacket = true };

    private static long UserNumOf(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player!.Identity.UserNum : throw new InvalidOperationException();

    private sealed class FakeDialectPolicy(string protocol, byte refereeTeam, int refereeSlot) : IUserHostDialectPolicy
    {
        public string Protocol => protocol;
        public byte RefereeTeam => refereeTeam;
        public int RefereeSlotIndex => refereeSlot;
    }

    private sealed class FakeBinding<T> : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
    }

    private sealed class FakeSender<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class OnlineRegistry : IOnlinePlayers
    {
        private readonly ConcurrentDictionary<int, ISession> _byUser = new();
        public void Set(int userNum, ISession session) => _byUser[userNum] = session;
        public void Remove(int userNum) => _byUser.TryRemove(userNum, out _);
        public ISession? FindByUserNum(int userNum) => _byUser.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => _byUser.Values.FirstOrDefault(s =>
            s.TryFeature<IPlayerSession>(out var p) && p.Identity.Nickname == nickname);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public MemoryPlayerTable Table { get; } = new(16);
        public MemoryPlayerRepository Players { get; }
        public MemoryInventoryRepository Inventory { get; }
        public MemoryPlayerWalletRepository Wallets { get; }
        public MemoryUserHostPrizeStore Prizes { get; }
        public OnlineRegistry Online { get; } = new();
        public RoomRegistry Registry { get; }
        public RoomFlow Flow { get; }
        public RoundFlow Round { get; }

        public RoomFlowTests.Sink<RoomEntered> Entered { get; } = new();
        public RoomFlowTests.Sink<RoomSeatUpdated> SeatUpdated { get; } = new();
        public RoomFlowTests.Sink<RoomSeatVacated> SeatVacated { get; } = new();
        public RoomFlowTests.Sink<RoomMasterChanged> MasterChanged { get; } = new();
        public RoomFlowTests.Sink<RoomMasterChangeReplied> MasterReplies { get; } = new();
        public RoomFlowTests.Sink<RoomJoinRefused> Refused { get; } = new();
        public RoomFlowTests.Sink<RoomLeft> Left { get; } = new();
        public RoomFlowTests.Sink<RoomMatchStarting> Starting { get; } = new();
        public RoomFlowTests.Sink<UserHostSettingUpdated> UserHostSettings { get; } = new();
        public RoomFlowTests.Sink<UserHostStartReplied> UserHostStarts { get; } = new();
        public RoomFlowTests.Sink<UserStatUpdated> UserStats { get; } = new();
        public RoomFlowTests.Sink<RoundPlayerLoaded> Loaded { get; } = new();
        public RoomFlowTests.Sink<RoundCountdownStarted> Countdown { get; } = new();
        public RoomFlowTests.Sink<RoundStarted> RoundStarted { get; } = new();
        public RoomFlowTests.Sink<RoundEnded> Ended { get; } = new();
        public RoomFlowTests.Sink<RoundScoreUpdated> Score { get; } = new();
        public RoomFlowTests.Sink<RoundScoreboard> Scoreboard { get; } = new();
        public RoomFlowTests.Sink<ResultStageAdvanced> Stage { get; } = new();
        public RoomFlowTests.Sink<RoundPlayerWalletChanged> WalletChanged { get; } = new();

        private Rig(InGameOptions options, byte refereeTeam)
        {
            Players = new MemoryPlayerRepository(Table);
            Inventory = new MemoryInventoryRepository(Table);
            Wallets = new MemoryPlayerWalletRepository(Table);
            Prizes = new MemoryUserHostPrizeStore(Table);
            Registry = new RoomRegistry(options);

            var senders = new RoomSenders(
                Entered, SeatUpdated, SeatVacated, MasterChanged, Refused, Left, Starting,
                SeatLock: new RoomFlowTests.Sink<RoomSeatLockChanged>(),
                Kicked: new RoomFlowTests.Sink<RoomMemberKicked>(),
                MasterChangeReply: MasterReplies);

            Flow = new RoomFlow(Registry, senders, NullLogger.Instance, null, null, 36, options, default, null, null, Online)
            {
                UserHostProfileId = "kr-1256",
                UserHostPolicy = new FakeDialectPolicy("test/game", refereeTeam, 12),
                UserHostPrizes = Prizes,
                UserHostInventory = Inventory,
                UserHostWallets = Wallets,
                UserHostPlayers = Players,
                UserHostSettingUpdatedSender = UserHostSettings,
                UserHostStartRepliedSender = UserHostStarts,
                UserHostStatUpdatedSender = UserStats,
            };

            var roundSenders = new RoundSenders(Loaded, Countdown, RoundStarted, Ended: Ended);
            var services = new ServiceRegistry();
            services.Register<IPlayerWalletRepository>(Wallets);
            services.RegisterMany<IMessageSender<RoundScoreUpdated>>(Score);
            services.RegisterMany<IMessageSender<RoundScoreboard>>(Scoreboard);
            services.RegisterMany<IMessageSender<RoundEnded>>(Ended);
            services.RegisterMany<IMessageSender<ResultStageAdvanced>>(Stage);
            services.RegisterMany<IMessageSender<RoundPlayerWalletChanged>>(WalletChanged);
            var pluginCtx = new PluginContext(
                new PluginRow { Id = "ingame", Plugin = "ingame", Source = "test" },
                services, new EventBus(NullLogger.Instance), NullLogger.Instance);

            Round = new RoundFlow(Registry, options, roundSenders, NullLogger.Instance, default, Flow.ReturnToRoomAsync, pluginCtx)
            {
                UserHostSettlement = Flow.SettleUserHostRoundAsync,
            };
        }

        public static Task<Rig> CreateAsync(
            bool userHostEnabled = true,
            uint minPrize = 1_000,
            uint maxPrize = 100_000,
            int feePercent = 10,
            int countdownMs = 5_000,
            byte refereeTeam = 36,
            bool cancelRoundOnDisconnect = false)
        {
            var options = new InGameOptions
            {
                GameProtocol = "test/game",
                UserHostEnabled = userHostEnabled,
                UserHostMinPrize = minPrize,
                UserHostMaxPrize = maxPrize,
                UserHostFeePercent = feePercent,
                UserHostCountdownMs = countdownMs,
                UserHostAllowedModes = [3],
                StartTimerMs = 0,
                ResultScoreboardDelayMs = 0,
                ResultRewardDelayMs = 0,
                ResultCloseGraceMs = 500,
                CancelRoundOnDisconnect = cancelRoundOnDisconnect,
            };
            return Task.FromResult(new Rig(options, refereeTeam));
        }

        public async Task<RecordingSession> CreatePlayerAsync(
            long id, string nickname, int lucci = 0, int tickets = 0, int captureTickets = 0, DateTimeOffset? expiresAtUtc = null)
        {
            var record = await Players.CreateAsync(nickname.ToLowerInvariant(), nickname, default);
            await Wallets.GetOrCreateAsync(new(record.UserNum, lucci, 0), default);
            var slots = new List<InventorySlotRecord>();
            if (tickets > 0)
            {
                slots.Add(new InventorySlotRecord(record.UserNum, InventoryBag.CharacterBag, 1L, PrimaryTicketItemId, tickets, 0)
                {
                    Lifetime = expiresAtUtc is { } exp ? new InventoryLifetime(InventoryLifetimeKind.ExpiresAt, exp.ToUnixTimeMilliseconds()) : default,
                });
            }
            if (captureTickets > 0)
            {
                slots.Add(new InventorySlotRecord(record.UserNum, InventoryBag.CharacterBag, 2L, CaptureTicketItemId, captureTickets, 0));
            }
            if (slots.Count > 0)
                await Inventory.ReplaceSlotsAsync(record.UserNum, InventoryBag.CharacterBag, slots, default);

            var session = new RecordingSession(id, new ClientProfile("test", "kr", 1256, 0, 0, "test"));
            session.SetLobbyPlayer(record.UserNum, nickname, level: 5);
            Online.Set((int)record.UserNum, session);
            return session;
        }

        public async Task<int> CreatePrizeRoomAsync(ISession host)
        {
            await Flow.HandleCreateAsync(host, new RoomCreateRequested("Prize Cup", "", 3, "rel_Village07_Easy02") { IsUserHost = true }, default);
            return Entered.To(host)[^1].Room.RoomId;
        }

        public Task JoinAsync(ISession guest, int roomId) =>
            Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default).AsTask();

        public async ValueTask DisposeAsync() => await Registry.StopAllAsync();
    }
}
