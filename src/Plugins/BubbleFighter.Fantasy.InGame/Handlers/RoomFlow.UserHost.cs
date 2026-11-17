using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal IUserHostDialectPolicy? UserHostPolicy { get; set; }
    internal IUserHostPrizeStore? UserHostPrizes { get; set; }
    internal string UserHostProfileId { get; set; } = string.Empty;
    internal string UserHostNodeId { get; set; } = string.Empty;
    internal IInventoryRepository? UserHostInventory { get; set; }
    internal IInventoryPushService? UserHostInventoryPush { get; set; }
    internal IPlayerWalletRepository? UserHostWallets { get; set; }
    internal IPlayerRepository? UserHostPlayers { get; set; }
    internal IMessageSender<UserHostSettingUpdated>? UserHostSettingUpdatedSender { get; set; }
    internal IMessageSender<UserHostStartReplied>? UserHostStartRepliedSender { get; set; }
    internal IMessageSender<UserStatUpdated>? UserHostStatUpdatedSender { get; set; }

    private bool IsUserHostEnabled => _options?.UserHostEnabled == true
        && UserHostPolicy is not null
        && UserHostPrizes is not null
        && UserHostInventory is not null
        && UserHostWallets is not null;

    private bool IsUserHostModeAllowed(int modeId) => _options?.UserHostAllowedModes.Contains(modeId) == true;

    /// <summary>Handles a waiting-room prize query or host prize update (<c>gcpUserHostSetting</c>).</summary>
    public async ValueTask HandleUserHostSettingAsync(ISession session, UserHostSettingRequested request, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogInformation("Session {Id} sent gcpUserHostSetting while outside any room; ignored.", session.Id);
            return;
        }
        var sender = UserHostSettingUpdatedSender;
        if (sender is null || !IsUserHostEnabled)
        {
            _logger.LogWarning("Session {Id} sent gcpUserHostSetting while user-host rooms are disabled; ignored.", session.Id);
            return;
        }

        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] recipients = [];
            UserHostSettingUpdated? reply = null;
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is null || !room.IsUserHost || room.Phase != RoomPhase.Waiting)
                    return;

                var min = _options!.UserHostMinPrize;
                var max = _options.UserHostMaxPrize;
                switch ((UserHostSettingAction)request.Action)
                {
                    case UserHostSettingAction.OpenEdit:
                    case UserHostSettingAction.HostQuery:
                        if (seat.Slot != room.MasterSlot || !room.IsRefereeSeat(seat))
                        {
                            _logger.LogWarning("Session {Id} sent host prize action {Action} in room {Room} without referee ownership.", session.Id, request.Action, room.Id);
                            return;
                        }
                        if (request.Action == (int)UserHostSettingAction.OpenEdit && room.UserHostCountdownActive)
                            return;
                        recipients = [seat];
                        reply = new UserHostSettingUpdated(request.Action, room.UserHostPrize, min, max);
                        break;

                    case UserHostSettingAction.GuestQuery:
                        recipients = [seat];
                        reply = new UserHostSettingUpdated((int)UserHostSettingAction.GuestQuery, room.UserHostPrize, min, max);
                        break;

                    case UserHostSettingAction.ConfirmPrize:
                        if (seat.Slot != room.MasterSlot || !room.IsRefereeSeat(seat))
                        {
                            _logger.LogWarning("Session {Id} attempted to change prize in room {Room} without referee ownership.", session.Id, room.Id);
                            return;
                        }
                        if (room.UserHostCountdownActive)
                            return;
                        if (request.Prize < min || request.Prize > max)
                        {
                            _logger.LogWarning("Session {Id} proposed out-of-range prize {Prize} (allowed {Min}..{Max}) in room {Room}.",
                                session.Id, request.Prize, min, max, room.Id);
                            recipients = [seat];
                            reply = new UserHostSettingUpdated((int)UserHostSettingAction.HostQuery, room.UserHostPrize, min, max);
                            break;
                        }
                        room.UserHostPrize = request.Prize;
                        recipients = [.. room.Seats];
                        reply = new UserHostSettingUpdated((int)UserHostSettingAction.ConfirmPrize, room.UserHostPrize, min, max);
                        break;

                    default:
                        _logger.LogWarning("Session {Id} sent unknown user-host setting action {Action} in room {Room}.", session.Id, request.Action, room.Id);
                        return;
                }
            }

            if (reply is null)
                return;
            foreach (var recipient in recipients)
                await NotifyScenePeerAsync(recipient.Game, sender, reply, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> ValidateUserHostCreateAsync(ISession session, long userNum, int modeId, CancellationToken ct)
    {
        if (!IsUserHostEnabled)
        {
            _logger.LogWarning("Session {Id} requested a user-host prize room while user_host_enabled is false.", session.Id);
            return false;
        }
        if (!IsUserHostModeAllowed(modeId))
        {
            _logger.LogWarning("Session {Id} requested user-host prize room in unsupported mode {Mode}.", session.Id, modeId);
            return false;
        }
        var wallet = await UserHostWallets!.FindByUserNumAsync(userNum, ct).ConfigureAwait(false);
        if ((wallet?.Lucci ?? 0) < _options!.UserHostMinPrize)
        {
            _logger.LogWarning("Session {Id} (user {UserNum}) has insufficient Lucci ({Lucci} < {Min}) to host a prize room.",
                session.Id, userNum, wallet?.Lucci ?? 0, _options.UserHostMinPrize);
            return false;
        }
        if (await FindActiveUserHostTicketItemIdAsync(userNum, ct).ConfigureAwait(false) is null)
        {
            _logger.LogWarning("Session {Id} (user {UserNum}) holds no active host ticket to create a prize room.", session.Id, userNum);
            return false;
        }
        return true;
    }

    private async ValueTask<int?> FindActiveUserHostTicketItemIdAsync(long userNum, CancellationToken ct)
    {
        if (UserHostInventory is null || _options is null)
            return null;
        var now = DateTimeOffset.UtcNow;
        var bag = await UserHostInventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        if (HasActiveItem(bag, _options.UserHostTicketItemId, now))
            return _options.UserHostTicketItemId;
        if (_options.UserHostCaptureTicketItemId > 0 && HasActiveItem(bag, _options.UserHostCaptureTicketItemId, now))
            return _options.UserHostCaptureTicketItemId;
        return null;
    }

    private static bool HasActiveItem(IReadOnlyList<InventorySlotRecord> bag, int itemId, DateTimeOffset now)
    {
        foreach (var slot in bag)
        {
            if (slot.ItemId == itemId && slot.Quantity > 0 && !InventoryExpiry.IsExpired(slot, now))
                return true;
        }
        return false;
    }

    private async ValueTask SendUserHostStartReplyAsync(ISession session, int status, CancellationToken ct)
    {
        if (UserHostStartRepliedSender is { } replySender)
            await NotifyScenePeerAsync(session, replySender, new UserHostStartReplied(status), ct).ConfigureAwait(false);
    }

    private sealed record UserHostValidationOutcome(
        int Status,
        RoomSeat[] Members,
        uint Prize,
        long HostUserNum,
        int TicketItemId,
        RoomSeat? HostSeat);

    private async ValueTask<UserHostValidationOutcome> ValidateUserHostPreStartAsync(
        PlayRoom room, ISession session, RoomStartRequested request, bool allowExistingCountdown, CancellationToken ct)
    {
        RoomSeat[] members;
        RoomSeat? hostSeat;
        uint prize;
        long hostUserNum;
        lock (room.Gate)
        {
            hostSeat = room.FindByGame(session.Id);
            if (!IsUserHostEnabled
                || !room.IsUserHost
                || room.Phase != RoomPhase.Waiting
                || hostSeat is null
                || hostSeat.Slot != room.MasterSlot
                || !room.IsRefereeSeat(hostSeat)
                || room.UserHostMatchEscrowed
                || room.PendingSelections.Count != 0
                || (!allowExistingCountdown && room.UserHostCountdownActive))
            {
                return new(UserHostStartStatus.InvalidState, [], 0, 0, 0, null);
            }

            if (PolicyHost is { Active: true } policies
                && policies.EvaluateStart(room, session.Id, automaticStart: false, resumedStart: false, roomHeld: false) is not null)
            {
                return new(UserHostStartStatus.InvalidState, [], 0, 0, 0, null);
            }

            var min = _options!.UserHostMinPrize;
            var max = _options.UserHostMaxPrize;
            if (!IsUserHostModeAllowed(room.ModeId) || room.UserHostPrize < min || room.UserHostPrize > max)
                return new(UserHostStartStatus.PolicyRejected, [], 0, 0, 0, null);

            if (!room.CanStart)
                return new(UserHostStartStatus.InvalidTeamsOrReady, [], 0, 0, 0, null);

            var red = 0;
            var blue = 0;
            foreach (var competitor in room.Seats)
            {
                if (room.IsRefereeSeat(competitor))
                    continue;
                if (competitor.Team == 1)
                    red++;
                else if (competitor.Team == 2)
                    blue++;
                else
                    return new(UserHostStartStatus.InvalidTeamsOrReady, [], 0, 0, 0, null);
            }
            if (red == 0 || red != blue)
                return new(UserHostStartStatus.InvalidTeamsOrReady, [], 0, 0, 0, null);

            var checkMap = request.MapKey.Length > 0 ? request.MapKey : room.MapKey;
            if (HostsRoundMap?.Invoke(room, checkMap) == false)
                return new(UserHostStartStatus.InvalidState, [], 0, 0, 0, null);

            members = [.. room.Seats];
            prize = room.UserHostPrize;
            hostUserNum = hostSeat.UserNum;
        }

        var wallet = await UserHostWallets!.FindByUserNumAsync(hostUserNum, ct).ConfigureAwait(false);
        if ((wallet?.Lucci ?? 0) < prize)
            return new(UserHostStartStatus.InsufficientLucci, [], 0, 0, 0, null);

        var ticketId = await FindActiveUserHostTicketItemIdAsync(hostUserNum, ct).ConfigureAwait(false);
        if (ticketId is null)
            return new(UserHostStartStatus.MissingTicket, [], 0, 0, 0, null);

        return new(UserHostStartStatus.Started, members, prize, hostUserNum, ticketId.Value, hostSeat);
    }

    private async ValueTask ArmUserHostCountdownAsync(PlayRoom room, ISession session, RoomStartRequested request, CancellationToken ct)
    {
        var check = await ValidateUserHostPreStartAsync(room, session, request, allowExistingCountdown: false, ct).ConfigureAwait(false);
        if (check.Status != UserHostStartStatus.Started)
        {
            await SendUserHostStartReplyAsync(session, check.Status, ct).ConfigureAwait(false);
            return;
        }

        CancellationTokenSource cts;
        int generation;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting || room.UserHostMatchEscrowed || room.UserHostCountdownActive)
            {
                _ = SendUserHostStartReplyAsync(session, UserHostStartStatus.InvalidState, ct);
                return;
            }
            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            room.UserHostCountdownCancellation = cts;
            generation = room.UserHostCountdownGeneration;
        }

        if (UserHostStartRepliedSender is { } sender)
        {
            var notice = new UserHostStartReplied(UserHostStartStatus.Started);
            foreach (var member in check.Members)
                await NotifyScenePeerAsync(member.Game, sender, notice, ct).ConfigureAwait(false);
        }

        var delayMs = _options!.UserHostCountdownMs;
        room.UserHostCountdownTask = BackgroundWork.Detached(() => CompleteUserHostCountdownAsync(room, session, request, generation, delayMs, cts));
    }

    private async Task CompleteUserHostCountdownAsync(
        PlayRoom room, ISession session, RoomStartRequested request, int generation, int delayMs, CancellationTokenSource cts)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested)
                return;
            await room.Executor.ExecuteAsync(operationCt =>
            {
                lock (room.Gate)
                {
                    if (cts.IsCancellationRequested || room.UserHostCountdownGeneration != generation || room.Phase != RoomPhase.Waiting)
                        return ValueTask.CompletedTask;
                }
                return StartRoomCoreAsync(room, session, request, operationCt, fromUserHostCountdown: true);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Room {Room}: user-host pre-start countdown failed.", room.Id);
        }
        finally
        {
            lock (room.Gate)
            {
                if (ReferenceEquals(room.UserHostCountdownCancellation, cts))
                    room.UserHostCountdownCancellation = null;
            }
            cts.Dispose();
        }
    }

    private async ValueTask<bool> CommitUserHostEscrowAndMarkStartingAsync(
        PlayRoom room, ISession session, RoomStartRequested request, CancellationToken ct)
    {
        lock (room.Gate)
            room.CancelUserHostCountdown();

        var check = await ValidateUserHostPreStartAsync(room, session, request, allowExistingCountdown: true, ct).ConfigureAwait(false);
        if (check.Status != UserHostStartStatus.Started || check.HostSeat is null)
        {
            await SendUserHostStartReplyAsync(session, check.Status, ct).ConfigureAwait(false);
            return false;
        }

        var matchId = Guid.NewGuid().ToString("N");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var escrow = await UserHostPrizes!.BeginEscrowAsync(new UserHostEscrowBeginCommand(
            UserHostProfileId,
            matchId,
            room.Id,
            check.HostUserNum,
            checked((int)check.Prize),
            _options!.UserHostFeePercent,
            check.TicketItemId,
            nowMs) { NodeId = UserHostNodeId }, ct).ConfigureAwait(false);

        if (escrow.Outcome != UserHostEscrowBeginOutcome.Committed)
        {
            var mapped = escrow.Outcome switch
            {
                UserHostEscrowBeginOutcome.InsufficientLucci => UserHostStartStatus.InsufficientLucci,
                UserHostEscrowBeginOutcome.MissingTicket => UserHostStartStatus.MissingTicket,
                _ => UserHostStartStatus.InvalidState,
            };
            await SendUserHostStartReplyAsync(session, mapped, ct).ConfigureAwait(false);
            return false;
        }

        var started = false;
        lock (room.Gate)
        {
            if (!room.IsStopping && room.Phase == RoomPhase.Waiting && !room.UserHostMatchEscrowed)
            {
                room.UserHostStartingCompetitors = room.Seats
                    .Where(seat => !room.IsRefereeSeat(seat) && seat.UserNum > 0)
                    .OrderBy(seat => seat.Slot)
                    .Select(seat => new UserHostCompetitor(seat.UserNum, seat.Team, seat.Slot))
                    .ToArray();
                room.UserHostMatchEscrowed = true;
                room.MarkStarting(matchId);
                started = true;
            }
        }

        if (!started)
        {
            await UserHostPrizes.SettleEscrowAsync(new UserHostSettleCommand(
                UserHostProfileId, matchId, IsDrawOrCancelled: true, [], CollectOnlineUserNums(room),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (UserHostInventoryPush is not null && !session.Connection.IsClosed)
        {
            try
            {
                await UserHostInventoryPush.PushInventoryAsync(session, check.HostUserNum, ct).ConfigureAwait(false);
            }
            catch (IOException failure)
            {
                _logger.LogInformation(failure, "Room {Room}: could not push updated ticket inventory to host.", room.Id);
            }
        }
        await PushUserHostStatAsync(session, check.HostSeat, escrow.HostLucciAfter, ct).ConfigureAwait(false);
        return true;
    }

    private async ValueTask PushUserHostStatAsync(ISession session, RoomSeat seat, int lucci, CancellationToken ct)
    {
        if (UserHostStatUpdatedSender is null || session.Connection.IsClosed || seat.UserNum <= 0)
            return;
        try
        {
            var wallet = await UserHostWallets!.FindByUserNumAsync(seat.UserNum, ct).ConfigureAwait(false)
                ?? new PlayerWallet(seat.UserNum, lucci, 0);
            var player = (UserHostPlayers is null ? null : await UserHostPlayers.FindByUserNumAsync(seat.UserNum, ct).ConfigureAwait(false))
                ?? new PlayerRecord(seat.UserNum, seat.Nickname, seat.Nickname, Math.Max(1, seat.Level)) { ColorIndex = seat.ColorIndex };
            await NotifyScenePeerAsync(session, UserHostStatUpdatedSender, new UserStatUpdated(player, wallet with { Lucci = lucci }), ct)
                .ConfigureAwait(false);
        }
        catch (IOException failure)
        {
            _logger.LogInformation(failure, "User {UserNum}: could not push user stat wallet update.", seat.UserNum);
        }
    }

    private long[] CollectOnlineUserNums(PlayRoom room, IEnumerable<long>? extraCandidates = null)
    {
        var ids = new HashSet<long>();
        foreach (var seat in room.Seats)
        {
            if (seat.UserNum > 0 && !seat.Game.Connection.IsClosed)
                ids.Add(seat.UserNum);
        }
        if (room.UserHostHostUserNum > 0 && _online?.FindByUserNum(checked((int)room.UserHostHostUserNum)) is { Connection.IsClosed: false })
            ids.Add(room.UserHostHostUserNum);
        if (extraCandidates is not null)
        {
            foreach (var candidate in extraCandidates)
            {
                if (candidate > 0 && _online?.FindByUserNum(checked((int)candidate)) is { Connection.IsClosed: false })
                    ids.Add(candidate);
            }
        }
        return [.. ids];
    }

    /// <summary>
    /// Settles an escrowed prize match once upon round end or refunds the full prize upon cancellation or draw.
    /// </summary>
    internal async ValueTask SettleUserHostRoundAsync(
        PlayRoom room, int generation, int? winnerTeam, IReadOnlySet<int>? winnerSlots, string? cancellationReason, CancellationToken ct)
    {
        if (!IsUserHostEnabled)
            return;

        string matchId;
        uint prize;
        int feePercent;
        long hostUserNum;
        UserHostCompetitor[] competitors;
        long[] onlineUsers;
        RoomSeat? connectedHostSeat;

        lock (room.Gate)
        {
            if (!room.IsUserHost || !room.UserHostMatchEscrowed || room.StartGeneration != generation
                || room.UserHostSettledGeneration == generation || string.IsNullOrWhiteSpace(room.MatchId))
            {
                return;
            }
            room.UserHostSettledGeneration = generation;
            matchId = room.MatchId;
            prize = room.UserHostPrize;
            feePercent = room.UserHostFeePercent;
            hostUserNum = room.UserHostHostUserNum;
            competitors = [.. (room.UserHostStartingCompetitors ?? [])];
            onlineUsers = CollectOnlineUserNums(room, competitors.Select(c => c.UserNum));
            connectedHostSeat = room.Seats.FirstOrDefault(s => s.UserNum == hostUserNum && !s.Game.Connection.IsClosed);
        }

        List<long> winners = [];
        if (cancellationReason is null)
        {
            foreach (var competitor in competitors)
            {
                var won = winnerSlots is not null
                    ? winnerSlots.Contains(competitor.Slot)
                    : winnerTeam is > 0 && competitor.Team == winnerTeam.Value;
                if (won && !winners.Contains(competitor.UserNum))
                    winners.Add(competitor.UserNum);
            }
        }

        var isDrawOrCancelled = cancellationReason is not null || winners.Count == 0;
        UserHostWinnerAllocation[] allocations = [];
        if (!isDrawOrCancelled)
        {
            var gross = (long)prize;
            var fee = checked((int)((gross * feePercent) / 100L));
            var netPool = checked((int)(gross - fee));
            var baseShare = netPool / winners.Count;
            var remainder = netPool % winners.Count;
            allocations = new UserHostWinnerAllocation[winners.Count];
            for (var i = 0; i < winners.Count; i++)
                allocations[i] = new UserHostWinnerAllocation(winners[i], baseShare + (i < remainder ? 1 : 0));
        }

        UserHostSettleResult settlement;
        try
        {
            settlement = await UserHostPrizes!.SettleEscrowAsync(new UserHostSettleCommand(
                UserHostProfileId,
                matchId,
                isDrawOrCancelled,
                allocations,
                onlineUsers,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (room.Gate)
            {
                if (room.StartGeneration == generation)
                    room.UserHostMatchEscrowed = false;
            }
        }

        _logger.LogInformation(
            "Room {Room} match {Match}: user-host escrow {Outcome} (drawOrCancelled={Refund}, gross={Prize}, recipients={Count}, reason={Reason}).",
            room.Id, matchId, settlement.Outcome, isDrawOrCancelled, prize, settlement.Payouts.Count, cancellationReason ?? "normal_end");

        if (isDrawOrCancelled && connectedHostSeat is not null && !connectedHostSeat.Game.Connection.IsClosed)
        {
            var hostPayout = settlement.Payouts.FirstOrDefault(p => p.UserNum == hostUserNum);
            if (hostPayout is { CreditedImmediately: true, WalletLucciAfter: { } walletAfter })
                await PushUserHostStatAsync(connectedHostSeat.Game, connectedHostSeat, walletAfter, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EvacuateWaitingUserHostGuestsAsync(PlayRoom room, RoomView preLeaveView, CancellationToken ct)
    {
        (RoomSeat Seat, RoomMemberView LeftView)[] evacuated;
        lock (room.Gate)
        {
            if (!room.IsUserHost || room.Seats.Count == 0)
                return;
            evacuated = room.Seats.Select(seat => (seat, View(seat))).ToArray();
        }

        foreach (var (seat, _) in evacuated)
        {
            _registry.Detach(seat.Game.Id);
            ForgetCoupleRoster(seat.Game, room);
        }

        foreach (var (seat, leftView) in evacuated)
        {
            if (seat.Game.Connection.IsClosed)
                continue;
            try
            {
                ScenePhase.Entered(seat.Game, SessionPhase.Plaza, _logger);
                if (_left is not null)
                    await _left.SendAsync(seat.Game, new RoomLeft(room.Id, leftView) { Room = preLeaveView }, ct).ConfigureAwait(false);
                if (PlazaBound)
                    await EnterPlazaAsync(seat.Game, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException)
            {
                _logger.LogInformation("Room {Room}: guest slot {Slot} closed while evacuating user-host room.", room.Id, seat.Slot);
            }
        }
    }
}
