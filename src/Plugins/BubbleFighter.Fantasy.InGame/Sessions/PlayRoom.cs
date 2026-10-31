using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Bounded in-memory room. All mutations take <see cref="Gate"/>.</summary>
public sealed partial class PlayRoom
{
    internal BubbleFighter.Fantasy.Contracts.Match.TutorialRoundOpened? Tutorial { get; set; }
    internal CancellationTokenSource? TutorialCancellation { get; set; }
    internal BubbleFighter.Fantasy.InGame.Handlers.RoundRescueState? Rescues { get; set; }
    internal ushort BlueScore { get; set; }
    internal bool AutoStartArmed { get; set; }
    // Room-only bindings log an unavailable start once; the marker dies with the bounded room.
    internal bool StartUnavailableLogged { get; set; }
    // After a round the room may start again only from this tick on, and only with every non-master seat ready.
    internal long? AutoStartRematchAfter { get; set; }
    internal Task? AutoStartTask { get; set; }
    internal CancellationTokenSource? AutoStartCancellation { get; set; }
    internal ushort RedScore { get; set; }
    internal BubbleFighter.Fantasy.InGame.Handlers.RoundSupplyState? Supplies { get; set; }
    internal BubbleFighter.Fantasy.InGame.Handlers.RoundLucciState? Lucci { get; set; }
    // The star modes' active stars and the first-wave seeds copied at T-45.
    internal BubbleFighter.Fantasy.InGame.Handlers.RoundStarState? Stars { get; set; }

    /// <summary>
    /// Gets the current round's per-seat counters and team totals. A new object per round, so a
    /// report that arrives after the round ended cannot add to the next round's score.
    ///
    /// </summary>
    internal RoundScoring Scoring { get; private set; } = new(0);

    /// <summary>Gets or sets the last drop serial handed out this round (S2C 645); a new round starts at zero.</summary>
    internal int HordeDropSerial { get; set; }
    private readonly RoomMembers<RoomSeat> _members;
    private readonly int _maxSeats;
    private CancellationTokenSource? _resultCancellation;
    private Task _resultCancellationTask = Task.CompletedTask;

    /// <summary>Creates an empty room that already owns a create-time session blob.</summary>
    /// <param name="id">Stable room id (written as C2S 403 <c>unknown0</c> lookup).</param>
    /// <param name="maxSeats">Seat cap.</param>
    /// <param name="setting">Client <c>SessionSetting</c> from C2S 402.</param>
    /// <param name="queueCapacity">Waiting-operation bound for <see cref="Executor"/>, from <see cref="InGameOptions.RoomQueueCapacity"/>.</param>
    public PlayRoom(int id, int maxSeats, SessionSettingBlob setting, int queueCapacity)
    {
        Id = id;
        _maxSeats = maxSeats;
        Setting = setting;
        ModeId = setting.Map.Unknown4;
        _members = new RoomMembers<RoomSeat>(maxSeats);
        Executor = new RoomExecutor(queueCapacity);
    }

    /// <summary>Gets the room id.</summary>
    public int Id { get; }

    /// <summary>
    /// Gets the serial queue that membership and seat-state changes (join, leave, ready, team, detach) run on,
    /// so each operation's pushes are built from the seat table it just changed. The registry stops it when the
    /// room is deleted and the plugin stops every remaining one on unload.
    ///
    /// </summary>
    public RoomExecutor Executor { get; }

    /// <summary>Gets the lock used by the registry and handlers.</summary>
    public object Gate { get; } = new();

    /// <summary>Gets the current phase.</summary>
    public RoomPhase Phase { get; private set; } = RoomPhase.Waiting;

    /// <summary>
    /// Gets the create-time session blob echoed on later S2C 117. Map changes
    /// (C2S 513) and the start click's map selection (C2S 503) replace it via
    /// <see cref="UpdateMap"/>; every other field is frozen at create time.
    ///
    /// </summary>
    public SessionSettingBlob Setting { get; private set; }

