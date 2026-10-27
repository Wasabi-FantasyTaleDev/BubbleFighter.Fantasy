using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the cash gift-box requests from <c>packets/cash-inbox.yaml</c>: the legacy open request
/// <c>gcpReqCashInven</c> and the close request <c>gcpReqCloseGiftBox</c> are empty bodies in every
/// build whose client reader or sender was read, and the cash-box names resolve to those numbers.
/// </summary>
public sealed class CashInboxTableTests
{
    private static readonly Dictionary<string, object?> NoValues = new();

    /// <summary>The legacy cash-box open request is an empty body: zero fields, zero bytes.</summary>
    /// <param name="build">The build whose table names the request.</param>
    [Theory]
    [InlineData("kr-812")]
    public void LegacyOpenRequestIsEmpty(string build)
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
    public void CloseRequestIsEmpty(string build)
    {
        Assert.Empty(TestBuilds.Packets.Resolve(build, "PresentCloseRequest"));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "PresentCloseRequest", NoValues));
    }

    /// <summary>
    /// The cash-box names resolve to the numbers the client readers and senders use: kr-812 C2S
    /// 826/827/828 and S2C 807/831; kr-1256 C2S 829/830 and S2C 810/835. The modern table has no
    /// established open-request name.
    /// </summary>
    [Fact]
    public void CashBoxNamesResolveToTheClientNumbers()
    {
        var kr812 = TestBuilds.Opcodes("kr-812");
        Assert.Equal((ushort)826, kr812.C2S("gcpReqCashInven"));
        Assert.Equal((ushort)827, kr812.C2S("gcpReqPickup"));
        Assert.Equal((ushort)828, kr812.C2S("gcpReqCloseGiftBox"));
        Assert.Equal((ushort)807, kr812.S2C("gspCashInvenUpdateAllInventorySlot"));
        Assert.Equal((ushort)831, kr812.S2C("gspRepPickup"));

        var kr1256 = TestBuilds.Opcodes("kr-1256");
        Assert.Equal((ushort)829, kr1256.C2S("gcpReqPickup"));
        Assert.Equal((ushort)830, kr1256.C2S("gcpReqCloseGiftBox"));
        Assert.Equal((ushort)810, kr1256.S2C("gspCashInvenUpdateAllInventorySlot"));
        Assert.Equal((ushort)835, kr1256.S2C("gspRepPickup"));
        Assert.Null(kr1256.Find(PacketDirection.ClientToServer, "gcpReqCashInven"));
    }
}
