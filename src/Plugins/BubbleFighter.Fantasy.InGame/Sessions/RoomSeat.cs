using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>A character's life in a round whose clients report it.</summary>
internal enum SeatLife
{
    /// <summary>Playing normally.</summary>
    Alive,

    /// <summary>Trapped in a bubble; a release or an enemy's pop decides what follows.</summary>
    Bubble,

    /// <summary>Dead; waits for its death presentation and then asks for a reset.</summary>
    Dead,

    /// <summary>The reset was granted; the client's confirmation on the relay completes it.</summary>
    ResetPending,
}

/// <summary>One player seat inside a <see cref="PlayRoom"/>.</summary>
public sealed class RoomSeat : IRoomMember
{
    internal NativeMatchCapability? NativeCapability { get; set; }
    internal int RelayGeneration { get; set; }
    // The player's merged movement this round; a partial report only means something against it.
    internal PlayerMovementState? Movement { get; set; }
    // The character's life this round where the clients report it; see RoundFlow.Life.
    internal SeatLife Life { get; set; }
    internal bool DeathCleaned { get; set; }
    // The relay a granted reset belongs to; a reset granted on an earlier relay died with it.
    internal ISession? ResetRelay { get; set; }
    internal bool AwaitingRespawn { get; set; }
    internal bool InstantRevivalPending { get; set; }
    internal int? LastDeathActor { get; set; }
    internal bool PveRevivalRequested { get; set; }
    /// <summary>Creates a seat for a game session.</summary>
    /// <param name="slot">Seat index.</param>
    /// <param name="game">The game-protocol session.</param>
    /// <param name="key">Relay key issued with S2C 571.</param>
    /// <param name="userNum">Identity written into <c>RoomPlayerInfo</c> +8.</param>
    /// <param name="nickname">Identity written into <c>RoomPlayerInfo</c> +12.</param>
    public RoomSeat(int slot, ISession game, RelaySessionKey key, int userNum, string nickname)
    {
        Slot = slot;
        Game = game;
        Key = key;
        UserNum = userNum;
        Nickname = nickname;
        ColorIndex = game.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out var player)
            ? player.Identity.ColorIndex : (ushort)0;
    }

    /// <summary>Gets the seat index used by S2C 584 and <c>RoomPlayerInfo</c> +3.</summary>
    public int Slot { get; }

    /// <summary>Gets the game-protocol session.</summary>
    public ISession Game { get; }

    /// <summary>Gets the relay key this seat must present.</summary>
    public RelaySessionKey Key { get; internal set; }

    /// <summary>Gets the player identity number.</summary>
    public int UserNum { get; }

    /// <summary>Gets the nickname displayed on this seat's member row.</summary>
    public string Nickname { get; internal set; }

    /// <summary>Gets the committed avatar color retained in the room snapshot.</summary>
    public ushort ColorIndex { get; internal set; }

    /// <summary>Gets or sets the relay-protocol session after C2S 1 succeeds.</summary>
    public ISession? Relay { get; set; }

    /// <summary>Gets or sets whether the current round advertised this seat's match capability.</summary>
    public bool EndpointAdvertised { get; set; }
    /// <summary>Gets or sets whether the current match connection finished scene registration.</summary>
    public bool RelayInitialized { get; set; }
    /// <summary>Gets or sets whether the game connection acknowledged successful match initialization.</summary>
    public bool RelayConfirmed { get; set; }
    /// <summary>Gets or sets whether the game connection reported its loaded scene.</summary>
    public bool SceneReady { get; set; }

    /// <summary>
    /// Gets or sets the round generation whose match traffic this seat sends and receives on its game connection
    /// because its match connection reported failure; 0 when the seat uses its match connection.
    ///
    /// </summary>
    internal int GameLinkGeneration { get; set; }

    /// <summary>Gets or sets whether the round already acknowledged this seat's request to add its own character.</summary>
    public bool ObjectAdded { get; set; }

    /// <summary>Gets or sets the manual or automatic ready state reported by C2S 505.</summary>
    public bool Ready { get; set; }

    /// <summary>Gets or sets whether this player's shop dialog is open.</summary>
    public bool InShop { get; set; }

    /// <summary>Gets or sets whether this player's inventory dialog is open.</summary>
    public bool InInventory { get; set; }

    /// <summary>
    /// Gets or sets the seat's team label: 0 = unassigned, 1 = blue, 2 = red. Team modes assign a
    /// label before the first waiting-room publish. The value lands in <c>RoomPlayerInfo.unknown4</c>, which
    /// <c>sub_980730</c> switches on for the per-slot team board. C2S 515 flips it.
    /// </summary>
    public byte Team { get; set; }

    /// <summary>Gets or sets whether this round published initialization before its acknowledgement can arrive.</summary>
    public bool RoundInitialized { get; set; }

    /// <summary>Gets or sets whether C2S 17 arrived.</summary>
    public bool RoundReady { get; set; }

    /// <summary>
    /// Gets or sets the thirty <c>i32</c> counters this seat reported in C2S 103
    /// when the round result opened, or <see langword="null"/> when none arrived in
    /// the current round. They are built by the client's own S2C 612 handler from
    /// <c>dword_18E7558</c> (<c>sub_9E88C0</c>, element reader
    /// <c>sub_113F230</c>), slot 0 echoes the second field of the S2C 608 that
    /// armed the round and the rest are client-side accumulations, so this is the
    /// only per-seat record of what the player did - it was previously parsed and
    /// then dropped on the floor.
    ///
    /// </summary>
    public IReadOnlyList<int>? RoundStats { get; set; }

    /// <summary>Gets or sets whether this seat already received the one-time room-state
    /// push for its C2S 517 re-send. The client re-sends 517 on every probe
    /// while waiting for the room; answering each with the full push starved
    /// the connection, so the push is once per seat and later re-sends only get
    /// the handshake echo.</summary>
    public bool JoinAcked { get; set; }

    /// <summary>
    /// Gets or sets the equipped character id (300000..301999) this seat
    /// presents to the profile map. Zero means unresolved; the room push then
    /// falls back to <see cref="LocalCharacterId"/> and otherwise skips the
    /// seat rather than writing an empty record.
    ///
    /// </summary>
    public int CharacterId { get; set; }

    /// <summary>Gets or sets the committed worn item snapshot used by every profile update; null means no inventory provider.</summary>
    public IReadOnlyList<int>? EquippedItemIds { get; set; }

    /// <summary>
    /// Gets or sets the lobby character type for this seat (1 = Dao on the login path). The
    /// character item is 300000 + type, so a seat without a resolved equipment id still renders through this
    /// fallback. The server-side equipped character identity takes precedence; the login character type
    /// supplies the fallback.
    /// </summary>
    public int LocalCharacterId { get; set; } = 300001;

    /// <summary>Gets or sets the player level written into the S2C 118 row's second field.</summary>
    public int Level { get; set; } = 1;

    /// <summary>Builds the S2C 117 / 118 identity row for this seat.</summary>
    public RoomPlayerIdentity ToIdentity() => new(UserNum, Nickname, checked((byte)Slot), Math.Max(1, Level),
        Team, Ready, InShop, InInventory);
}

/// <summary>Owns one opaque native capability for a seat's current round.</summary>
internal sealed record NativeMatchCapability(int Generation, byte[] Bytes);