    /// <summary>
    /// Replaces the room's map metadata under the room gate. Callers pass the
    /// selection the client reported, so the next room push and the S2C 510
    /// stage enter carry the map the host actually chose.
    ///
    /// </summary>
    /// <param name="map">The new map metadata.</param>
    public void UpdateMap(MapMetaBlob map)
    {
        lock (Gate)
            Setting = Setting with { Map = map };
    }

    /// <summary>
    /// Gets the parsed C2S 503 / 107 rule record when the room was created from
    /// that family, or <see langword="null"/> for the C2S 402 path.
    ///
    /// </summary>
    public RoomRuleRecord? CreateRules { get; init; }

    /// <summary>
    /// Gets or sets the display name the host typed on create. The S2C 101 room-list row writes it
    /// into the third wstring, and the room push echoes it into the install's <c>SessionSetting.Unknown8</c>
    /// name slot. An empty name stays empty.
    /// </summary>
    public string RoomName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the map key or random-selection marker of a room created from a typed
    /// request, retained between rounds; empty for rooms whose map lives only in <see cref="Setting"/>.
    ///
    /// </summary>
    public string MapKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the creating client's own settings block, handed back to members of its lineage.</summary>
    public BubbleFighter.Fantasy.Contracts.Rooms.RoomBuildSettings? BuildSettings { get; set; }

    /// <summary>
    /// Gets or sets the create mode id. The S2C 101 room-list row writes it
    /// into the nested MapMeta's <c>u16</c> slot, and the row label reads it.
    ///
    /// </summary>
    public ushort ModeId { get; set; }

    /// <summary>Gets a snapshot of seats. Callers must hold <see cref="Gate"/>.</summary>
    public IReadOnlyList<RoomSeat> Seats => _members.Members;

    /// <summary>Gets the delegated master or oldest remaining seat, or -1 for an empty room. Read under <see cref="Gate"/>.</summary>
    public int MasterSlot => _members.MasterSlot;

    // The room executor and gate serialize this with joins, departures and starts.
    internal bool TransferMaster(int slot) => _members.TransferMaster(slot);

    /// <summary>Checks readiness for a start click; the master needs no ready toggle (client <c>0x96DD20</c>).</summary>
    public bool CanStart => _members.CanStart;

    /// <summary>
    /// Gets the room's logical seat count, which is what the client's
    /// <c>sub_9D9250</c> derives from the session kind and what the S2C 602-605
    /// seat-info mask must therefore cover. The switch is <c>kind - 1</c> over
    /// <c>*(setting+64)</c> and reads two constant blocks in the image:
    /// <c>xmmword_1642160</c> = {32, 36, 12, 12},
    /// <c>xmmword_1642210</c> = {24, 32, 36, 24, 25, 25, 25, 25} and
    /// <c>xmmword_1642280</c> = {16, 32, 32, 36, 9, 10, 30, 60}.
    /// The 36 x 320-byte seat array the same function builds is a fixed
    /// capacity, so this count is a population the client waits on, not a
    /// storage bound. Evidence: verified switch, case bodies and both constant
    /// blocks (memory 0x1642160, 0x1642210 and 0x1642280).
    ///
    /// </summary>
    public int SeatCount => Setting.Kind switch
    {
        1 => 32,   // case 0: DWORD1(xmmword_1642210)
        2 => 16,   // case 1: the first dword of xmmword_1642280
        3 => 36,   // case 2: DWORD1(xmmword_1642160)
        4 => 32,   // case 3: DWORD1(xmmword_1642210)
        5 => 24,   // case 4: the last dword of xmmword_1642210
        6 => 16,   // case 5: the low qword of xmmword_1642280
        _ => 36,   // the constructor default
    };

    /// <summary>Seat count kr-1256 sub_9D9250 derives for kind 2 (a room).</summary>
    public const int RoomSeatCount = 16;

    /// <summary>Seat count kr-1256 sub_9D9250 derives for kind 3 (the plaza).</summary>
    public const int PlazaSeatCount = 36;

    /// <summary>Whether every seat is taken. Read under <see cref="Gate"/>.</summary>
    internal bool IsFull => _members.IsFull;

