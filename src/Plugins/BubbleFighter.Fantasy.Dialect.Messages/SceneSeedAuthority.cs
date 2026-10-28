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
    /// states a non-zero defence chance, whose byte scale on the wire is unproven.</exception>
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
            // cn-324 sub_4A9540 scales the defence byte by a constant whose meaning is not established, so only
            // the shipped all-zero column is accepted.
            if (Number(row[defence], name, "crtDefenceFactor") != 0)
                throw new ConfigException($"'{path}' category '{name}' states a defence chance; its seed byte scale is unproven.");
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
/// Issues every seat's native seed tables for a starting round. The client never generates these bits; its only
/// writers are the relay appliers (cn-324 sub_460820 -> sub_4A91A0, kr-812 sub_506290 -> sub_556FB0).
/// </summary>
internal sealed class SceneSeedAuthority(string protocol, string build, PacketTables tables, ushort criticalOpcode,
    ushort defenceOpcode, ushort cancelOpcode, CriticalHitChances chances, int maxSessions, Random random)
    : IMessageSender<MatchSeedTablesRequested>
{
    // The expander copies 32 words into each of the first two weapon-slot tables and leaves the other three zero
    // (cn-324 sub_4998C0 stops at 32 words; kr-812 sub_54A660 is its counterpart). The roll reads the table of the
    // current weapon slot, Character+6452 (cn-324 sub_4A94C0; the slot setter is cn-324 sub_4CD3C0).
    internal const int WordsPerTable = 32;

    // Each table holds 1000 one-bit rolls; the cursor wraps at 1000 (cn-324 sub_4A94C0), so the last 24 bits of the
    // 32 words are never read.
    internal const int BitsPerTable = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, (int Generation, int EntityId)> _issued = [];

    /// <inheritdoc />
    public string Protocol => protocol;

    /// <summary>Sends the critical, defence and cancellation tables (relay S2C 4, 5 and 6) for one seat.</summary>
    public async ValueTask SendAsync(ISession session, MatchSeedTablesRequested message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Native seed tables belong to another client profile.");
        lock (_gate)
        {
            if (!_issued.ContainsKey(session.Id) && _issued.Count >= maxSessions)
                throw new InvalidOperationException($"Native seed authority reached its {maxSessions} session limit.");
            _issued[session.Id] = (message.Generation, message.EntityId);
        }
        var chance = chances.RoundStartChance(message.PreviousRound);
        await session.SendAsync(criticalOpcode, Critical(message.EntityId, chance), ct).ConfigureAwait(false);
        await session.SendAsync(defenceOpcode, Defence(message.EntityId), ct).ConfigureAwait(false);
        await session.SendAsync(cancelOpcode, Cancellation(message.EntityId), ct).ConfigureAwait(false);
    }

    /// <summary>Returns whether a client-reported table names the entity issued to that relay session this round.</summary>
    internal bool Issued(long sessionId, int entityId, out int generation)
    {
        lock (_gate)
        {
            var found = _issued.TryGetValue(sessionId, out var issued) && issued.EntityId == entityId;
            generation = found ? issued.Generation : 0;
            return found;
        }
    }

    /// <summary>Forgets a closed relay session.</summary>
    internal void Remove(long sessionId)
    {
        lock (_gate) _issued.Remove(sessionId);
    }

    /// <summary>Encodes a critical table with each of the first two weapon-slot tables drawn at <paramref name="chance"/>.</summary>
    internal byte[] Critical(int entityId, double chance) =>
        SceneSeedSender.Encode(tables, build, NativeSceneSeedTable.WithWordVectors(NativeSceneSeedKind.Critical, entityId,
            [Draw(chance), Draw(chance)]), "RelayCriticalSeedTable");

    // The shipped defence column is zero for every category (checked on load), so every defence byte is zero.
    internal byte[] Defence(int entityId) =>
        SceneSeedSender.Encode(tables, build, NativeSceneSeedTable.Bytes(NativeSceneSeedKind.Defence, entityId,
            new byte[BitsPerTable]), "RelayDefenceSeedTable");

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
