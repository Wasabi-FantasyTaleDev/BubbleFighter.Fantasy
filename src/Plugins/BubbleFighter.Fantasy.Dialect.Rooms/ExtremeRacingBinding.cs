using BubbleFighter.Fantasy.Abstractions;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The ExtremeRacing round's exchange (round mode 24): it reads the goal touch and writes the race clocks, the round
/// change and the record updates through <c>packets/extreme-racing.yaml</c>, numbered by the profile's opcode table.
/// Which round runs, when a clock starts and how records are scored is the round flow's decision. The checkpoint
/// touch is a round-feed route in <c>packets/builds.yaml</c>, not part of this binding.
/// </summary>
internal sealed class ExtremeRacingBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundExtremeRacingGoalReported>, IMessageSender<RoundExtremeRacingTimerChanged>,
      IMessageSender<RoundExtremeRacingRoundChanged>, IMessageSender<RoundExtremeRacingRecordsUpdated>,
      IMessageSender<RoundExtremeRacingInitialized>, IMessageSender<RoundExtremeRacingCannonCommand>
{
    // kr-1256 sub_CE6BA0 scope 1: every record, the ranking board and the team round totals.
    private const int AllRecordsScope = 1;
    private readonly IReadOnlyDictionary<RoundExtremeRacingTimer, int> _timerCodes = ReadTimerCodes(packets, build);

    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts, then registers the goal request and the three senders.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var goal = table.C2S("gcpExtremeRacingGoal_");
        _ = table.S2C("gspExtremeRacingTimer_");
        _ = table.S2C("gspExtremeRacingRound_");
        _ = table.S2C("gspExtremeRacingUpdatePlayRecord_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, goal, async (session, packet, ct) =>
        {
            var report = ReadGoal(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No ExtremeRacing goal handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundExtremeRacingGoalReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingTimerChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingRoundChanged>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundExtremeRacingRecordsUpdated>>(this));
        logger.LogInformation("dialect.rooms: ExtremeRacing exchange of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>Registers only the optional stage initializer, without changing the ordinary race exchange's requirements.</summary>
    internal void MountInitialization(IPluginContext context)
    {
        RequireInitializationLayouts(packets, build);
        _ = table.S2C("gspExtremeRacingInitGameData_");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundExtremeRacingInitialized>>(this));
        logger.LogInformation("dialect.rooms: ExtremeRacing initialization of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>
    /// Registers only the cannon command sender. The shot itself is the <c>extreme_racing_cannon</c> round-feed route
    /// of <c>packets/builds.yaml</c>; neither schedules anything or selects a recipient.
    /// </summary>
    internal void MountCannon(IPluginContext context)
    {
        RequireCannonLayouts(packets, build);
        _ = table.S2C("gspExtremeRacingCannon_");
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundExtremeRacingCannonCommand>>(this));
        logger.LogInformation("dialect.rooms: ExtremeRacing cannon command of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>Writes one cannon command to the receiving client.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingCannonCommand message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingCannon_"), WriteCannon(packets, build, message), ct);

    /// <summary>Writes the group, identity, weapon and scale words; builds without the scale word cannot be written here.</summary>
    internal static byte[] WriteCannon(PacketTables packets, string build, RoundExtremeRacingCannonCommand message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireCannonLayouts(packets, build);
        return PacketCodec.Write(packets, build, "ExtremeRacingCannon", new Dictionary<string, object?>
        {
            ["group"] = message.Group,
            ["identity"] = message.Identity,
            ["weapon"] = message.Weapon,
            ["scale"] = message.Scale,
        });
    }

    /// <summary>
    /// Decodes the 38-byte shot with no trailing bytes. Finite-vector and ownership checks belong to the round, which
    /// forwards the original bytes unchanged.
    /// </summary>
    internal static RoundExtremeRacingCannonShot ReadCannonShot(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "ExtremeRacingCannonShot", payload);
        return new((int)values["actor"], (ushort)values["weapon"],
            new((float)values["origin_x"], (float)values["origin_y"], (float)values["origin_z"]),
            new((float)values["direction_x"], (float)values["direction_y"], (float)values["direction_z"]),
            (uint)(int)values["cannon_identity"], (uint)(int)values["serial"]);
    }

    /// <summary>Checks the command and shot layouts of a build whose command carries the scale word.</summary>
    internal static void RequireCannonLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingCannon", "ExtremeRacing cannon",
            "group:u32,identity:u32,weapon:u32,scale:f32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingCannonShot", "ExtremeRacing cannon",
            "actor:u32,weapon:u16,origin_x:f32,origin_y:f32,origin_z:f32,direction_x:f32,direction_y:f32,direction_z:f32,"
            + "cannon_identity:u32,serial:u32");
    }

    /// <summary>Writes the complete initial object identities and both coin maps for a loaded race stage.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingInitialized message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingInitGameData_"), WriteInitialization(packets, build, message), ct);

    /// <summary>Writes a race clock change.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingTimerChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingTimer_"), WriteTimer(packets, build, message, _timerCodes), ct);

    /// <summary>Writes a race round change.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingRoundChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingRound_"), WriteRound(packets, build, message), ct);

    /// <summary>Writes record updates for every listed seat.</summary>
    public ValueTask SendAsync(ISession session, RoundExtremeRacingRecordsUpdated message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspExtremeRacingUpdatePlayRecord_"), WriteRecords(packets, build, message), ct);

    /// <summary>Reads the one-word goal touch, with no trailing bytes.</summary>
    internal static RoundExtremeRacingGoalReported ReadGoal(PacketTables packets, string build, ReadOnlySpan<byte> payload) =>
        new((int)PacketCodec.ReadScalars(packets, build, "ExtremeRacingGoal", payload)["round"]);

    /// <summary>Writes the code, the duration and the activity word.</summary>
    internal static byte[] WriteTimer(PacketTables packets, string build, RoundExtremeRacingTimerChanged message) =>
        WriteTimer(packets, build, message, ReadTimerCodes(packets, build));

    private static byte[] WriteTimer(PacketTables packets, string build, RoundExtremeRacingTimerChanged message,
        IReadOnlyDictionary<RoundExtremeRacingTimer, int> codes) =>
        PacketCodec.Write(packets, build, "ExtremeRacingTimer", new Dictionary<string, object?>
        {
            ["code"] = codes.TryGetValue(message.Timer, out var code) ? code : throw new ArgumentOutOfRangeException(nameof(message)),
            ["duration_ms"] = message.DurationMs,
            ["active"] = message.Active,
        });

    private static IReadOnlyDictionary<RoundExtremeRacingTimer, int> ReadTimerCodes(PacketTables packets, string build)
    {
        const string key = "extreme_racing_timers";
        if (!packets.ValuesOf(build).TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> values)
            throw new ConfigException($"Build '{build}' requires its native {key} mapping.");
        var result = new Dictionary<RoundExtremeRacingTimer, int>();
        foreach (var timer in Enum.GetValues<RoundExtremeRacingTimer>())
        {
            if (!values.TryGetValue(timer.ToString(), out var value) || value is not string text
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code <= 0)
                throw new ConfigException($"Build '{build}' requires a positive native {key}.{timer} code.");
            result.Add(timer, code);
        }
        if (values.Count != result.Count || result.Values.Distinct().Count() != result.Count)
            throw new ConfigException($"Build '{build}' requires exactly three distinct native {key} codes.");
        return result;
    }

    /// <summary>Writes the round word after the unused leading word, which keeps its table default.</summary>
    internal static byte[] WriteRound(PacketTables packets, string build, RoundExtremeRacingRoundChanged message) =>
        PacketCodec.Write(packets, build, "ExtremeRacingRound", new Dictionary<string, object?> { ["round"] = message.Round });

    /// <summary>Writes the all-records scope and each seat's field updates.</summary>
    internal static byte[] WriteRecords(PacketTables packets, string build, RoundExtremeRacingRecordsUpdated message) =>
        PacketCodec.Write(packets, build, "ExtremeRacingRecords", new Dictionary<string, object?>
        {
            ["scope"] = AllRecordsScope,
            ["records"] = message.Records.Select(record => (object?)new Dictionary<string, object?>
            {
                ["seat"] = checked((ushort)record.Seat),
                ["values"] = record.Values.Select(value => (object?)new Dictionary<string, object?>
                {
                    ["field"] = checked((byte)value.Field),
                    ["value"] = value.Value,
                }).ToArray(),
            }).ToArray(),
        });

    /// <summary>Writes complete consecutive coin maps and the unused compatibility word after validating bounded counts.</summary>
    internal static byte[] WriteInitialization(PacketTables packets, string build, RoundExtremeRacingInitialized message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Coins);
        var supportsStoneDisposers = RequireInitializationLayouts(packets, build);
        if (!supportsStoneDisposers && message.StoneDisposerCount != 0)
            throw new InvalidDataException("This racing initialization layout cannot represent stone disposers.");
        var fixedBytes = PacketCodec.Write(packets, build, "ExtremeRacingInitialization", new Dictionary<string, object?>()).Length;
        // cn-738 sub_413690 and kr-1256 sub_1155540 read two eight-byte entries per coin; fixed fields come from the table.
        if (message.Coins.Count > (ushort.MaxValue - fixedBytes) / 16 || message.StoneDisposerCount < 0
            || message.FirstCannonIdentity == 0 || message.FirstCoinIdentity == 0
            || supportsStoneDisposers && message.FirstStoneDisposerIdentity == 0)
            throw new InvalidDataException("ExtremeRacing initialization has invalid identities or excessive counts.");
        for (var index = 0; index < message.Coins.Count; index++)
        {
            var coin = message.Coins[index];
            if ((ulong)message.FirstCoinIdentity + (uint)index > uint.MaxValue
                || coin.Identity != message.FirstCoinIdentity + (uint)index || coin.Variant != 0 || coin.Round < 1)
                throw new InvalidDataException("ExtremeRacing initialization requires consecutive coins with supported presentation and rounds.");
        }
        if (message.StoneDisposerCount > 0
            && (ulong)message.FirstStoneDisposerIdentity + (uint)message.StoneDisposerCount - 1 > uint.MaxValue)
            throw new InvalidDataException("ExtremeRacing stone-disposer identities overflow.");
        return PacketCodec.Write(packets, build, "ExtremeRacingInitialization", new Dictionary<string, object?>
        {
            ["first_cannon_identity"] = message.FirstCannonIdentity,
            // kr-1256 sub_CE6F80 ignores this read word; keep its unknown meaning and serialize zero explicitly.
            ["unknown_8"] = 0,
            ["first_coin_identity"] = message.FirstCoinIdentity,
            ["coin_count"] = message.Coins.Count,
            ["coin_variants"] = message.Coins.Select(coin => (object?)new Dictionary<string, object?>
            {
                ["identity"] = coin.Identity, ["variant"] = coin.Variant,
            }).ToArray(),
            ["coin_rounds"] = message.Coins.Select(coin => (object?)new Dictionary<string, object?>
            {
                ["identity"] = coin.Identity, ["round"] = coin.Round,
            }).ToArray(),
            ["first_stone_disposer_identity"] = message.FirstStoneDisposerIdentity,
            ["stone_disposer_count"] = message.StoneDisposerCount,
        });
    }

    /// <summary>Checks the initialization and map entries, returning whether the table includes the stone-disposer pair.</summary>
    internal static bool RequireInitializationLayouts(PacketTables packets, string build)
    {
        var supportsStoneDisposers = packets.Resolve(build, "ExtremeRacingInitialization")
            .Any(field => field.Name is "first_stone_disposer_identity" or "stone_disposer_count");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingInitialization", "ExtremeRacing initialization",
            "first_cannon_identity:u32,unknown_8:u32,first_coin_identity:u32,coin_count:u32,"
            + "coin_variants:list<ExtremeRacingCoinVariant>,coin_rounds:list<ExtremeRacingCoinRound>"
            + (supportsStoneDisposers ? ",first_stone_disposer_identity:u32,stone_disposer_count:u32" : ""));
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingCoinVariant", "ExtremeRacing initialization",
            "identity:u32,variant:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingCoinRound", "ExtremeRacing initialization",
            "identity:u32,round:u32");
        return supportsStoneDisposers;
    }

    /// <summary>Checks every structure this binding reads or writes.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        _ = ReadTimerCodes(packets, build);
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingTimer", "ExtremeRacing",
            "code:u32,duration_ms:u32,active:bool");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRound", "ExtremeRacing",
            "unknown_0:u32,round:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingGoal", "ExtremeRacing", "round:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecords", "ExtremeRacing",
            "scope:u32,records:list<ExtremeRacingRecord>");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecord", "ExtremeRacing",
            "seat:u16,values:list<ExtremeRacingRecordValue>");
        PacketLayoutContract.RequireSignature(packets, build, "ExtremeRacingRecordValue", "ExtremeRacing",
            "field:u8,value:u32");
    }
}