    /// <summary>The number of seats the room was created with.</summary>
    internal int MaxSeats => _maxSeats;

    // Empty seats the master closed (C2S 509); never more than the seat count.
    /// <summary>Empty seats the room master closed. Read under <see cref="Gate"/>.</summary>
    internal IReadOnlyCollection<int> ClosedSlots => _members.ClosedSlots;

    /// <summary>
    /// Closes or reopens an empty seat. Returns whether the state changed. Throws for a seat
    /// outside the room or one that holds a player. Called under <see cref="Gate"/>.
    ///
    /// </summary>
    internal bool SetSlotClosed(int slot, bool closed)
    {
        return _members.SetSlotClosed(slot, closed);
    }

    /// <summary>
    /// Whether other players can list or join the room. A client-created room stays unlisted until
    /// the host's create push has been sent, so no join can reach the room before the host's own
    /// install. Read and written under <see cref="Gate"/>.
    ///
    /// </summary>
    internal bool Listed { get; set; }

    /// <summary>
    /// The password the host set in the C2S 402 create dialog, or empty for an open room. A C2S 403
    /// join must carry the same string. Written before the room is published.
    ///
    /// </summary>
    internal string Password { get; set; } = string.Empty;

    /// <summary>
    /// The map the current round is played on, drawn at start from the room's selection (which may
    /// be a random marker) or resolved by the typed master's start request. The S2C 510 stage enter
    /// and the match transport's map check use it. Written under <see cref="Gate"/> when the start
    /// runs and cleared when the room returns to waiting.
    ///
    /// </summary>
    internal string RoundMapKey { get; set; } = string.Empty;

    /// <summary>
    /// The stage the current round loads: the <c>MapMeta</c> <c>Directory</c> of
    /// <see cref="RoundMapKey"/> (a pirate mission key such as <c>npc_Village07_Easy01</c> loads
    /// <c>rel_Village07_Easy02</c>). S2C 510 carries it in its second slot, which the client copies to
    /// session+232 and echoes in its C2S 3 map report (<c>sub_9E1EB0</c>, <c>sub_9E7080</c>), so the
    /// match transport's binding holds it too. Written under <see cref="Gate"/> when the start runs.
    ///
    /// </summary>
    internal string RoundStageKey { get; set; } = string.Empty;

    /// <summary>
    /// The rule record the master's start click carried (C2S 503), which sets the round length;
    /// a room created with C2S 402 has no <see cref="CreateRules"/>. Written under <see cref="Gate"/>.
    ///
    /// </summary>
    internal RoomRuleRecord? RoundRules { get; set; }

    /// <summary>
    /// The round lengths a typed start request carried, or null for a build whose start carries no rule record.
    /// Written under <see cref="Gate"/> when the start runs.
    ///
    /// </summary>
    internal BubbleFighter.Fantasy.Contracts.Rooms.RoomRoundTimeLimits? RoundTimeLimits { get; set; }

    // Seats vacated while a round was on, announced once the room is back to waiting. At most one
    // entry per seat, since a vacated seat cannot be refilled before the room returns to waiting.
    private readonly List<(int Slot, bool WasMaster)> _deferredDepartures = [];

    /// <summary>
    /// Records a seat vacated outside the waiting phase. Nothing is announced during a round
    /// (no S2C 511 or 502 is sent between S2C 510 and S2C 620); the members
    /// learn of it after S2C 620. Called under <see cref="Gate"/>.
    ///
    /// </summary>
    internal void DeferDeparture(int slot, bool wasMaster)
    {
        if (_deferredDepartures.Count >= _maxSeats)
            throw new InvalidOperationException($"Room {Id} deferred more departures than it has seats.");
        _deferredDepartures.Add((slot, wasMaster));
    }

    /// <summary>Takes the departures deferred during the last round. Called under <see cref="Gate"/>.</summary>
    internal (int Slot, bool WasMaster)[] TakeDeferredDepartures()
    {
        var taken = _deferredDepartures.ToArray();
        _deferredDepartures.Clear();
        return taken;
    }

