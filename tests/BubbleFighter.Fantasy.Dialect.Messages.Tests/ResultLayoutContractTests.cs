using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Rejects retyped result fields before publishing an outbound sender.</summary>
public sealed class ResultLayoutContractTests
{
    /// <summary>Fixed-row senders require their native outcome words and saturated byte totals at construction.</summary>
    [Theory]
    [InlineData("RoundEndGame", "unknown_8", "u32")]
    [InlineData("RoundTimeOverTotals", "red_total", "u8")]
    public void FixedSenderRejectsRetypedScalar(string structure, string field, string type)
    {
        var edited = PacketTableEdits.WithFieldType(structure, field, type, "wstr");
        var packets = PacketTables.Load(edited.FullName);
        var error = Assert.Throws<ConfigException>(() => new RoundResultSender("kr-812/game", packets,
            "kr-812", _ => TestBuilds.Opcodes("kr-812"), TestBuilds.Of("kr-812").Play.Score));
        Assert.Contains(structure + "." + field, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Masked-row senders require native result-open and reward-stage fields before any send.</summary>
    [Theory]
    [InlineData("RoundResultOpen", "unknown_n1", "u32")]
    [InlineData("RoundResultClose", "unknown_n0", "u8")]
    public void MaskedSenderRejectsRetypedScalar(string structure, string field, string type)
    {
        var edited = PacketTableEdits.WithFieldType(structure, field, type, "wstr");
        var packets = PacketTables.Load(edited.FullName);
        var error = Assert.Throws<ConfigException>(() => new RoundResultSender("kr-1256/game", packets,
            "kr-1256", _ => TestBuilds.Opcodes("kr-1256"), [1],
            new RoundResultWire.MaskedRows("unknown_0", new Dictionary<string, object?>()), null));
        Assert.Contains(structure + "." + field, error.Message, StringComparison.Ordinal);
    }
}
