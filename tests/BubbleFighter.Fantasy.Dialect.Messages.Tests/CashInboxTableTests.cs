using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the cash gift-box requests from <c>packets/cash-inbox.yaml</c>, opcode assignments across the
/// cash-inbox-enabled builds, and the <c>cash_inbox</c> capability flags in <c>packets/builds.yaml</c>.
/// </summary>
public sealed class CashInboxTableTests
{
    private static readonly Dictionary<string, object?> NoValues = new();

    /// <summary>The open request <c>gcpReqCashInven</c> is an empty body in every enabled family.</summary>
    /// <param name="build">The build whose table names the request.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    [InlineData("cn-406")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void OpenRequestIsEmpty(string build)
    {
        Assert.Empty(TestBuilds.Packets.Resolve(build, "CashInboxRequest"));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "CashInboxRequest", NoValues));
    }

    /// <summary>The close request is an empty body in both families.</summary>
    /// <param name="build">The build whose table names the request.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-324")]
    [InlineData("tw-410")]
    [InlineData("cn-406")]
    public void CloseRequestIsEmpty(string build)
    {
        Assert.Empty(TestBuilds.Packets.Resolve(build, "PresentCloseRequest"));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "PresentCloseRequest", NoValues));
    }

    /// <summary>
    /// Every enabled build resolves the five cash-inbox game-connection opcodes to its audited numbers.
    /// </summary>
    [Theory]
    [InlineData("kr-812", 826, 827, 828, 807, 831)]
    [InlineData("id-400", 826, 827, 828, 807, 831)]
    [InlineData("id-1809", 826, 827, 828, 807, 831)]
    [InlineData("cn-406", 826, 827, 828, 807, 831)]
    [InlineData("cn-404", 826, 827, 828, 807, 831)]
    [InlineData("cn-408", 826, 827, 828, 807, 831)]
    [InlineData("cn-706", 826, 827, 828, 807, 831)]
    [InlineData("cn-717", 826, 827, 828, 807, 831)]
    [InlineData("cn-324", 827, 828, 829, 807, 832)]
    [InlineData("tw-410", 827, 828, 829, 807, 832)]
    [InlineData("kr-1256", 828, 829, 830, 810, 835)]
    [InlineData("cn-774", 828, 829, 830, 810, 835)]
    [InlineData("tw-454", 828, 829, 830, 810, 835)]
    public void CashBoxNamesResolveToTheClientNumbers(string build, ushort open, ushort pickup, ushort close,
        ushort list, ushort pickupReply)
    {
        var game = TestBuilds.Opcodes(build).ForConnection(OpcodeConnection.Game);
        Assert.Equal(open, game.C2S("gcpReqCashInven"));
        Assert.Equal(pickup, game.C2S("gcpReqPickup"));
        Assert.Equal(close, game.C2S("gcpReqCloseGiftBox"));
        Assert.Equal(list, game.S2C("gspCashInvenUpdateAllInventorySlot"));
        Assert.Equal(pickupReply, game.S2C("gspRepPickup"));
    }

    /// <summary>Checks that the <c>cash_inbox</c> capability matches audited client coverage.</summary>
    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", true)]
    [InlineData("cn-324", true)]
    [InlineData("tw-410", true)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", true)]
    [InlineData("cn-705", true)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("kr-186", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-735", false)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    public void CapabilityMatchesAuditedBuilds(string build, bool expected) =>
        Assert.Equal(expected, TestBuilds.Packets.CapabilityOf(build, "cash_inbox"));
}