    /// <summary>Adds a game session. Throws when the seat table is full.</summary>
    /// <param name="game">The joining session.</param>
    /// <param name="key">Relay key for this seat.</param>
    /// <param name="userNum">Player identity.</param>
    /// <param name="nickname">Player nickname.</param>
    public RoomSeat Add(ISession game, RelaySessionKey key, int userNum, string nickname)
    {
        if (IsFull)
            throw new InvalidOperationException($"Room {Id} is full ({_maxSeats} seats, {ClosedSlots.Count} closed).");
        if (Phase is RoomPhase.Closed or RoomPhase.Result)
            throw new InvalidOperationException($"Room {Id} is not accepting seats (phase {Phase}).");
        var seat = _members.Add(slot => new RoomSeat(slot, game, key, userNum, nickname));
        // 0x923790 selects waiting/ready positions by team; zero has no target.
        // Seats already carry teams before ready.
        if (Setting.Kind == SessionSettingBlob.MainMenuKind && Phase == RoomPhase.Waiting)
            AssignSpawnTeams();
        return seat;
    }

    /// <summary>Finds a seat by game session id.</summary>
    /// <param name="sessionId">The game session id.</param>
    public RoomSeat? FindByGame(long sessionId)
    {
        foreach (var seat in Seats)
        {
            if (seat.Game.Id == sessionId)
                return seat;
        }

        return null;
    }

    /// <summary>Finds a seat by relay key.</summary>
    /// <param name="key">The presented key.</param>
    public RoomSeat? FindByKey(RelaySessionKey key)
    {
        foreach (var seat in Seats)
        {
            if (seat.Key.Equals(key))
                return seat;
        }

        return null;
    }

    /// <summary>Copies current seats into identity rows for S2C 117 / 118.</summary>
    public RoomPlayerIdentity[] SnapshotIdentities()
    {
        var rows = new RoomPlayerIdentity[Seats.Count];
        for (var i = 0; i < Seats.Count; i++)
            rows[i] = Seats[i].ToIdentity();
        return rows;
    }

    /// <summary>Marks the room starting. Throws if not <see cref="RoomPhase.Waiting"/>.</summary>
    public void MarkStarting()
    {
        if (Phase != RoomPhase.Waiting)
            throw new InvalidOperationException($"Room {Id} cannot start from phase {Phase}.");
        CancelResultSequence();
        Phase = RoomPhase.Starting;
        CancelStarFever();
        StartGeneration++;
        MatchId = Guid.NewGuid().ToString("N");
        BlueScore = RedScore = 0;
        HordeDropSerial = 0;
        Scoring = new RoundScoring(maxSeats: _maxSeats);
        LifeChecks = new LifeObservations();
        ZombieKingSlot = null;
        DecidedWinnerTeam = null;
        DecidedWinnerSlots = null;
        ResetBotRoundState();
        foreach (var seat in Seats)
        {
            seat.Key = RoomRegistry.NewKey();
            seat.NativeCapability = null;
            seat.RoundReady = false;
            seat.RoundInitialized = false;
            seat.SceneReady = false;
            seat.ObjectAdded = false;
            seat.AwaitingRespawn = false;
            seat.InstantRevivalPending = false;
            seat.LastDeathActor = null;
            seat.PveRevivalRequested = false;
            seat.EndpointAdvertised = false;
            seat.RelayConfirmed = false;
            seat.RelayInitialized = false;
            seat.Movement = null;
            seat.Life = SeatLife.Alive;
            seat.DeathCleaned = false;
            seat.ResetRelay = null;
        }
    }

    /// <summary>Gets or sets the seat the zombie battle made its zombie king this round; null before the first conversion.</summary>
    internal int? ZombieKingSlot { get; set; }

    /// <summary>Gets or sets the single king-picker task retained for this room, so a test can observe it end.</summary>
    internal Task ZombieKingTask { get; set; } = Task.CompletedTask;

