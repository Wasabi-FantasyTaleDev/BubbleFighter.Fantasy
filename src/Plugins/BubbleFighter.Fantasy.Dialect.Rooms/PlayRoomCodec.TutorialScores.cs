using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class PlayRoomCodec
{
    private bool TutorialScoresSupported()
    {
        if (!packetTables.ValuesOf(lineage).TryGetValue("tutorial_score_builds", out var raw))
            return false;
        if (raw is not string[] builds || builds.Length == 0 || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length)
            throw new ConfigException("tutorial_score_builds must list distinct audited client builds.");
        return builds.Contains(lineage, StringComparer.Ordinal);
    }

    internal static string TutorialHelpField(PacketTables packets, string build)
    {
        if (!packets.ValuesOf(build).TryGetValue("tutorial_score_help_field", out var value)
            || value is not string field || string.IsNullOrWhiteSpace(field)
            || field is "knock_downs" or "kills")
            throw new ConfigException("tutorial_score_help_field must name a distinct native help counter.");
        PacketLayoutContract.RequireFields(packets, build, "ScoreRow", "Selected tutorial help counter", (field, "u32"));
        return field;
    }

    internal static (string Victim, string Scorer, string Auxiliary)? TutorialNpcFinishFields(PacketTables packets, string build)
    {
        var values = packets.ValuesOf(build);
        if (!values.TryGetValue("tutorial_score_builds", out var membership)
            || membership is not string[] audited || !audited.Contains(build, StringComparer.Ordinal))
            return null;
        if (!values.TryGetValue("tutorial_score_npc_finish_fields", out var value))
            return null;
        if (value is not Dictionary<string, object?> aliases || aliases.Count != 3
            || !aliases.TryGetValue("victim", out var rawVictim) || rawVictim is not string victim
            || !aliases.TryGetValue("scorer", out var rawScorer) || rawScorer is not string scorer
            || !aliases.TryGetValue("auxiliary", out var rawAuxiliary) || rawAuxiliary is not string auxiliary)
            throw new ConfigException("tutorial_score_npc_finish_fields must map victim, scorer and auxiliary to native word fields.");
        var fields = new[] { victim, scorer, auxiliary };
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(field => string.IsNullOrWhiteSpace(field)
                || field is "kind" or "unknown_8" or "unknown_12" or "unknown_24" or "unknown_32"))
            throw new ConfigException("tutorial_score_npc_finish_fields must name distinct fields outside the fixed NPC report words.");
        PacketLayoutContract.RequireFields(packets, build, "RoundScoreReport", "Selected tutorial NPC finishing report",
            ("kind", "u32"), ("unknown_8", "u32"), ("unknown_12", "u32"), (victim, "u32"),
            (scorer, "u32"), ("unknown_24", "u32"), (auxiliary, "u32"), ("unknown_32", "u32"));
        return (victim, scorer, auxiliary);
    }

    // cn-406 sub_43AEA0 reports bubble kinds 1/22; cn-406 sub_43CF70 reports finishing kind 2.
    // cn-406 sub_43AB30 reports rescue kind 4; cn-406 sub_42ADA0 writes the scorer at object +12.
    internal static TutorialScoreReported? ReadTutorialScore(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "RoundScoreReport", body);
        var kind = Convert.ToInt32(fields["kind"]);
        var goal = kind switch
        {
            1 or 22 => TutorialScoreEvent.Bubble,
            2 when packets.Resolve(build, "RoundScoreReport").Single(field => field.Name == "kind").Type == "u32"
                => TutorialScoreEvent.Pang,
            4 => TutorialScoreEvent.Help,
            _ => (TutorialScoreEvent?)null,
        };
        var slot = PlayerSeatOf(unchecked((int)fields["unknown_12"]));
        // cn-406 sub_5E3A90 and cn-324 sub_5E1F60 emit kind 2 with zero actor references;
        // their native eight-word envelopes carry a scorer seat instead.
        if (kind == 2 && TutorialNpcFinishFields(packets, build) is { } npc
            && (int)fields["unknown_8"] == 0 && (int)fields["unknown_12"] == 0
            && (int)fields[npc.Victim] == -1
            && (int)fields["unknown_24"] == 0 && (int)fields[npc.Auxiliary] == 0
            && (int)fields["unknown_32"] == 0)
        {
            var reported = (int)fields[npc.Scorer];
            if (reported >= 0) slot = reported;
        }
        return goal is { } accepted && slot >= 0 ? new(slot, accepted) : null;
    }

    // kr-812 sub_6E05D0 reports an NPC finishing blow as kind 2 and an explicit scorer seat.
    // kr-812 sub_530900 writes exactly one byte, one word and one byte.
    internal static TutorialScoreReported? ReadOptimizedTutorialScore(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(packets, build, "RoundOptimizedScoreReport", body);
        return (byte)fields["kind"] == 2
            ? new((byte)fields["reporter_slot"], TutorialScoreEvent.Pang) : null;
    }

    /// <summary>Sends a full native score restatement without ending the selected tutorial.</summary>
    public ValueTask SendAsync(ISession session, TutorialScoreUpdated message, CancellationToken ct)
        => session.SendAsync(tables.Get(session.Profile.Id).S2C("gspGameBroadcastPlayerScoreData"),
            WriteTutorialScore(packetTables, lineage, message), ct);

    internal static byte[] WriteTutorialScore(PacketTables packets, string build, TutorialScoreUpdated message)
    {
        if (message.Slot != 0 || message.Bubbles < 0 || message.Pangs < 0 || message.Helps < 0)
            throw new ArgumentOutOfRangeException(nameof(message), "Tutorial score requires nonnegative counters for solo seat zero.");
        var board = new RoundScoreboard(0, 0, [0, 0],
            [new(message.Slot, message.Nickname, 0, 1,
                new(message.Slot, message.Pangs, message.Bubbles, 0, 0), null)]);
        var values = new Dictionary<string, object?>(RoundResultWire.ScoreboardValues(packets, build, board));
        var rows = (IReadOnlyDictionary<string, object?>[])values["rows"]!;
        // cn-406 sub_605B10 / kr-812 sub_701AB0 read row +56; cn-324 sub_5FFCC0 reads row +32.
        rows[message.Slot] = new Dictionary<string, object?>(rows[message.Slot])
            { [TutorialHelpField(packets, build)] = message.Helps };
        return PacketCodec.Write(packets, build, "ScoreData", values);
    }
}
