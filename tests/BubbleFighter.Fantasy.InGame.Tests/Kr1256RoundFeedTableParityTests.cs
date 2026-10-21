using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the 38 round-feed structures of <c>packets/round-feed.yaml</c> against the code that owns their
/// lengths: <see cref="MatchAction1256.Read"/> accepts one exact body length per action and reports the actor
/// words the relay resolves, so a table whose fields add up to another length is a table of another packet.
/// Every golden body is read and written back through <see cref="PacketCodec"/> unchanged, and both readers
/// refuse one byte short. The golden bytes give field i a distinct value, so a swapped field or a wrong
/// width fails on the bytes and not only on the total.
/// </summary>
/// <remarks>
/// 32926 is deliberately absent: it carries two tagged optional words (tag 0x3A carries a word, 0xBA does
/// not) and the tag reader was not read in this pass, so the frame stays untabled until it is.
/// The eight damager-spawn frames are pinned by <see cref="DamagerSpawnTableTests"/> and are not repeated here.
/// </remarks>
public sealed class Kr1256RoundFeedTableParityTests
{
    private const string Build = "kr-1256";
    private const string Baseline = "kr-812";

    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    /// <summary>
    /// Structure, the opcode the round flow dispatches it under, the golden body and the actor offsets
    /// <see cref="MatchAction1256"/> reports for that body (the actor word and the alternate participant
    /// word, -1 when the frame has none). The golden body writes field i with the value the pattern in
    /// <see cref="Pattern"/> produces: 0x01020304 + i for a word, 0xA0 + i for a byte, 0xB000 + i for a
    /// half-word, 1f + i for a float and 1 for a bool.
    /// </summary>
    public static TheoryData<string, int, string, int, int> Frames => new()
    {
        { "Dash", 32779, "0403020101000000", 0, -1 },
        { "Roll", 32780, "0403020105030201A2A3", 0, -1 },
        { "Jump", 32781, "040302010503020106030201", 0, -1 },
        { "Aim", 32782, "04030201", 0, -1 },
        { "SetTargettingObj", 32783, "0403020105030201", 4, -1 },
        { "BubbleJump", 32784, "04030201050302010100000007030201", 0, -1 },
        { "WallAction", 32786, "0403020105030201A2", 0, -1 },
        { "Stunned", 32788, "040302010503020100004040000080400000A04009030201", 0, -1 },
        { "EndStunned", 32790, "04030201", 0, -1 },
        { "KickStart", 32791, "040302010503020106030201", 0, -1 },
        { "KickFail", 32792, "04030201", 0, -1 },
        { "Reload", 32811, "0403020105030201", 0, -1 },
        { "ReturnToNormal", 32813, "04030201", 0, -1 },
        { "HumiliationStart", 32815, "0403020105030201", 0, -1 },
        { "HumiliationEnd", 32816, "0403020105030201", 0, -1 },
        { "WallState", 32834, "0403020105030201", 0, -1 },
        { "GotKicked", 32835, "04030201", 0, -1 },
        { "Dead", 32836, "04030201", 0, -1 },
        { "KickEnd", 32837, "04030201", 0, -1 },
        { "WatchingOnMe", 32884, "04030201050302010100000007030201", 0, -1 },
        { "Unknown32810", 32810, "040302010503020106030201070302010100000001000000", 8, -1 },
        { "Unknown32882", 32882, "040302010100000001000000", 0, -1 },
        { "Unknown32907", 32907, "04030201", -1, -1 },
        { "Unknown32921", 32921, "040302010503020106030201", 0, -1 },
        { "Unknown32925", 32925, "04030201A102B003B004B005B006B007B00C030201A90E0302010F0302010CB0", 0, -1 },
        { "Unknown32821", 32821, "04030201A1A2", 0, -1 },
        { "Unknown33286", 33286, "040302010503020106030201", 0, -1 },
        { "Unknown33301", 33301, "040302010000004000004040000080400803020109030201", 16, -1 },
        { "Unknown38271", 38271, "0403020105030201", 4, 0 },
        { "Unknown38272", 38272, "0403020105030201", 0, -1 },
        { "Unknown38273", 38273, "040302010503020100004040A3", 0, 4 },
        { "Unknown38274", 38274, "04030201", 0, -1 },
        { "Unknown38277", 38277, "010000000503020106030201000080400000A0400000C040010000000B030201", 8, 4 },
        { "Unknown38280", 38280, "0403020105030201A2", 4, 0 },
        { "Unknown38281", 38281, "04030201", 0, -1 },
        { "Unknown38282", 38282, "040302010503020100004040000080400000A0400903020101000000010000000C03020101000000", 0, -1 },
        { "Unknown38283", 38283, "040302010503020102B003B004B0A501000000", -1, -1 },
        { "Unknown38284", 38284, "0403020101000000", 0, -1 },
    };

