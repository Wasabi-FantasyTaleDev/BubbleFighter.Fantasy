using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class FighterInfoBindingTests
{
    // Reply-body byte offsets with the two-character nickname Ab; memory offsets are different.
    public static readonly TheoryData<string, int> PrimaryBattleOffsets = new()
    {
        { "kr-1256", 29 }, { "kr-812", 60 }, { "id-400", 60 }, { "id-1809", 60 },
        { "cn-324", 56 }, { "cn-717", 60 }, { "cn-774", 52 }, { "tw-454", 52 },
        { "cn-279", 28 }, { "cn-406", 60 }, { "cn-706", 60 }, { "cn-404", 60 }, { "cn-408", 60 },
    };

    [Theory]
    [MemberData(nameof(PrimaryBattleOffsets))]
    public void PrimaryPageProjectsOnlyRecordedNoItemAndItemCounters(string build, int winsAt)
    {
        var binding = Binding(build, out _);
        var snapshot = new FighterInfoReplied(new PlayerIdentity(77, "target", "Ab", 9), new(300007, [300007]));
        var baseline = binding.Write(snapshot);
        var history = new PlayerBattleRecord[]
        {
            new(1, Wins: 2, Losses: 3, Draws: 4, Escapes: 5, Knockdowns: 7, Kills: 11, Rescues: 13, Score: long.MaxValue),
            new(3, Wins: 11, Losses: 13, Draws: 17, Escapes: 19, Knockdowns: 23, Kills: 29, Rescues: 31),
            new(5, Wins: long.MaxValue, Knockdowns: long.MaxValue),
        };
        var body = binding.Write(snapshot with { BattleRecords = history });
        // Native W/L/D/Bubble/Pang/Help positions and the separate escape word; every other byte stays default.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(24), 24);
        var values = new[] { 13, 16, 21, 30, 40, 44 };
        for (var index = 0; index < values.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(winsAt + index * 4), values[index]);
        Assert.Equal(baseline, body);
        Assert.Equal(long.MaxValue, history[0].Score);
        Assert.Equal(2, history[0].Wins);
    }

    [Theory]
    [MemberData(nameof(PrimaryBattleOffsets))]
    public async Task UnknownOrUnsafeHistoryProducesOnlyTheNativeFailure(string build, int winsAt)
    {
        _ = winsAt;
        var binding = Binding(build, out var opcodes);
        IReadOnlyList<PlayerBattleRecord>[] invalid =
        [
            [], [new(5, Wins: 1)], [new(1, Wins: -1)], [new(1, Wins: 214749)],
            [new(1, Wins: 1, Losses: int.MaxValue)], [new(1, Escapes: (long)int.MaxValue + 1)],
            [new(1, Kills: int.MaxValue)], [new(1, Knockdowns: int.MaxValue, Rescues: 1)],
            [new(1, Wins: 1), new(1, Wins: 1)], [new(1, Losses: long.MaxValue), new(3, Losses: 1)],
        ];
        foreach (var history in invalid)
        {
            var session = new Session();
            await binding.SendAsync(session, new(new(77, "target", "Ab", 9), new(300007, [300007]), history), default);
            var frame = Assert.Single(session.Sent);
            Assert.Equal(opcodes.S2C("gspRepFighterInfo"), frame.Opcode);
            Assert.Equal(binding.Write(new(null)), frame.Body);
        }
    }

    [Theory]
    [MemberData(nameof(PrimaryBattleOffsets))]
    public void LargestSafeNativeArithmeticKeepsExactCounts(string build, int winsAt)
    {
        var binding = Binding(build, out _);
        var record = new PlayerBattleRecord(1, Wins: int.MaxValue / 10000,
            Losses: int.MaxValue - int.MaxValue / 10000, Escapes: int.MaxValue, Knockdowns: int.MaxValue);
        var body = binding.Write(new(new(77, "target", "Ab", 9), new(300007, [300007]), [record]));
        Assert.Equal(1, Int(body, 0));
        Assert.Equal(record.Wins, Int(body, winsAt));
        Assert.Equal(record.Losses, Int(body, winsAt + 4));
        Assert.Equal(record.Knockdowns, Int(body, winsAt + 12));
        Assert.Equal(record.Escapes, Int(body, 24));
    }
}
