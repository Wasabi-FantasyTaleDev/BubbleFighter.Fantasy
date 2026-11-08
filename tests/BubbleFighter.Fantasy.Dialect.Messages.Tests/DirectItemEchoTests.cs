using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins that no legacy build echoes a direct item use back to its sender.</summary>
public sealed class DirectItemEchoTests
{
    /// <summary>
    /// cn-324 sub_47BCA0 -> sub_465EA0 (kr-812 sub_525670 -> sub_463080) queues the sender's own copy in ordinary
    /// rounds, so an echo applies the item twice and moves the host's object numbering away from every member's.
    /// </summary>
    [Theory]
    [InlineData("kr-812"), InlineData("cn-324"), InlineData("cn-717"), InlineData("id-1809"), InlineData("id-400"),
     InlineData("cn-404"), InlineData("cn-406"), InlineData("cn-408"), InlineData("cn-705"), InlineData("cn-706"),
     InlineData("cn-279")]
    public void LegacyBuildsNeverEchoDirectItems(string build) =>
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "direct_item_effect_echo"));
}