    // Field i carries a value derived from its index, so a shifted or swapped field shows up as a wrong value.
    private static Dictionary<string, object?> Pattern(IReadOnlyList<PacketField> fields)
    {
        var values = new Dictionary<string, object?>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            values[field.Name] = field.Type switch
            {
                "u8" => (byte)(0xA0 + i),
                "u16" => (ushort)(0xB000 + i),
                "u32" => 0x01020304u + (uint)i,
                "u64" => 0x0102030405060708UL + (ulong)i,
                "bool" => true,
                "f32" => 1f + i,
                var other => throw new InvalidOperationException($"The round-feed parity test has no value pattern for {other}."),
            };
        }
        return values;
    }

    /// <summary>The table writes the golden body, the round flow accepts exactly that length, and the body reads back unchanged.</summary>
    [Theory]
    [MemberData(nameof(Frames))]
    public void TheTableBodyIsTheBodyTheRoundFlowAccepts(string structure, int opcode, string goldenHex, int actorOffset,
        int alternateActorOffset)
    {
        var body = Convert.FromHexString(goldenHex);
        var tables = Tables.Value;
        Assert.Equal(goldenHex, Convert.ToHexString(PacketCodec.Write(tables, Build, structure, Pattern(tables.Resolve(Build, structure)))));

        // The round flow's own length and actor pins agree with the table's body.
        var action = MatchAction1256.Read((ushort)opcode, body);
        Assert.Equal(body.Length, action.BodyBytes);
        Assert.Equal(actorOffset, action.ActorOffset);
        Assert.Equal(alternateActorOffset, action.AlternateActorOffset);

        // Reading the golden body back and writing the read values reproduces it byte for byte.
        Assert.Equal(body, PacketCodec.Write(tables, Build, structure, PacketCodec.Read(tables, Build, structure, body)));

        // One byte short and one byte too long are refused by both paths.
        Assert.Throws<PacketFormatException>(() => MatchAction1256.Read((ushort)opcode, body[..^1]));
        Assert.Throws<PacketFormatException>(() => MatchAction1256.Read((ushort)opcode, [.. body, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, Build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, Build, structure, [.. body, 0]));
    }

    /// <summary>The 20 frames that carry a kr-812 class name inherit exactly the kr-812 rows, and the 18 Unknown frames state no kr-812 layout.</summary>
    [Fact]
    public void TheNamedFramesComeFromTheBaselineAndTheUnknownFramesDoNot()
    {
        string[] named =
        [
            "Dash", "Roll", "Jump", "Aim", "SetTargettingObj", "BubbleJump", "WallAction", "Stunned", "EndStunned",
            "KickStart", "KickFail", "Reload", "ReturnToNormal", "HumiliationStart", "HumiliationEnd", "WallState",
            "GotKicked", "Dead", "KickEnd", "WatchingOnMe",
        ];
        foreach (var structure in named)
        {
            var fields = Tables.Value.Resolve(Build, structure);
            Assert.NotEmpty(fields);
            Assert.All(fields, field => Assert.Equal(Baseline, field.Origin));
        }

        string[] unknown =
        [
            "Unknown32810", "Unknown32882", "Unknown32907", "Unknown32921", "Unknown32925", "Unknown32821",
            "Unknown33286", "Unknown33301", "Unknown38271", "Unknown38272", "Unknown38273", "Unknown38274",
            "Unknown38277", "Unknown38280", "Unknown38281", "Unknown38282", "Unknown38283", "Unknown38284",
        ];
        foreach (var structure in unknown)
        {
            Assert.NotEmpty(Tables.Value.Resolve(Build, structure));
            Assert.Empty(Tables.Value.Resolve(Baseline, structure));
        }
    }
}
