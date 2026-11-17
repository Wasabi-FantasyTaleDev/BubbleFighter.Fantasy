using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.CashInbox;

/// <summary>
/// Manages cash item inbox storage, bounded snapshot publication, atomic pickups into the character bag, and
/// live/login snapshot synchronization for one build's game protocol.
/// </summary>
internal sealed class CashInboxService(
    ShopOptions options,
    ShopCatalog catalog,
    Func<ShopStockRow, PurchaseGrantTerm> resolve,
    IInventoryRepository inventory,
    ICashInboxStore store,
    IMessageSender<ShopStateUpdated>? state,
    IInventoryPushService? push,
    IMessageSender<ShopBalanceUpdated> balance,
    IMessageSender<CashInventoryPublished> listSender,
    IMessageSender<CashInboxPickupReplied> pickupReplies,
    ISessionRegistry? sessions,
    TimeProvider clock,
    ILogger logger) : ICashInboxService
{
    private const int EnvelopeHeaderBytes = 4;
    private const int FixedSlotBytes = 25;
    private const int WideStringLengthPrefixBytes = 4;

    private string Profile => options.ClientProfile;

    /// <inheritdoc />
    public string Protocol => options.Protocol;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CashInboxEntryRecord>> GetInboxSnapshotAsync(
        long userNum,
        CancellationToken cancellationToken = default)
    {
        if (userNum <= 0)
            return [];

        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var active = await store.ListActiveAsync(
            Profile,
            userNum,
            options.MaxCashInboxEntriesPerPlayer,
            nowMs,
            cancellationToken).ConfigureAwait(false);
        return ApplyFrameBudget(active, options.MaxCashInboxFrameBytes, logger, userNum);
    }

    /// <inheritdoc />
    public async ValueTask<CashInboxGrantResult> EnqueueStockAsync(
        long userNum,
        int stockKey,
        string note = "",
        string? claimKey = null,
        bool ignoreInboxLimit = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stockKey);
        note ??= string.Empty;
        if (note.Length > CashInboxLimits.MaxNoteChars)
            throw new ArgumentOutOfRangeException(nameof(note), note.Length, "Cash inbox note exceeds maximum length.");
        if (claimKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);
            if (claimKey.Length > CashInboxLimits.MaxClaimKeyChars)
                throw new ArgumentOutOfRangeException(nameof(claimKey), claimKey.Length, "Claim key exceeds maximum length.");
        }

        var (stocks, _) = ResolveCatalogReward(stockKey)
            ?? throw new InvalidOperationException($"Cash inbox stock key {stockKey} was not found in '{Profile}' catalog.");
        if (stocks.Count == 0)
            throw new InvalidOperationException($"Cash inbox stock key {stockKey} has no inventory items to grant.");

        var terms = stocks.Select(resolve).ToArray();
        var itemKey = stocks[0].ItemId;
        var (quantity, days) = ResolveDisplayQuantities(terms[0]);
        var createdAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAtMs = options.CashInboxTtlSeconds > 0
            ? checked(createdAtMs + options.CashInboxTtlSeconds * 1000L)
            : 0L;

        var result = await store.GrantAsync(
            new CashInboxGrantCommand(
                Profile,
                userNum,
                stockKey,
                itemKey,
                quantity,
                days,
                note,
                createdAtMs,
                expiresAtMs,
                options.MaxCashInboxEntriesPerPlayer)
            {
                ClaimKey = claimKey,
                IgnoreInboxLimit = ignoreInboxLimit,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == CashInboxGrantOutcome.Granted)
        {
            logger.LogInformation(
                "Cash inbox grant {SlotId} (stock={StockKey}, item={ItemKey}) stored for player {Player} on '{Profile}'.",
                result.Entry!.CashSlotId, stockKey, itemKey, userNum, Profile);
            await PushSnapshotToOnlineSessionsAsync(userNum, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Supplies active cash inbox rows when the login burst requests the build's snapshot.</summary>
    internal async ValueTask<(bool Handled, IReadOnlyList<CashInboxEntryRecord>? Entries)> OnSnapshotRequestedAsync(
        CashInboxSnapshotRequested request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Protocol, options.Protocol, StringComparison.Ordinal))
            return (false, null);

        var snapshot = await GetInboxSnapshotAsync(request.UserNum, cancellationToken).ConfigureAwait(false);
        return (true, snapshot);
    }

    /// <summary>Enqueues configured idempotent starter welcome stocks when the client menu finishes loading.</summary>
    internal async ValueTask OnLoginWelcomeAsync(LoginMenuInstalled login, CancellationToken cancellationToken)
    {
        if (!string.Equals(login.Protocol, options.Protocol, StringComparison.Ordinal)
            || options.CashInboxWelcomeStocks.Length == 0)
            return;

        var grantedAny = false;
        foreach (var stockKey in options.CashInboxWelcomeStocks)
        {
            var claimKey = $"welcome:{Profile}:{stockKey}";
            var result = await EnqueueStockAsync(
                login.Login.UserNumber,
                stockKey,
                options.CashInboxWelcomeNote,
                claimKey,
                ignoreInboxLimit: true,
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == CashInboxGrantOutcome.Granted)
                grantedAny = true;
        }

        if (grantedAny)
        {
            // EnqueueStockAsync pushes to tracked sessions if online, but a newly connecting session may not yet be in
            // ISessionRegistry during login burst; send directly on the opening session if it is still open.
            var snapshot = await GetInboxSnapshotAsync(login.Login.UserNumber, cancellationToken).ConfigureAwait(false);
            try
            {
                await listSender.SendAsync(login.Session, new(snapshot), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to publish welcome cash inbox snapshot to player {Player} on session {Session}.",
                    login.Login.UserNumber, login.Session.Id);
            }
        }
    }

    /// <summary>Handles legacy explicit cash-inbox open requests (<c>gcpReqCashInven</c>).</summary>
    internal async ValueTask OpenAsync(ISession session, CashInboxOpenRequested request, CancellationToken cancellationToken)
    {
        _ = request;
        if (!session.TryFeature<IPlayerSession>(out var player))
            return;

        var snapshot = await GetInboxSnapshotAsync(player.Identity.UserNum, cancellationToken).ConfigureAwait(false);
        await listSender.SendAsync(session, new(snapshot), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Handles the empty close notification (<c>gcpReqCloseGiftBox</c>); no client reply packet exists.</summary>
    internal ValueTask CloseAsync(ISession session, CashInboxCloseRequested request, CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        if (session.TryFeature<IPlayerSession>(out var player))
        {
            logger.LogDebug("Player {Player} closed cash item inbox on session {Session}.",
                player.Identity.UserNum, session.Id);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Validates account ownership and item terms, commits an atomic pickup into the character bag, publishes the
    /// committed regular bag/wallet first, and then emits <c>gspRepPickup</c> (<c>kr-812 sub_824310</c>).
    /// </summary>
    internal async ValueTask PickupAsync(ISession session, CashInboxPickupRequested request, CancellationToken cancellationToken)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
        {
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId is >= 0 and <= CashInboxLimits.MaxCashSlotId ? request.CashSlotId : 0,
                    CashInboxPickupStatus.AccountMismatch),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var me = player.Identity;
        if (request.CashSlotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
        {
            await pickupReplies.SendAsync(
                session,
                new(0, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var entry = await store.FindBySlotIdAsync(Profile, request.CashSlotId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (entry.UserNum != me.UserNum)
        {
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.AccountMismatch),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!entry.IsActiveAt(nowMs))
        {
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var resolved = ResolveCatalogReward(entry.StockKey);
        if (resolved is null || resolved.Value.Stocks.Count == 0)
        {
            logger.LogError(
                "Cash inbox slot {SlotId} references stock key {Key}, which is missing from '{Profile}' catalog.",
                request.CashSlotId, entry.StockKey, Profile);
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PurchaseGrantTerm[] terms;
        try
        {
            terms = resolved.Value.Stocks.Select(resolve).ToArray();
        }
        catch (Exception ex) when (ex is PurchaseRejectedException or InvalidOperationException)
        {
            logger.LogError(ex,
                "Cash inbox slot {SlotId} (stock {Key}) has invalid purchase terms on '{Profile}'.",
                request.CashSlotId, entry.StockKey, Profile);
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        CashInboxPickupResult? committed = null;
        for (var attempt = 0; attempt < options.MaxPurchaseAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bag = await inventory.ListSlotsAsync(
                me.UserNum,
                InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ShopPurchaseGrant> grants;
            try
            {
                grants = ItemPurchasePlanner.Plan(me.UserNum, terms, bag, clock.GetUtcNow());
            }
            catch (PurchaseRejectedException ex)
            {
                logger.LogInformation(
                    "Cash inbox slot {SlotId} ({Key}) refused for player {Player}: {Reason}",
                    request.CashSlotId, entry.StockKey, me.UserNum, ex.Message);
                var status = ex.Refusal is ShopPurchaseRefusal.AlreadyOwned or ShopPurchaseRefusal.LimitReached
                    or ShopPurchaseRefusal.NoMoreAllowed
                    ? CashInboxPickupStatus.AlreadyOwned
                    : CashInboxPickupStatus.Failed;
                await pickupReplies.SendAsync(
                    session,
                    new(request.CashSlotId, status),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var appendCount = grants.Count(grant => grant.ExpectedSlot is null);
            if (bag.Count + appendCount > InventoryLimits.MaxInventorySlotsPerBag)
            {
                await pickupReplies.SendAsync(
                    session,
                    new(request.CashSlotId, CashInboxPickupStatus.Failed),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var commitNowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var command = new CashInboxPickupCommand(
                Profile,
                me.UserNum,
                request.CashSlotId,
                ShopPurchaser.SeedWallet(options, me.UserNum),
                ShopPurchaser.PurchaseSlotKey(me.UserNum, 0),
                grants,
                commitNowMs)
            {
                LucciReward = resolved.Value.Lucci,
            };

            CashInboxPickupResult attemptResult;
            try
            {
                attemptResult = await store.PickUpAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (ShopPurchaseConflictException) when (attempt < options.MaxPurchaseAttempts - 1)
            {
                continue;
            }

            if (attemptResult.Outcome == CashInboxPickupOutcome.PickedUp)
            {
                committed = attemptResult;
                break;
            }

            // A concurrent non-conflicting bag edit may leave the slot snapshot stale until replanned; retry while attempts remain.
            if (attemptResult.Outcome == CashInboxPickupOutcome.AlreadyOwned && attempt < options.MaxPurchaseAttempts - 1)
                continue;

            var failureStatus = attemptResult.Outcome switch
            {
                CashInboxPickupOutcome.OwnerMismatch => CashInboxPickupStatus.AccountMismatch,
                CashInboxPickupOutcome.AlreadyOwned => CashInboxPickupStatus.AlreadyOwned,
                _ => CashInboxPickupStatus.Failed,
            };
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, failureStatus),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (committed?.PurchaseResult is null)
        {
            await pickupReplies.SendAsync(
                session,
                new(request.CashSlotId, CashInboxPickupStatus.Failed),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // The pickup is durably committed; publish the regular bag and wallet before gspRepPickup(1) because
        // kr-812 sub_824310 and kr-1256 sub_9B2900 allow immediate inventory opening from the result dialog.
        var purchase = committed.PurchaseResult;
        try
        {
            if (state is not null)
            {
                await state.SendAsync(session, new(me, purchase.Wallet, purchase.Grants, false), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await push!.PushPurchaseAsync(session, me.UserNum, purchase.Grants, cancellationToken)
                    .ConfigureAwait(false);
                await balance.SendAsync(session, new(me, purchase.Wallet), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Cash inbox pickup {SlotId} ({Key}) committed for player {Player}, but post-commit bag/wallet publication failed.",
                request.CashSlotId, entry.StockKey, me.UserNum);
        }

        logger.LogInformation(
            "Cash inbox slot {SlotId} ({Key}) picked up by player {Player}: {Count} grant(s).",
            request.CashSlotId, entry.StockKey, me.UserNum, purchase.Grants.Count);
        await pickupReplies.SendAsync(
            session,
            new(request.CashSlotId, CashInboxPickupStatus.Success),
            cancellationToken).ConfigureAwait(false);
    }

    internal (IReadOnlyList<ShopStockRow> Stocks, int Lucci)? ResolveCatalogReward(int stockKey)
    {
        if (catalog.Gifts.TryGetValue(stockKey, out var gift))
            return (gift.Stocks, gift.Lucci);
        if (catalog.FindPurchase(stockKey) is { } offer)
            return (offer.Stocks, 0);
        return null;
    }

    internal static IReadOnlyList<CashInboxEntryRecord> ApplyFrameBudget(
        IReadOnlyList<CashInboxEntryRecord> entries,
        int maxFrameBytes,
        ILogger logger,
        long userNum)
    {
        if (entries.Count == 0)
            return [];

        var totalBytes = EnvelopeHeaderBytes;
        var accepted = new List<CashInboxEntryRecord>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var slotBytes = FixedSlotBytes + WideStringLengthPrefixBytes + Encoding.Unicode.GetByteCount(entry.Note);
            if (totalBytes + slotBytes > maxFrameBytes)
            {
                logger.LogWarning(
                    "Truncating cash inbox snapshot for player {Player} at {Kept}/{Total} entries to fit frame budget ({Budget} bytes).",
                    userNum, accepted.Count, entries.Count, maxFrameBytes);
                break;
            }

            totalBytes += slotBytes;
            accepted.Add(entry);
        }

        return accepted;
    }

    private async ValueTask PushSnapshotToOnlineSessionsAsync(long userNum, CancellationToken cancellationToken)
    {
        if (sessions is null)
            return;

        IReadOnlyList<CashInboxEntryRecord>? snapshot = null;
        foreach (var live in sessions.All)
        {
            if (live.Connection.IsClosed
                || !string.Equals(live.Profile.Id, Profile, StringComparison.Ordinal)
                || !live.TryFeature<IPlayerSession>(out var player)
                || player.Identity.UserNum != userNum)
                continue;

            snapshot ??= await GetInboxSnapshotAsync(userNum, cancellationToken).ConfigureAwait(false);
            try
            {
                await listSender.SendAsync(live, new(snapshot), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to push updated cash inbox snapshot to player {Player} on session {Session}.",
                    userNum, live.Id);
            }
        }
    }

    private static (int Quantity, int Days) ResolveDisplayQuantities(PurchaseGrantTerm term) => term.Kind switch
    {
        PurchaseGrantKind.Count => (Math.Clamp(term.Amount, 0, ushort.MaxValue), 0),
        PurchaseGrantKind.Life => (Math.Clamp(term.Amount, 0, ushort.MaxValue), 0),
        PurchaseGrantKind.Days => (1, Math.Clamp(term.Amount, 0, ushort.MaxValue)),
        PurchaseGrantKind.Permanent => (1, 0),
        _ => throw new InvalidOperationException($"Unsupported cash inbox grant kind '{term.Kind}'."),
    };
}
