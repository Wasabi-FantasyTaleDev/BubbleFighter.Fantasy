using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// The per-category attack and defence chances of the build's own <c>Config/CriticalHitTable.xml</c>. The sheet's
/// <c>[Desc]</c> row states each column as a 0-100 chance by previous round, current round and rank, and the
/// client sums one row of each family (cn-279 sub_7EC4D0 reads <c>crtAttackFactor</c> the same way).
/// </summary>
internal sealed class CriticalHitChances
{
    // The ten categories the client reads (cn-279 sub_7EC4D0); a table without one of them is not this format.
    private static readonly string[] Categories =
        ["prevWin", "prevDraw", "prevLose", "prevLoseInRow", "win", "draw", "lose", "highRank", "midRank", "lowRank"];

    private readonly IReadOnlyDictionary<string, double> _attack;

    private CriticalHitChances(IReadOnlyDictionary<string, double> attack) => _attack = attack;

    /// <summary>Reads and validates the attack column of one worksheet.</summary>
    /// <param name="data">The build's client data.</param>
    /// <param name="path">The workbook's client path.</param>
    /// <param name="sheet">The worksheet holding the category rows.</param>
    /// <param name="maxBytes">The workbook size cap.</param>
    /// <returns>The parsed chances.</returns>
    /// <exception cref="ConfigException">The workbook lacks a category or column, holds a value outside 0-100, or
    /// states a non-zero defence chance, whose seed-value distribution is not established.</exception>
    internal static CriticalHitChances Load(IClientDataStore data, string path, string sheet, int maxBytes)
    {
        IReadOnlyList<IReadOnlyList<string>> rows;
        try
        {
            rows = SpreadsheetWorkbook.Load(WorkbookFile.FromBytes(path, data.Read(path)), maxBytes,
                "critical_hit_table_max_bytes", [sheet])[sheet];
        }
        catch (Exception failure) when (failure is FileNotFoundException or InvalidOperationException or System.Xml.XmlException)
        {
            throw new ConfigException($"Native scene seeds cannot read '{path}' sheet '{sheet}': {failure.Message}");
        }
        var header = rows.FirstOrDefault(row => row.Count > 0 && row[0] == "[Field]")
            ?? throw new ConfigException($"'{path}' sheet '{sheet}' has no [Field] row.");
        int Column(string name)
        {
            var index = header.ToList().IndexOf(name);
            return index > 0 ? index : throw new ConfigException($"'{path}' sheet '{sheet}' has no '{name}' column.");
        }
        var category = Column("category");
        var attack = Column("crtAttackFactor");
        var defence = Column("crtDefenceFactor");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Count <= Math.Max(attack, defence) || !Categories.Contains(row[category]))
                continue;
            var name = row[category];
            if (values.ContainsKey(name))
                throw new ConfigException($"'{path}' sheet '{sheet}' states category '{name}' twice.");
            var chance = Number(row[attack], name, "crtAttackFactor");
            // cn-324 sub_4A9540 scales each defence byte by approximately 0.01. Its nonzero table distribution
            // is not established, so only the shipped all-zero column is accepted.
            if (Number(row[defence], name, "crtDefenceFactor") != 0)
                throw new ConfigException($"'{path}' category '{name}' states a defence chance; its seed-value distribution is unproven.");
            values[name] = chance;
        }
        var missing = Categories.Where(name => !values.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            throw new ConfigException($"'{path}' sheet '{sheet}' lacks the categories {string.Join(", ", missing)}.");
        return new(values);

        double Number(string text, string name, string column) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100
                ? value
                : throw new ConfigException($"'{path}' category '{name}' column {column} is not a 0-100 chance: '{text}'.");
    }

    /// <summary>
    /// The chance, 0-1, of one critical bit at round start: the current round is still a draw, every score is tied so
    /// the client ranks its player last (cn-279 sub_704060 counts every equal score as not worse), and the previous
    /// round's row comes from the server's history. A first round takes the prevWin row, the zero state the
    /// client's previous-round field starts in (cn-279 sub_705DF0 writes 0 for a win).
    /// </summary>
    /// <param name="previous">The seat's previous-round result.</param>
    /// <returns>The summed chance as a probability.</returns>
    internal double RoundStartChance(MatchPreviousRound previous)
    {
        var history = previous switch
        {
            MatchPreviousRound.Drew => "prevDraw",
            MatchPreviousRound.Lost => "prevLose",
            MatchPreviousRound.LostInRow => "prevLoseInRow",
            _ => "prevWin",
        };
        return Math.Clamp((_attack["draw"] + _attack["lowRank"] + _attack[history]) / 100.0, 0, 1);
    }
}

