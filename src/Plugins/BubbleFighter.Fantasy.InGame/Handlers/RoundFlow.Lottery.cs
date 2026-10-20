using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly ConcurrentDictionary<int, LotteryState> _lotteries = new();
    private IMessageSender<RoundLotteryOpened>? _lotteryOpened;
    private IMessageSender<RoundLotteryClosed>? _lotteryClosed;
    private IMessageSender<RoundLotterySelected>? _lotterySelected;
    private IMessageSender<RoundLotteryRevealed>? _lotteryRevealed;
    private IShopPurchaseStore? _lotteryStore;
    private IInventoryRepository? _lotteryInventory;
    private IInventoryPushService? _lotteryPush;

    private void ConfigureLottery(IPluginContext? context)
    {
        if (_options.LotteryModes.Length == 0) return;
        if (context is null || _result is null || _readWallet is null
            || !context.Services.TryGet<IShopPurchaseStore>(out _lotteryStore)
            || !context.Services.TryGet<IInventoryRepository>(out _lotteryInventory)
            || !context.Services.TryGet<IInventoryPushService>(out _lotteryPush))
            throw new ConfigException("Reward cards require result, wallet, transactional grant storage and inventory push services.");
        _lotteryOpened = PlaySenders.Optional<RoundLotteryOpened>(context, _options.GameProtocol)
            ?? throw new ConfigException("Reward cards require a dialog sender.");
        _lotteryClosed = PlaySenders.Optional<RoundLotteryClosed>(context, _options.GameProtocol)
            ?? throw new ConfigException("Reward cards require a native result-close sender.");
        _lotterySelected = PlaySenders.Optional<RoundLotterySelected>(context, _options.GameProtocol)
            ?? throw new ConfigException("Reward cards require a selection sender.");
        _lotteryRevealed = PlaySenders.Optional<RoundLotteryRevealed>(context, _options.GameProtocol)
            ?? throw new ConfigException("Reward cards require a reveal sender.");
        if (_options.LotteryWorkbookClientPath.Length == 0)
            LotteryCatalog.Validate(_options.LotteryWorkbookPath, _options.LotteryItems);
        else if (context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var data) && data is not null)
            LotteryCatalog.Validate(data.Read(_options.LotteryWorkbookClientPath), _options.LotteryItems);
        else
            throw new ConfigException("lottery_workbook_client_path requires a clientdata row for this build.");
    }

    private int LotteryAwardItem(int roomId, int generation, int seat) =>
        _lotteries.TryGetValue(roomId, out var state) && state.Generation == generation
            ? state.Awards.GetValueOrDefault(seat) : 0;

    /// <summary>
    /// Whether a round that ended for <paramref name="reason"/> in <paramref name="modeId"/> may open the reward
    /// dialog. Only a cleared wave round does: the client opens its award dialog on S2C gspGameAwardDialog alone
    /// (kr-812 sub_7FBBB0 case 734 raises event 176, whose only handler sub_A18EC0 builds DlgHordeAward), so a
    /// failure, a time-out or an ordinary end must never be followed by that packet.
    /// </summary>
    internal static bool LotteryAllowed(InGameOptions options, int modeId, RoundEndReason reason) =>
        reason == RoundEndReason.WavesCleared && options.LotteryModes.Contains(modeId);

    private async ValueTask<bool> BeginLotteryAsync(PlayRoom room, int generation, RoundEndReason reason,
        IReadOnlyDictionary<int, RoundReward> rewards, CancellationToken ct)
    {
        if (_lotteryOpened is null || !LotteryAllowed(_options, room.ModeId, reason))
            return false;
        var state = new LotteryState(generation, Snapshot(room, generation), _options.LotteryGroupedDraw ? GroupedLottery.Draw(_options) : DrawLotteryCards(_options.LotteryItems), rewards, reason);
        if (state.Members.Length > 10)
            throw new ConfigException("Native reward cards support at most ten participating players.");
        if (!_lotteries.TryAdd(room.Id, state))
            throw new InvalidOperationException($"Room {room.Id} already has active reward cards.");
        try
        {
            await SendToMembersAsync(room, generation, (seat, token) =>
                _lotteryOpened.SendAsync(seat.Game, new(), token), ct).ConfigureAwait(false);
        }
        catch
        {
            _lotteries.TryRemove(room.Id, out _);
            throw;
        }
        // Selection requests must enter the executor while the dialog is open; waiting here blocks them.
        room.CountdownTask = BackgroundWork.Detached(() => FinishLotteryAsync(room, state));
        _logger.LogInformation("Room {Room}: native reward cards opened for {Players} players.", room.Id, state.Members.Length);
        return true;
    }

    internal static LotteryItem[] DrawLotteryCards(IReadOnlyList<LotteryItem> pool)
    {
        if (pool.Count == 0) throw new ConfigException("Reward-card pool is empty.");
        var total = pool.Sum(item => item.Weight);
        var cards = new LotteryItem[10]; // cn-324 sub_40B3D0 resets exactly ten cards.
        for (var card = 0; card < cards.Length; card++)
        {
            var ticket = Random.Shared.Next(total);
            foreach (var item in pool)
            {
                ticket -= item.Weight;
                if (ticket < 0) { cards[card] = item; break; }
            }
        }
        return cards;
    }

    /// <summary>Checks the claimed seat and card, commits one reward, then acknowledges its native selection.</summary>
    public async ValueTask HandleLotteryRequestedAsync(ISession session, RoundLotteryRequested request, CancellationToken ct)
    {
        if (_lotterySelected is null || _registry.TryGet(session.Id) is not { } room) return;
        await room.Executor.ExecuteAsync(async token =>
        {
            var member = room.FindByGame(session.Id);
            if (member is null || request.Seat != member.Slot || !_lotteries.TryGetValue(room.Id, out var state)
                || room.Phase != RoomPhase.Result || room.StartGeneration != state.Generation
                || state.Revealed || request.Card is < 0 or >= 10 || state.Awards.ContainsKey(member.Slot)
                || state.Claimed.Contains(request.Card))
            {
                await _lotterySelected.SendAsync(session, new(request.Card, request.Seat, true), token).ConfigureAwait(false);
                return;
            }
            await SelectLotteryCardAsync(room, state, member, request.Card, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask SelectLotteryCardAsync(PlayRoom room, LotteryState state, RoomSeat member, int card, CancellationToken ct)
    {
        var prize = state.Cards[card];
        var result = await GrantLotteryItemAsync(member, prize, ct).ConfigureAwait(false);
        // Record the committed selection before any wire write; a disconnected client cannot cause a second grant.
        state.Claimed.Add(card);
        state.Awards.Add(member.Slot, prize.DisplayId);
        _logger.LogInformation("Room {Room} seat {Seat}: reward card {Card} committed item {Item} quantity {Count} duration {Days} days.",
            room.Id, member.Slot, card, prize.ItemId, prize.Quantity, prize.Days);
        if (!member.Game.Connection.IsClosed && result.Grants.Count > 0)
            await _lotteryPush!.PushPurchaseAsync(member.Game, member.UserNum, result.Grants, ct).ConfigureAwait(false);
        if (!member.Game.Connection.IsClosed && prize.Lucci > 0 && _result?.Wallet is { } walletSender)
            await walletSender.SendAsync(member.Game, new RoundPlayerWalletChanged(member.UserNum,
                result.Wallet.Lucci, result.Wallet.Cash, state.Rewards.GetValueOrDefault(member.Slot)?.Level ?? member.Level), ct).ConfigureAwait(false);
        // cn-324 sub_40B550 reads the award from ScoreData, not from the selection reply.
        await SendScoreboardAsync(room, state.Generation, state.Rewards, true, ct, state.Reason).ConfigureAwait(false);
        await SendToMembersAsync(room, state.Generation, (seat, token) =>
            _lotterySelected!.SendAsync(seat.Game, new(card, member.Slot), token), ct).ConfigureAwait(false);
    }

    private async ValueTask<ShopPurchaseResult> GrantLotteryItemAsync(RoomSeat member, LotteryItem prize, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.LotteryGrantRetryCount; attempt++)
        {
            var wallet = await _readWallet!(member.UserNum, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Reward player {member.UserNum} has no wallet.");
            var rows = await _lotteryInventory!.ListSlotsAsync(member.UserNum, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            var components = prize.Components.Length > 0 ? prize.Components : prize.Lucci > 0 ? [] : new[] { prize };
            var grants = new List<ShopPurchaseGrant>();
            foreach (var component in components)
            {
                var matches = rows.Where(row => row.ItemId == component.ItemId).Take(2).ToArray();
                if (matches.Length > 1)
                    throw new InvalidOperationException($"Reward item {component.ItemId} has duplicate inventory rows.");
                grants.Add(PlanLotteryGrant(component, matches.SingleOrDefault(), DateTimeOffset.UtcNow));
            }
            var command = new ShopPurchaseCommand(wallet, prize.DisplayId,
                prize.DisplayId >= 2000000 ? prize.DisplayId : 0, "lucci", 0,
                checked((long)member.UserNum * 1_000_000), grants) { LucciReward = prize.Lucci };
            try
            {
                return await _lotteryStore!.PurchaseAsync(command, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A zero-price reward grant was rejected.");
            }
            catch (ShopPurchaseConflictException) when (attempt + 1 < _options.LotteryGrantRetryCount) { }
        }
        throw new InvalidOperationException("Reward grant exhausted its bounded inventory retries.");
    }

    internal static ShopPurchaseGrant PlanLotteryGrant(LotteryItem prize, InventorySlotRecord? existing, DateTimeOffset now)
    {
        var permanent = existing?.Lifetime.Kind == InventoryLifetimeKind.Permanent;
        var start = existing?.Lifetime.ExpiresAtUnixMilliseconds is { } end
            ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(end, now.ToUnixTimeMilliseconds())) : now;
        var lifetime = prize.Days == 0 || permanent ? InventoryLifetime.Permanent : InventoryLifetime.Until(start.AddDays(prize.Days));
        var quantity = prize.Stackable
            ? (int)Math.Min(int.MaxValue, (long)(existing?.Quantity ?? 0) + prize.Quantity)
            : Math.Max(1, existing?.Quantity ?? 1);
        return new(prize.ItemId, quantity) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = existing, Lifetime = lifetime };
    }

    private async Task FinishLotteryAsync(PlayRoom room, LotteryState state)
    {
        try
        {
            await Task.Delay(_options.LotterySelectTimeMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async ct =>
            {
                if (room.Phase != RoomPhase.Result || room.StartGeneration != state.Generation) return;
                foreach (var member in state.Members)
                {
                    if (state.Awards.ContainsKey(member.Slot)) continue;
                    var card = Enumerable.Range(0, 10).First(index => !state.Claimed.Contains(index));
                    await SelectLotteryCardAsync(room, state, member, card, ct).ConfigureAwait(false);
                }
                state.Revealed = true;
                await SendToMembersAsync(room, state.Generation, (seat, token) =>
                    _lotteryRevealed!.SendAsync(seat.Game, new(state.Cards.Select((item, card) =>
                        state.Claimed.Contains(card) ? 0 : item.DisplayId).ToArray()), token), ct).ConfigureAwait(false);
            }, _lifetime).ConfigureAwait(false);
            var remaining = LotteryRemainingDisplayMs(state.OpenedAt, Environment.TickCount64,
                _options.LotterySelectTimeMs, _options.LotteryRevealTimeMs);
            await Task.Delay(remaining, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception failure) { _logger.LogError(failure, "Room {Room}: reward-card sequence failed.", room.Id); }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    await room.Executor.ExecuteAsync(async ct =>
                    {
                        _lotteries.TryRemove(room.Id, out _);
                        if (room.Phase != RoomPhase.Result || room.StartGeneration != state.Generation) return;
                        await SendToMembersAsync(room, state.Generation, (seat, token) =>
                            _lotteryClosed!.SendAsync(seat.Game, new(), token), ct).ConfigureAwait(false);
                        room.ResetToWaiting();
                        await _returnToRoom(room, ct).ConfigureAwait(false);
                    }, _lifetime).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { _lotteries.TryRemove(room.Id, out _); }
            }
        }
    }

    internal static int LotteryRemainingDisplayMs(long openedAt, long now, int selectMs, int revealMs) =>
        (int)Math.Max(0, selectMs + (long)revealMs - Math.Max(0, now - openedAt));

    private sealed class LotteryState(int generation, RoomSeat[] members, LotteryItem[] cards,
        IReadOnlyDictionary<int, RoundReward> rewards, RoundEndReason reason)
    {
        internal int Generation { get; } = generation;
        internal long OpenedAt { get; } = Environment.TickCount64;
        internal RoomSeat[] Members { get; } = members;
        internal LotteryItem[] Cards { get; } = cards;
        internal IReadOnlyDictionary<int, RoundReward> Rewards { get; } = rewards;
        internal RoundEndReason Reason { get; } = reason;
        internal Dictionary<int, int> Awards { get; } = [];
        internal HashSet<int> Claimed { get; } = [];
        internal bool Revealed { get; set; }
    }
}