    /// <summary>Gets or sets the team a rule of the round declared the winner; null leaves it to the team totals.</summary>
    internal int? DecidedWinnerTeam { get; set; }

    /// <summary>
    /// Gets or sets the seats a rule of the round declared winners when it has no teams (the individual
    /// star mode); an empty set is a draw for everyone, null leaves the outcome to the teams.
    ///
    /// </summary>
    internal IReadOnlySet<int>? DecidedWinnerSlots { get; set; }

    /// <summary>Gets the generation used to reject stale countdown continuations after a round restart.</summary>
    public int StartGeneration { get; private set; }

    /// <summary>
    /// The current round start's identity, new at every start and never reused, even across restarts: the key of
    /// each seat's settlement receipt, so the round credits a player at most once. Empty before the first start.
    ///
    /// </summary>
    public string MatchId { get; private set; } = "";

    /// <summary>The current round's ignored life reports (observation only). Guarded by <see cref="Gate"/>.</summary>
    internal LifeObservations LifeChecks { get; private set; } = new();

    /// <summary>Gets or sets the start generation whose countdown was already announced; 0 for none.</summary>
    internal int CountdownGeneration { get; set; }

    /// <summary>Gets or sets the single countdown task retained for this bounded room.</summary>
    public Task CountdownTask { get; set; } = Task.CompletedTask;

    /// <summary>Gets or sets the single T-45 Fever/second-wave task retained for this bounded room.</summary>
    internal Task StarFeverTask { get; set; } = Task.CompletedTask;

    /// <summary>Gets or sets the cancellation source that ends this round's T-45 task.</summary>
    internal CancellationTokenSource? StarFeverCancellation { get; set; }

    /// <summary>Gets whether the current mode requires a red or blue team for seat and spawn placement.</summary>
    internal bool RequiresSpawnTeams => Setting.Map.Unknown4 is 1 or 3 or 6 or 9 or 11 or 12 or 14 or 16 or 18 or 19
        or 20 or 21 or 24 or 27 or 29 or 31 or 33 or 34 or 35 or 36 or 37 or 38 or 39;