/// <summary>
/// Issues every seat's native seed tables for a starting round. The seeded player paths consume the installed
/// decisions directly (cn-324 sub_4A94C0 and cn-406 sub_472B20), independently of builds that roll locally. A relay
/// whose scene accept already installs the critical and cancellation tables (kr-1256) is issued defence tables only:
/// it is built without critical opcodes and chances.
/// </summary>
internal sealed partial class SceneSeedAuthority(string protocol, string build, PacketTables tables, ushort? criticalOpcode,
    ushort defenceOpcode, ushort? cancelOpcode, CriticalHitChances? chances, int maxSessions, Random random,
    WeaponCriticalCatalog? weapons = null, DefenceSeedCatalog? defences = null)
    : IMessageSender<MatchSeedTablesRequested>, IMatchSeedInputValidator, IMatchCriticalSeedAuthority
{
    // The critical family is all or nothing: its two opcodes and its chances, and weapon contributions need it.
    private readonly bool _critical = (criticalOpcode, cancelOpcode, chances) switch
    {
        ({ }, { }, { }) => true,
        (null, null, null) when weapons is null => false,
        _ => throw new ArgumentException("Critical seed tables need both opcodes and the chances; weapons need all three."),
    };

    // The expander copies 32 words into each of the first two weapon-slot tables and leaves the other three zero
    // (cn-324 sub_4998C0 stops at 32 words; kr-812 sub_54A660 is its counterpart). The roll reads the table of the
    // current weapon slot, Character+6452 (cn-324 sub_4A94C0; the slot setter is cn-324 sub_4CD3C0).
    internal const int WordsPerTable = 32;

    // Each table holds 1000 one-bit rolls; the cursor wraps at 1000 (cn-324 sub_4A94C0), so the last 24 bits of the
    // 32 words are never read.
    internal const int BitsPerTable = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, Issuance> _issued = [];

    private sealed class Issuance(MatchSeedTablesRequested request)
    {
        internal MatchSeedTablesRequested Request { get; } = request;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal (double Primary, double Sidearm)? LastChances { get; set; }
        internal CriticalRevision? Revision { get; set; }
    }

    /// <inheritdoc />
    public string Protocol => protocol;

    private CriticalHitChances Chances =>
        chances ?? throw new InvalidOperationException("This relay is issued no critical seed tables.");

    private ushort CriticalOpcode =>
        criticalOpcode ?? throw new InvalidOperationException("This relay is issued no critical seed tables.");

    /// <summary>Identifies modes whose critical tables require authoritative equipment and accepted weapon transitions.</summary>
    public bool RequiresFixedEquipment(int modeId) => weapons?.AppliesTo(modeId) == true;

    /// <summary>Resolves optional weapon inputs without drawing bits or reserving an installation.</summary>
    public void Validate(MatchSeedTablesRequested request)
    {
        if (weapons is not null) _ = weapons.Contributions(request.Equipment);
        var occupants = DefenceSeedRoster.Occupants(request);
        if (defences is not null)
            foreach (var occupant in occupants) _ = defences.Range(occupant.Equipment);
    }

    /// <summary>
    /// Queues one round's critical, defence and cancellation tables in order. Duplicate requests share the original
    /// outcome without reinstalling tables; a failed or canceled attempt cannot be retried within that round.
    /// </summary>
    public async ValueTask SendAsync(ISession session, MatchSeedTablesRequested message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Native seed tables belong to another client profile.");
        Validate(message);
        Issuance issuance;
        var ownsIssuance = false;
        lock (_gate)
        {
            if (_issued.TryGetValue(session.Id, out var previous))
            {
                if (previous.Request.RoomId == message.RoomId && previous.Request.Generation == message.Generation)
                {
                    if (previous.Request != message)
                        throw new InvalidOperationException("Native seed inputs changed within the same room generation.");
                    issuance = previous;
                }
                else
                {
                    if (!previous.Completion.Task.IsCompleted || previous.Revision?.Completion.Task.IsCompleted == false)
                        throw new InvalidOperationException("Native seed installation is still in progress for this relay session.");
                    if (previous.Request.RoomId == message.RoomId && message.Generation < previous.Request.Generation)
                        throw new InvalidOperationException("Native seed request belongs to an older room generation.");
                    issuance = new(message);
                    _issued[session.Id] = issuance;
                    ownsIssuance = true;
                }
            }
            else
            {
                if (_issued.Count >= maxSessions)
                    throw new InvalidOperationException($"Native seed authority reached its {maxSessions} session limit.");
                issuance = new(message);
                _issued.Add(session.Id, issuance);
                ownsIssuance = true;
            }
        }
        if (ownsIssuance)
        {
            try
            {
                (double Primary, double Sidearm)? probabilities = null;
                byte[]? critical = null;
                if (_critical)
                {
                    var baseline = Chances.RoundStartChance(message.PreviousRound);
                    var contribution = weapons?.Contributions(message.Equipment) ?? (Primary: 0.0, Sidearm: 0.0);
                    var chance = (Primary: Math.Clamp(baseline + contribution.Primary, 0, 1),
                        Sidearm: Math.Clamp(baseline + contribution.Sidearm, 0, 1));
                    critical = Critical(message.EntityId, chance.Primary, chance.Sidearm);
                    probabilities = chance;
                }
                // Every client rolls each victim's reduction from that victim's own table (kr-812 sub_51D000 runs for
                // relayed hits too, and kr-812 sub_5063A0 installs a table for any seat), so each seat receives every
                // occupant's table; the bytes depend only on the round and the occupant, never on the recipient.
                var defence = defences is null ? [Defence(message.EntityId)]
                    : DefenceSeedRoster.Occupants(message).Select(occupant => Defence(occupant.EntityId,
                        defences.Table(message.RoomId, message.Generation, occupant.EntityId, occupant.Equipment))).ToArray();
                var cancellation = _critical ? Cancellation(message.EntityId) : null;
                if (critical is not null)
                    await SendTableAsync(CriticalOpcode, critical).ConfigureAwait(false);
                foreach (var table in defence)
                    await SendTableAsync(defenceOpcode, table).ConfigureAwait(false);
                if (cancellation is not null)
                    await SendTableAsync(cancelOpcode!.Value, cancellation).ConfigureAwait(false);
                lock (_gate) issuance.LastChances = probabilities;
                issuance.Completion.TrySetResult();
            }
            catch (OperationCanceledException failure)
            {
                issuance.Completion.TrySetCanceled(failure.CancellationToken);
            }
            catch (Exception failure)
            {
                issuance.Completion.TrySetException(failure);
            }
        }
        // cn-324 sub_4A91A0 / cn-406 sub_4728A0 reset every critical cursor on installation, even for identical
        // words. Retain failures as well as success: a partial send cannot safely restart from the first table.
        await issuance.Completion.Task.WaitAsync(ct).ConfigureAwait(false);

        ValueTask SendTableAsync(ushort opcode, byte[] payload)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_issued.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, issuance))
                    throw new InvalidOperationException($"Native seed issuance was removed for relay session {session.Id}.");
            }
            return session.SendAsync(opcode, payload, ct);
        }
    }

    /// <summary>
    /// Correlates a client-reported table with this session's attempted round issuance, including reports arriving
    /// while later tables are being queued. A defence table may name any occupant the seat was sent. The association
    /// is not proof of delivery or client installation.
    /// </summary>
    internal bool Issued(long sessionId, NativeSceneSeedKind kind, int entityId, out int generation)
    {
        lock (_gate)
        {
            if (_issued.TryGetValue(sessionId, out var issued) && (issued.Request.EntityId == entityId
                || (defences is not null && kind == NativeSceneSeedKind.Defence
                    && issued.Request.Roster?.Occupants.Any(occupant => occupant.EntityId == entityId) == true)))
            {
                generation = issued.Request.Generation;
                return true;
            }
            generation = 0;
            return false;
        }
    }

    /// <summary>Forgets a closed relay session.</summary>
    internal void Remove(long sessionId)
    {
        lock (_gate) _issued.Remove(sessionId);
    }

    /// <summary>Encodes a critical table with each of the first two weapon-slot tables drawn at <paramref name="chance"/>.</summary>
    internal byte[] Critical(int entityId, double chance) =>
        Critical(entityId, chance, chance);

    /// <summary>Encodes independent primary and sidearm probabilities without changing native vector sizes.</summary>
    internal byte[] Critical(int entityId, double primaryChance, double sidearmChance) =>
        SceneSeedSender.Encode(tables, build, NativeSceneSeedTable.WithWordVectors(NativeSceneSeedKind.Critical, entityId,
            [Draw(primaryChance), Draw(sidearmChance)]), "RelayCriticalSeedTable");

    // Without a defence catalog every byte is zero: the table branch then reduces no damage.
    internal byte[] Defence(int entityId, byte[]? values = null) =>
        SceneSeedSender.Encode(tables, build, NativeSceneSeedTable.Bytes(NativeSceneSeedKind.Defence, entityId,
            values ?? new byte[BitsPerTable]), "RelayDefenceSeedTable");

    // A player's cancellation chance is a character attribute (cn-324 sub_4A9A00's non-player branch reads it from
    // the attribute block); the server keeps no such attribute, so no hit is cancelled.
    internal byte[] Cancellation(int entityId) =>
        SceneSeedSender.Encode(tables, build, NativeSceneSeedTable.Words(NativeSceneSeedKind.CriticalCancellation, entityId,
            new uint[WordsPerTable]), "RelayCriticalSeedCancelTable");

    private uint[] Draw(double chance)
    {
        var words = new uint[WordsPerTable];
        lock (_gate)
        {
            for (var bit = 0; bit < BitsPerTable; bit++)
                if (random.NextDouble() < chance)
                    words[bit >> 5] |= 1u << (bit & 31);
        }
        return words;
    }
}
