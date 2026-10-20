using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the consumable revival body and distinguishes player references from owned NPC references.</summary>
public sealed class RevivalTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void AuditedRevivalBuildsReadTheSameNativeActorReference(string build)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "instant_revival"));
        var body = BitConverter.GetBytes(1_200_000);
        var request = RevivalBinding.ReadInstant(TestBuilds.Packets, build, body);
        Assert.Equal(1_200_000, request.Actor);
        Assert.Equal(2, request.ActorSeat);
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.Packets, build, body[..3]));
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.Packets, build, new byte[8]));
    }

    /// <summary>C714 contains exactly one player reference and rejects truncated or extended bodies.</summary>
    [Fact]
    public void ConsumableRevivalIsFourBytesAndPreservesOwner()
    {
        var body = BitConverter.GetBytes(1_100_000);
        var request = RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", body);
        Assert.Equal(1_100_000, request.Actor);
        Assert.Equal(1, request.ActorSeat);
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", body[..3]));
        Assert.Throws<PacketFormatException>(() => RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", new byte[8]));
        Assert.Equal(-1, RevivalBinding.ReadInstant(TestBuilds.All.Tables, "cn-324", BitConverter.GetBytes(1_100_001)).ActorSeat);
    }
}