    /// <summary>
    /// Assigns unassigned players to the smaller team before publishing waiting or start rosters.
    /// kr-1256 sub_923790 and kr-1256 sub_C80220 require team 1 or 2 for these modes;
    /// team zero leaves its validity flag clear and kr-1256 sub_6D2040 skips placement.
    /// Existing team choices and observers are preserved. Call under <see cref="Gate"/>.
    ///
    /// </summary>
    public void AssignSpawnTeams()
    {
        // kr-1256 sub_1150F80, excluding the earlier special-mode branches in kr-1256 sub_C80220.
        if (!RequiresSpawnTeams)
            return;
        var first = Seats.Count(seat => seat.Team == 1) + Bots.Count(bot => bot.Appearance.Team == 1);
        var second = Seats.Count(seat => seat.Team == 2) + Bots.Count(bot => bot.Appearance.Team == 2);
        foreach (var seat in Seats.OrderBy(seat => seat.Slot))
        {
            if (seat.Team != 0)
                continue;
            if (first <= second)
            {
                seat.Team = 1;
                first++;
            }
            else
            {
                seat.Team = 2;
                second++;
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the S2C 510 stage move has gone out and the rest of
    /// the start chain is waiting for the client's relay confirmation. Only the
    /// direct-start training room uses this: its whole chain in the create
    /// burst was dropped because the stage did not exist yet.
    ///
    /// </summary>
    public bool StartPending
    {
        get => _startPending;
        set
        {
            lock (Gate)
                _startPending = value;
        }
    }

    /// <summary>Clears the pending-start flag under the room gate.</summary>
    public void ClearStartPending()
    {
        lock (Gate)
            _startPending = false;
    }

    private bool _startPending;

    /// <summary>
    /// Gets or sets when the round the S2C 608 initialize armed ends, in UTC. <see langword="null"/>
    /// when no timed round is running. The client sends nothing when its own copy of the clock expires, so the
    /// server arms the expiration itself and checks it on a server timer and also checked on the session's own
    /// traffic.
    /// </summary>
    public DateTime? RoundEndsAtUtc { get; set; }

    /// <summary>Gets or sets the single result sequence retained for this room, including all display delays.</summary>
    public Task ResultTask { get; set; } = Task.CompletedTask;

    internal CancellationTokenSource BeginResultSequence(CancellationToken lifetime)
    {
        if (_resultCancellation is not null)
            throw new InvalidOperationException($"Room {Id} still owns result cancellation state.");
        _resultCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _resultCancellationTask = Task.CompletedTask;
        return _resultCancellation;
    }

    private void CancelResultSequence()
    {
        // CancelAsync never runs registered callbacks inline while Gate is held.
        if (_resultCancellation is { IsCancellationRequested: false } source)
            _resultCancellationTask = source.CancelAsync();
    }

    internal async Task ReleaseResultSequenceAsync(CancellationTokenSource source)
    {
        Task cancellation;
        lock (Gate)
        {
            cancellation = _resultCancellationTask;
            if (ReferenceEquals(_resultCancellation, source))
                _resultCancellation = null;
        }
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>Gets or sets whether the S2C 612 result open went out.</summary>
    public bool ResultOpenSent { get; set; }

    /// <summary>Gets or sets whether S2C 615 advanced clients to the reward stage.</summary>
    public bool ResultCloseSent { get; set; }

    /// <summary>Cancels the current round's T-45 task without running its continuation inline while the room gate is held.</summary>
    internal void CancelStarFever()
    {
        var source = StarFeverCancellation;
        StarFeverCancellation = null;
        if (source is { IsCancellationRequested: false })
            _ = source.CancelAsync();
    }

    /// <summary>
    /// Returns the room from the result phase to waiting after S2C 620, clearing the round clock and the result flags under the room
    /// gate, so the next start click finds a reusable room.
    ///
    /// </summary>
    public void ResetToWaiting()
    {
        lock (Gate)
        {
            CancelResultSequence();
            Phase = RoomPhase.Waiting;
            AutoStartArmed = false;
            Rescues = null;
            Supplies = null;
            Lucci = null;
            Stars = null;
            CancelStarFever();
            RoundMapKey = string.Empty;
            RoundStageKey = string.Empty;
            RoundEndsAtUtc = null;
            ResultOpenSent = false;
            ResultCloseSent = false;

            // The counters belong to the round that just closed. A seat that
            // stays on keeps them, so the next round's record cannot be read as
            // the previous one.
            foreach (var seat in Seats)
            {
                seat.RoundStats = null;
                seat.NativeCapability = null;
                seat.Ready = false;
                seat.InShop = false;
                seat.InInventory = false;
            }
        }
    }

    /// <summary>Gets the <see cref="Environment.TickCount64"/> at which the room last entered playing, or 0.</summary>
    public long PlayingSince { get; private set; }

    /// <summary>Marks the room playing.</summary>
    public void MarkPlaying()
    {
        if (Phase is RoomPhase.Closed or RoomPhase.Result)
            throw new InvalidOperationException($"Room {Id} cannot enter playing from phase {Phase}.");
        if (Phase != RoomPhase.Playing)
            PlayingSince = Environment.TickCount64;
        Phase = RoomPhase.Playing;
    }

    /// <summary>Marks the room in result.</summary>
    public void MarkResult()
    {
        if (Phase == RoomPhase.Closed)
            throw new InvalidOperationException($"Room {Id} is closed.");
        Phase = RoomPhase.Result;
    }

    /// <summary>Removes a seat. Closes the room when none remain.</summary>
    /// <param name="sessionId">Game or relay session id.</param>
    public void RemoveSession(long sessionId)
    {
        _members.RemoveAll(s => s.Game.Id == sessionId || s.Relay?.Id == sessionId);
        if (Seats.Count == 0)
        {
            RemoveBots();
            CancelResultSequence();
            TutorialCancellation?.Cancel();
            CancelStarFever();
            Phase = RoomPhase.Closed;
        }
    }
}
