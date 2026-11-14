using System.Globalization;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>
/// One build's ban rules: the room lists their hosts set, the server policies, the start check, the announcements and
/// the overlay messages. Room callbacks run on each room's executor; commands and overlay messages arrive on other
/// threads, so all per-room state is taken under one lock and every send happens outside it.
/// </summary>
/// <remarks>
/// Text reaches a player as a server notice where the build's message row sends one (<c>server_notices</c>), otherwise
/// as a room chat line from a system speaker.
/// </remarks>
internal sealed class BanPickRoomRules(IBanPickHub hub, IRoomPolicyHost rooms, BanPickItemCatalog catalog,
    IMessageSender<ServerNotice>? notices, IMessageSender<RoomChatReceived>? chat, IGameText texts, IOnlinePlayers? online,
    TimeProvider clock, ILogger logger)
    : IRoomPolicy, IBanPickScope
{
    // The chat line's colour channel: kr-812 sub_8023E0 and kr-1256 sub_9DE360 colour type 1 apart from players' lines.
    private const int SystemChannel = 1;

    // A native notice holds at most 511 characters (cn-324 sub_78A570 formats it into a 512-character buffer).
    private const int NoticeCharacters = 511;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RoomState> _rooms = [];

    private sealed class RoomState
    {
        internal HashSet<int> Items { get; } = [];
        internal HashSet<string> Groups { get; } = new(StringComparer.Ordinal);
        internal long ListVersion { get; set; }
        internal string? Confirmed { get; set; }
        internal PendingStart? Pending { get; set; }
        internal Dictionary<long, HashSet<int>> Announced { get; } = [];

        internal BanPickRoomList Snapshot() => new(Items.ToHashSet(), Groups.ToHashSet(StringComparer.Ordinal));
    }

    // The last start held for items the host may allow: who pressed it, exactly which items, and until when a second
    // press, the overlay's yes or "bp start" counts as the host's answer.
    private sealed record PendingStart(long Host, string Signature, long ExpiresAt);

    public string ClientBuild => rooms.ClientBuild;

    internal BanPickItemCatalog Catalog => catalog;

    public IReadOnlyList<BanPickCatalogEntry> Search(string text, int limit) =>
        [.. catalog.Search(text, limit).Select(item => new BanPickCatalogEntry(item.Id, item.Name))];

    private BanPickPolicy Policy => hub.Store.Current.Policy;

    private BanPickSettings Options => hub.Settings;

    public ISession? FindSeated(long sessionId) => rooms.Session(sessionId);

    // ---- start ---------------------------------------------------------------------------------------------------

    public RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId)
    {
        // The native automatic start has no host to confirm or press again; ban lists never hold it.
        if (room.AutomaticStart) return RoomStartDecision.Proceed;
        var hits = BanPickEvaluator.Evaluate(Policy, catalog, ClientBuild, room, ListOf(room.Instance));
        if (BanPickEvaluator.Where(hits, type => type.StopsStart && !type.HostMayConfirm).Count > 0)
            return RoomStartDecision.Hold;
        var confirmable = BanPickEvaluator.Where(hits, type => type.HostMayConfirm);
        if (confirmable.Count == 0) return RoomStartDecision.Proceed;
        var signature = BanPickEvaluator.Signature(confirmable);
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            var state = State(room.Instance);
            // The host already said yes to exactly these items.
            if (state.Confirmed == signature) return RoomStartDecision.Proceed;
            // Pressing start again in time is the yes. Another policy's replay is not the host pressing again.
            if (!room.ResumedStart
                && state.Pending is { } pending && pending.Signature == signature && pending.Host == requesterSessionId && now < pending.ExpiresAt)
            {
                state.Confirmed = signature;
                state.Pending = null;
                return RoomStartDecision.Proceed;
            }
        }
        return RoomStartDecision.Hold;
    }

    // The yes is used up only by a start that really goes ahead; another policy's hold keeps it for the replay.
    public void OnStartDecided(RoomPolicyRoom room, long requesterSessionId, bool proceeding)
    {
        if (!proceeding) return;
        lock (_gate)
        {
            if (_rooms.TryGetValue(room.Instance, out var state))
            {
                state.Confirmed = null;
                state.Pending = null;
            }
        }
        // The round is starting: the overlay closes its editor and button until the room waits again.
        foreach (var seat in room.Seats)
            if (rooms.Session(seat.SessionId) is { } member)
                SendListChanged(member, room, playing: true);
    }

    public ValueTask OnWaitingAgainAsync(RoomPolicyRoom room, CancellationToken ct)
    {
        BroadcastListChanged(room);
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct)
    {
        var hits = BanPickEvaluator.Evaluate(Policy, catalog, ClientBuild, room, ListOf(room.Instance));
        var blocked = BanPickEvaluator.Where(hits, type => type.StopsStart && !type.HostMayConfirm);
        if (blocked.Count > 0)
        {
            await RoomLinesAsync(room, "banpick.start.blocked", blocked, ct).ConfigureAwait(false);
            return;
        }
        var confirmable = BanPickEvaluator.Where(hits, type => type.HostMayConfirm);
        if (confirmable.Count == 0) return;
        var signature = BanPickEvaluator.Signature(confirmable);
        // A replay after another policy's hold must not announce or ask again while the host's question is still open.
        if (room.ResumedStart)
            lock (_gate)
                if (State(room.Instance).Pending is { } open && open.Host == requesterSessionId && open.Signature == signature
                    && clock.GetTimestamp() < open.ExpiresAt)
                    return;
        await RoomLinesAsync(room, "banpick.start.violators", confirmable, ct).ConfigureAwait(false);
        if (rooms.Session(requesterSessionId) is not { } host) return;
        // The overlay's yes runs "bp start" through the launch-bound command channel, like a typed command.
        if (hub.Companion is { } companion && companion.Supports(host, BanPickCompanion.StartConfirm, BanPickCompanion.Version))
        {
            lock (_gate)
                State(room.Instance).Pending = new(requesterSessionId, signature, clock.GetTimestamp() + Ticks(Options.ConfirmTimeoutMs));
            var sent = companion.Send(host, BanPickCompanion.StartConfirm, BanPickCompanion.Version, new
            {
                RoomInstance = room.Instance,
                Command = "bp start",
                Title = texts.Format(host, "banpick.start.confirm_title"),
                Text = texts.Format(host, "banpick.start.confirm_text"),
                ConfirmLabel = texts.Format(host, "banpick.start.confirm_yes"),
                CancelLabel = texts.Format(host, "banpick.start.confirm_no"),
                Violators = confirmable.Select(seat => new { seat.Seat.Nickname, Items = seat.Hits.Select(hit => catalog.NameOf(hit.ItemId)).ToArray() }).ToArray(),
                ExpiresInMs = Options.ConfirmTimeoutMs,
            });
            if (sent) return;
            // The overlay's queue is gone or full: the host gets the chat way instead.
        }
        lock (_gate)
            State(room.Instance).Pending = new(requesterSessionId, signature, clock.GetTimestamp() + Ticks(Options.ReclickWindowMs));
        await NoticeAsync(host, "banpick.start.reclick", ct,
            ("seconds", (Options.ReclickWindowMs / 1000).ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    // ---- room life -----------------------------------------------------------------------------------------------

    public async ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct)
    {
        if (rooms.Session(sessionId) is not { } member) return;
        var list = ListOf(room.Instance);
        // Every member learns the room's state, an empty list included: the host's overlay offers the editor from it.
        SendListChanged(member, room);
        if (list.Count > 0)
            await SendListAsync(member, list, "banpick.list.current", "banpick.list.current_many", ct).ConfigureAwait(false);
        var hits = BanPickEvaluator.Evaluate(Policy, catalog, ClientBuild, room, list);
        // Everyone already wearing a restricted item is named to the newcomer once.
        var warned = BanPickEvaluator.Where(hits, type => !type.StopsStart);
        var others = warned.Where(seat => seat.Seat.SessionId != sessionId).ToArray();
        if (others.Length > 0)
            await LinesAsync(member, "banpick.warn.summary", others, ct).ConfigureAwait(false);
        await AnnounceChangesAsync(room, sessionId, hits, ct).ConfigureAwait(false);
    }

    public async ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct)
    {
        if (!room.Waiting) return;
        var hits = BanPickEvaluator.Evaluate(Policy, catalog, ClientBuild, room, ListOf(room.Instance));
        await AnnounceChangesAsync(room, sessionId, hits, ct).ConfigureAwait(false);
    }

    public ValueTask OnMemberLeftAsync(RoomPolicyRoom? room, ISession session, CancellationToken ct)
    {
        // The departed player's overlay closes the room's editor; the members who stay may have a new host.
        if (rooms.Find(session.Id) is null) SendListChanged(session, null);
        if (room is not null) BroadcastListChanged(room);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnHostChangedAsync(RoomPolicyRoom room, CancellationToken ct)
    {
        BroadcastListChanged(room);
        return ValueTask.CompletedTask;
    }

    public void OnRoomClosed(Guid instance)
    {
        lock (_gate) _rooms.Remove(instance);
    }

    // Tells the room about restricted items the seat newly wears, and the seat about its newly worn banned items.
    private async ValueTask AnnounceChangesAsync(RoomPolicyRoom room, long sessionId, IReadOnlyList<BanPickSeatHits> hits, CancellationToken ct)
    {
        var seat = hits.FirstOrDefault(entry => entry.Seat.SessionId == sessionId);
        BanPickHit[] fresh;
        lock (_gate)
        {
            var state = State(room.Instance);
            var worn = seat?.Hits.Select(hit => hit.ItemId).ToHashSet() ?? [];
            var before = state.Announced.GetValueOrDefault(sessionId) ?? [];
            fresh = seat?.Hits.Where(hit => !before.Contains(hit.ItemId)).ToArray() ?? [];
            if (worn.Count == 0) state.Announced.Remove(sessionId);
            else state.Announced[sessionId] = worn;
        }
        if (seat is null || fresh.Length == 0) return;
        var warn = fresh.Where(hit => !hit.Type.StopsStart).ToArray();
        var banned = fresh.Where(hit => hit.Type.StopsStart).ToArray();
        if (warn.Length > 0)
            await RoomNoticeAsync(room, "banpick.warn.wearing", ct, ("nickname", seat.Seat.Nickname), ("items", Names(warn))).ConfigureAwait(false);
        if (banned.Length > 0 && rooms.Session(sessionId) is { } member)
        {
            await NoticeAsync(member, "banpick.reminder", ct, ("items", Names(banned))).ConfigureAwait(false);
            SendWarn(member, "room", banned, null, texts.Format(member, "banpick.reminder", Arguments(("items", Names(banned)))));
        }
        if (warn.Length > 0 && hub.Companion is { } companion)
            foreach (var other in room.Seats)
                if (rooms.Session(other.SessionId) is { } peer && companion.Supports(peer, BanPickCompanion.Warn, BanPickCompanion.Version))
                    SendWarn(peer, "warn", warn, seat.Seat.Nickname, texts.Format(peer, "banpick.warn.wearing",
                        Arguments(("nickname", seat.Seat.Nickname), ("items", Names(warn)))));
    }

    // ---- equipment -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Refuses wearing an item a block rule names where the player is: anywhere for an unconditional policy, otherwise
    /// in a room the policy's scope selects. The refusal rides the equipment row's ordinary failure reply.
    /// </summary>
    internal async ValueTask<EquipmentSelectionDecision> GuardEquipmentAsync(EquipmentSelectionApplying request,
        Func<CancellationToken, ValueTask<EquipmentSelectionDecision>> next, CancellationToken ct)
    {
        if (request.Protocol != rooms.Protocol || !request.Worn || request.UserNum is <= 0 or > int.MaxValue)
            return await next(ct).ConfigureAwait(false);
        var session = online?.FindByUserNum((int)request.UserNum);
        var room = session is null ? null : rooms.Find(session.Id);
        var place = new BanPickPlace(ClientBuild, room?.ModeId, room?.HasPassword ?? false);
        var hit = BanPickEvaluator.Check(Policy, catalog, place, BanPickRoomList.Empty, request.ItemId);
        if (hit is null || !hit.Type.RefusesEquip)
            return await next(ct).ConfigureAwait(false);
        logger.LogInformation("Player {User} wear of item {Item} on {Protocol} refused by ban-pick policy {Policy}.",
            request.UserNum, request.ItemId, request.Protocol, hit.Policy);
        if (session is not null)
        {
            await NoticeAsync(session, "banpick.equip.blocked", ct, ("item", catalog.NameOf(request.ItemId))).ConfigureAwait(false);
            SendWarn(session, "block", [hit], null, texts.Format(session, "banpick.equip.blocked", Arguments(("item", catalog.NameOf(request.ItemId)))));
        }
        return new EquipmentSelectionDecision(false, request.ItemId, request.Worn, $"ban-pick policy {hit.Policy}");
    }

    // ---- commands ------------------------------------------------------------------------------------------------

    public async ValueTask<CommandResult> RoomCommandAsync(ISession session, string language, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var verb = arguments.Count > 0 ? arguments[0].ToLowerInvariant() : "list";
        var text = arguments.Count > 1 ? string.Join(' ', arguments.Skip(1)) : "";
        if (rooms.Find(session.Id) is not { } room)
            return new(CommandStatus.InvalidState, texts.Format(session, "banpick.command.room_only"));
        switch (verb)
        {
            case "list" or "ls":
                return List(session, room, text);
            case "add" or "remove" or "rm" or "del" or "clear" or "start" or "set":
                break;
            default:
                return Usage(session);
        }
        if (room.Seats.FirstOrDefault(seat => seat.SessionId == session.Id) is not { Host: true })
            return new(CommandStatus.Success, texts.Format(session, "banpick.command.host_only"));
        if (!room.Waiting)
            return new(CommandStatus.Success, texts.Format(session, "banpick.command.waiting_only"));
        if (verb == "start")
            return await ConfirmStartAsync(session, room, ct).ConfigureAwait(false);
        if (verb == "clear")
        {
            lock (_gate)
            {
                var state = State(room.Instance);
                state.Items.Clear();
                state.Groups.Clear();
                state.ListVersion++;
                state.Confirmed = null;
                state.Pending = null;
            }
            await RoomNoticeAsync(room, "banpick.list.cleared", ct).ConfigureAwait(false);
            BroadcastListChanged(room);
            return new(CommandStatus.Success, "");
        }
        if (verb == "set")
            return await SetAsync(session, room, arguments.Skip(1).ToArray(), ct).ConfigureAwait(false);
        if (text.Length == 0) return Usage(session);
        var target = Resolve(text);
        if (target.Error is { } error)
            return new(CommandStatus.Success, Fit(texts.Format(session, error, target.Matches.Length == 0
                ? Arguments(("text", text)) : Arguments(("text", text), ("items", target.Matches)))));
        var outcome = Edit(room.Instance, verb == "add" ? [target.Entry!] : [], verb == "add" ? [] : [target.Entry!], null);
        if (outcome is not null)
            return new(CommandStatus.Success, texts.Format(session, outcome, Arguments(("text", text))));
        await AnnounceListAsync(room, ct).ConfigureAwait(false);
        return new(CommandStatus.Success, "");
    }

    // "bp set <id or group>...": the whole room list at once, as the overlay's editor sends it, announced once. Each word
    // is one item id or group name; nothing changes unless every word resolves and the list fits.
    private async ValueTask<CommandResult> SetAsync(ISession session, RoomPolicyRoom room, IReadOnlyList<string> words, CancellationToken ct)
    {
        if (words.Count == 0) return Usage(session);
        var entries = new List<Entry>();
        foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Resolve(word);
            if (target.Error is { } error)
                return new(CommandStatus.Success, Fit(texts.Format(session, error, target.Matches.Length == 0
                    ? Arguments(("text", word)) : Arguments(("text", word), ("items", target.Matches)))));
            if (!entries.Contains(target.Entry!)) entries.Add(target.Entry!);
        }
        if (entries.Count > Options.MaxRoomEntries) return new(CommandStatus.Success, texts.Format(session, "banpick.command.full"));
        lock (_gate)
        {
            var state = State(room.Instance);
            var items = entries.Where(entry => entry.Item is not null).Select(entry => entry.Item!.Value).ToHashSet();
            var groups = entries.Where(entry => entry.Group is not null).Select(entry => entry.Group!).ToHashSet(StringComparer.Ordinal);
            if (state.Items.SetEquals(items) && state.Groups.SetEquals(groups)) return new(CommandStatus.Success, "");
            state.Items.Clear(); state.Items.UnionWith(items);
            state.Groups.Clear(); state.Groups.UnionWith(groups);
            state.ListVersion++;
            state.Confirmed = null;
            state.Pending = null;
        }
        await AnnounceListAsync(room, ct).ConfigureAwait(false);
        return new(CommandStatus.Success, "");
    }

    // "bp start": the host allows the start the rules just held, for exactly the items they named then.
    private async ValueTask<CommandResult> ConfirmStartAsync(ISession session, RoomPolicyRoom room, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = State(room.Instance);
            if (state.Pending is not { } pending || pending.Host != session.Id || clock.GetTimestamp() >= pending.ExpiresAt)
                return new(CommandStatus.Success, texts.Format(session, "banpick.command.nothing_held"));
            state.Pending = null;
            state.Confirmed = pending.Signature;
        }
        if (await rooms.ResumeHeldStartAsync(room.Instance, session.Id, ct).ConfigureAwait(false))
            return new(CommandStatus.Success, "");
        lock (_gate) State(room.Instance).Confirmed = null;
        return new(CommandStatus.Success, texts.Format(session, "banpick.command.nothing_held"));
    }

    private CommandResult List(ISession session, RoomPolicyRoom room, string pageText)
    {
        var entries = DisplayNames(ListOf(room.Instance));
        if (entries.Count == 0) return new(CommandStatus.Success, texts.Format(session, "banpick.list.empty"));
        var size = Options.AnnouncementItems;
        var pages = (entries.Count + size - 1) / size;
        var page = int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 1, pages) : 1;
        return new(CommandStatus.Success, Fit(texts.Format(session, "banpick.list.page", Arguments(
            ("page", page.ToString(CultureInfo.InvariantCulture)), ("pages", pages.ToString(CultureInfo.InvariantCulture)),
            ("items", string.Join(", ", entries.Skip((page - 1) * size).Take(size)))))));
    }

    private CommandResult Usage(ISession session) => new(CommandStatus.InvalidArguments, texts.Format(session, "banpick.command.usage"));

    // A room entry: an item id, or a group name.
    private sealed record Entry(int? Item, string? Group);

    private sealed record Resolution(Entry? Entry, string? Error, string Matches = "");

    // A declared group name, an item id this build has, or the one item whose name contains the text.
    private Resolution Resolve(string text)
    {
        var name = text.Trim();
        if (Policy.Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)) is { } group)
            return catalog.HasAny(group) ? new(new(null, group.Name), null) : new(null, "banpick.command.group_absent");
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return catalog.Find(id) is not null ? new(new(id, null), null) : new(null, "banpick.command.not_found");
        var matches = catalog.Search(name, Options.SearchResults + 1);
        var exact = matches.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return new(new(exact[0].Id, null), null);
        return matches.Count switch
        {
            0 => new(null, "banpick.command.not_found"),
            1 => new(new(matches[0].Id, null), null),
            _ => new(null, "banpick.command.ambiguous", string.Join(", ", matches.Take(Options.SearchResults)
                .Select(item => $"{item.Name} ({item.Id.ToString(CultureInfo.InvariantCulture)})"))),
        };
    }

    // Applies an edit; returns a message key when nothing changed, null when the list changed.
    private string? Edit(Guid instance, IReadOnlyList<Entry> add, IReadOnlyList<Entry> remove, long? expectedVersion)
    {
        lock (_gate)
        {
            var state = State(instance);
            if (expectedVersion is { } version && version != state.ListVersion) return "banpick.command.stale";
            var size = state.Items.Count + state.Groups.Count
                - remove.Count(entry => entry.Item is { } item ? state.Items.Contains(item) : state.Groups.Contains(entry.Group!));
            if (size + add.Count(entry => entry.Item is { } item ? !state.Items.Contains(item) : !state.Groups.Contains(entry.Group!))
                > Options.MaxRoomEntries) return "banpick.command.full";
            var changed = false;
            foreach (var entry in remove)
                changed |= entry.Item is { } item ? state.Items.Remove(item) : state.Groups.Remove(entry.Group!);
            foreach (var entry in add)
                changed |= entry.Item is { } item ? state.Items.Add(item) : state.Groups.Add(entry.Group!);
            if (!changed) return add.Count > 0 ? "banpick.command.already" : "banpick.command.not_listed";
            state.ListVersion++;
            state.Confirmed = null;
            state.Pending = null;
            return null;
        }
    }

    // After a change: every member is told, and anyone now wearing a newly banned item is reminded.
    private async ValueTask AnnounceListAsync(RoomPolicyRoom room, CancellationToken ct)
    {
        var list = ListOf(room.Instance);
        foreach (var seat in room.Seats)
            if (rooms.Session(seat.SessionId) is { } member)
                await SendListAsync(member, list, "banpick.list.changed", "banpick.list.changed_many", ct).ConfigureAwait(false);
        BroadcastListChanged(room);
        var hits = BanPickEvaluator.Evaluate(Policy, catalog, ClientBuild, room, list);
        foreach (var seat in room.Seats)
            await AnnounceChangesAsync(room, seat.SessionId, hits, ct).ConfigureAwait(false);
    }

    // ---- overlay -------------------------------------------------------------------------------------------------

    public async ValueTask<object?> CompanionAsync(string type, CompanionCaller caller, JsonElement payload, CancellationToken ct)
    {
        var session = caller.Session;
        var room = rooms.Find(session.Id);
        switch (type)
        {
            case BanPickCompanion.ListGet:
                return new
                {
                    RoomInstance = room?.Instance,
                    InRoom = room is not null,
                    IsHost = room?.Seats.Any(seat => seat.SessionId == session.Id && seat.Host) ?? false,
                    ListVersion = room is null ? 0 : Version(room.Instance),
                    Room = room is null ? Array.Empty<object>() : Entries(ListOf(room.Instance)),
                    Global = GlobalEntries(room),
                };
            case BanPickCompanion.CatalogSearch:
            {
                var text = String(payload, "text") ?? "";
                return new
                {
                    Items = (text.Length == 0 ? [] : catalog.Search(text, Options.SearchResults))
                        .Select(item => new { item.Id, item.Name, item.MainCategory, item.SubCategory }).ToArray(),
                    Groups = Policy.Groups.Where(group => catalog.HasAny(group)
                        && (text.Length == 0 || group.Name.Contains(text, StringComparison.OrdinalIgnoreCase))).Select(group => group.Name).ToArray(),
                };
            }
            default:
                throw new InvalidOperationException($"Ban-pick message type '{type}' is not routed here.");
        }
    }

    private void BroadcastListChanged(RoomPolicyRoom room)
    {
        // List commands can await notices while the room executor handles a transfer or departure.
        foreach (var seat in room.Seats)
            if (rooms.Session(seat.SessionId) is { } member && rooms.Find(member.Id) is { } current
                && current.Instance == room.Instance)
                SendListChanged(member, current);
    }

    // A null room tells the player they are no longer in one; playing says a round of this room is under way.
    private void SendListChanged(ISession member, RoomPolicyRoom? room, bool playing = false)
    {
        if (hub.Companion is not { } companion || !companion.Supports(member, BanPickCompanion.ListChanged, BanPickCompanion.Version)) return;
        var entries = room is null ? [] : Entries(ListOf(room.Instance));
        companion.Send(member, BanPickCompanion.ListChanged, BanPickCompanion.Version, new
        {
            RoomInstance = room?.Instance,
            InRoom = room is not null,
            IsHost = room?.Seats.Any(seat => seat.SessionId == member.Id && seat.Host) ?? false,
            Playing = room is not null && (playing || !room.Waiting),
            ListVersion = room is null ? 0 : Version(room.Instance),
            Title = texts.Format(member, "banpick.overlay.list_title"),
            ButtonLabel = texts.Format(member, "banpick.overlay.list_button"),
            Count = entries.Length,
            Preview = entries.Take(Options.AnnouncementItems).ToArray(),
        });
    }

    // The same sentence the chat announcement carries, so the overlay shows it as it is.
    private void SendWarn(ISession session, string reason, IReadOnlyList<BanPickHit> hits, string? wearer, string text)
    {
        if (hub.Companion is not { } companion || !companion.Supports(session, BanPickCompanion.Warn, BanPickCompanion.Version)) return;
        companion.Send(session, BanPickCompanion.Warn, BanPickCompanion.Version, new
        {
            Title = texts.Format(session, "banpick.overlay.warn_title"),
            Text = Fit(text),
            Reason = reason,
            Items = hits.Select(hit => catalog.NameOf(hit.ItemId)).ToArray(),
            Wearer = wearer,
        });
    }

    private object[] Entries(BanPickRoomList list) =>
    [
        .. list.Groups.Order(StringComparer.Ordinal).Select(group => new { Kind = "group", Group = group, Name = GroupName(group), Scope = "room", Rule = BanPickRuleTypes.Confirm }),
        .. list.Items.Order().Select(item => new { Kind = "item", Item = item, Name = catalog.NameOf(item), Scope = "room", Rule = BanPickRuleTypes.Confirm }),
    ];

    private object[] GlobalEntries(RoomPolicyRoom? room)
    {
        var place = new BanPickPlace(ClientBuild, room?.ModeId, room?.HasPassword ?? false);
        return [.. Policy.Policies.Where(entry => room is null ? entry.Scope.Builds.Count == 0 || entry.Scope.Builds.Contains(ClientBuild)
                : BanPickEvaluator.Applies(entry.Scope, place))
            .SelectMany(entry => entry.Rules.Select(rule => new
            {
                Policy = entry.Id,
                PolicyName = entry.Name,
                Rule = rule.Type,
                entry.Scope.Modes,
                entry.Scope.Rooms,
                Items = rule.Items.Select(item => new { Item = item, Name = catalog.NameOf(item) }).ToArray(),
                rule.Groups,
            }))];
    }

    // ---- text ----------------------------------------------------------------------------------------------------

    private IReadOnlyList<string> DisplayNames(BanPickRoomList list) =>
        [.. list.Groups.Order(StringComparer.Ordinal).Select(GroupName), .. list.Items.Order().Select(catalog.NameOf)];

    private string GroupName(string group) => texts.Format("", "banpick.group", Arguments(("name", group)));

    private string Names(IEnumerable<BanPickHit> hits) => string.Join(", ", hits.Select(hit => catalog.NameOf(hit.ItemId)));

    private async ValueTask SendListAsync(ISession member, BanPickRoomList list, string key, string manyKey, CancellationToken ct)
    {
        var names = DisplayNames(list);
        if (names.Count > Options.AnnouncementItems)
            await NoticeAsync(member, manyKey, ct, ("count", names.Count.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        else
            await NoticeAsync(member, key, ct, ("items", string.Join(", ", names))).ConfigureAwait(false);
    }

    private async ValueTask RoomLinesAsync(RoomPolicyRoom room, string header, IReadOnlyList<BanPickSeatHits> seats, CancellationToken ct)
    {
        foreach (var seat in room.Seats)
            if (rooms.Session(seat.SessionId) is { } member)
                await LinesAsync(member, header, seats, ct).ConfigureAwait(false);
    }

    // A header notice, then one notice per player: a native notice is one bounded line.
    private async ValueTask LinesAsync(ISession member, string header, IReadOnlyList<BanPickSeatHits> seats, CancellationToken ct)
    {
        await NoticeAsync(member, header, ct).ConfigureAwait(false);
        foreach (var seat in seats)
            await NoticeAsync(member, "banpick.line", ct, ("nickname", seat.Seat.Nickname), ("items", Names(seat.Hits))).ConfigureAwait(false);
    }

    private async ValueTask RoomNoticeAsync(RoomPolicyRoom room, string key, CancellationToken ct, params (string Name, string Value)[] arguments)
    {
        foreach (var seat in room.Seats)
            if (rooms.Session(seat.SessionId) is { } member)
                await NoticeAsync(member, key, ct, arguments).ConfigureAwait(false);
    }

    private async ValueTask NoticeAsync(ISession member, string key, CancellationToken ct, params (string Name, string Value)[] arguments)
    {
        if (member.Connection.IsClosed) return;
        var text = Fit(texts.Format(member, key, Arguments(arguments)));
        try
        {
            if (notices is not null)
                await notices.SendAsync(member, new ServerNotice(text), ct).ConfigureAwait(false);
            else if (chat is not null)
            {
                // The line carries the recipient's own seat so no other player is shown as its speaker.
                var room = rooms.Find(member.Id);
                var slot = room?.Seats.FirstOrDefault(seat => seat.SessionId == member.Id)?.Slot ?? 0;
                await chat.SendAsync(member, new RoomChatReceived(room?.RoomId ?? 0, slot, texts.Format(member, "banpick.system_name"),
                    SystemChannel, text), ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The member left while the notice was queued; the room's own departure handles the seat.
        }
    }

    private static string Fit(string text) => text.Length <= NoticeCharacters ? text : text[..(NoticeCharacters - 3)] + "...";

    private static Dictionary<string, string> Arguments(params (string Name, string Value)[] arguments) =>
        arguments.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal);

    private static string? String(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    // ---- state ---------------------------------------------------------------------------------------------------

    // Under _gate. A room's state is created on first use and removed when the room closes.
    private RoomState State(Guid instance)
    {
        if (!_rooms.TryGetValue(instance, out var state)) _rooms.Add(instance, state = new());
        return state;
    }

    private BanPickRoomList ListOf(Guid instance)
    {
        lock (_gate) return _rooms.TryGetValue(instance, out var state) ? state.Snapshot() : BanPickRoomList.Empty;
    }

    private long Version(Guid instance)
    {
        lock (_gate) return _rooms.TryGetValue(instance, out var state) ? state.ListVersion : 0;
    }

    private long Ticks(int milliseconds) => (long)(milliseconds * (clock.TimestampFrequency / 1000.0));
}
