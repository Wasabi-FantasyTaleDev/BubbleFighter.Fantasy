using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the kr-1256 Mysterystore binding's typed reads and its reply bytes.</summary>
public sealed class MysteryStoreBindingTests
{
    private const string Build = "kr-1256";

    // Read and Write never touch the opcode table; only Bind resolves the packet names.
    private static MysteryStoreBinding Binding() => new("kr-1256/game", null!, TestBuilds.Packets, Build);

    /// <summary>Each request type reads its own tail into the typed request (kr-1256 sub_114E2E0 writes 5, 7, 7, 11 bytes).</summary>
    [Theory]
    [InlineData("0144332211", MysteryStoreAction.Open, 0, 0, 0)]
    [InlineData("02443322116054", MysteryStoreAction.Query, 21600, 0, 0)]
    [InlineData("03443322110A00", MysteryStoreAction.Refresh, 0, 10, 0)]
    [InlineData("0444332211568A1E006400", MysteryStoreAction.Purchase, 0, 100, 2001494)]
    public void RequestsReadAsTypedMessages(string hex, MysteryStoreAction action, int seconds, int coins, int key)
    {
        var request = Binding().Read(Convert.FromHexString(hex));
        Assert.Equal(new MysteryStoreRequested(action, 0x11223344, (ushort)seconds, (ushort)coins, key), request);
    }

    /// <summary>An unknown type, a truncated body, a trailing byte and a purchase of key zero are all refused.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0544332211")]
    [InlineData("01443322")]
    [InlineData("014433221100")]
    [InlineData("0444332211000000006400")]
    public void MalformedRequestsAreRefused(string hex) =>
        Assert.Throws<PacketFormatException>(() => Binding().Read(Convert.FromHexString(hex)));

    /// <summary>An unchanged query is answered with the three-byte prefix alone.</summary>
    [Fact]
    public void QueryWithoutSectionsIsThePrefix() =>
        Assert.Equal(Convert.FromHexString("020000"),
            Binding().Write(new(MysteryStoreAction.Query, MysteryStoreStatus.Done, null, null, null, null, 0)));

    /// <summary>
    /// A successful purchase carries all four sections in bit order and then the receipt word; the signed status is
    /// the build's own value (insufficient coins is -78, 0xB2).
    /// </summary>
    [Fact]
    public void PurchaseRepliesPinEveryByte()
    {
        var config = new MysteryStoreReplyConfig(0x11223344, 100385, 10, false);
        MysteryStoreReplyOffer[] offers = [new(2001494, 100, 0, "")];
        var done = Binding().Write(new(MysteryStoreAction.Purchase, MysteryStoreStatus.Done, config, 21600, offers, 1, 2001494));
        Assert.Equal(Convert.FromHexString(
            "04000F" + "44332211" + "21880100" + "0A00" + "00000000" // prefix, config
            + "6054" // remaining seconds
            + "01000000" + "00000000" + "00000000" + "568A1E00" + "6400" // one card: unknown word, empty text, key, price
            + "01000000" // purchased mask
            + "568A1E00"), done); // receipt
        var refused = Binding().Write(new(MysteryStoreAction.Purchase, MysteryStoreStatus.InsufficientCoins, null, null, null, null, 0));
        Assert.Equal(Convert.FromHexString("04B200"), refused);
    }

    /// <summary>A successful receipt follows the prefix even when the optional sections are absent.</summary>
    [Fact]
    public void PurchaseReceiptIsIndependentOfSectionMask() =>
        Assert.Equal(Convert.FromHexString("040000568A1E00"),
            Binding().Write(new(MysteryStoreAction.Purchase, MysteryStoreStatus.Done, null, null, null, null, 2001494)));

    /// <summary>Reject invalid combinations before emitting a frame the client would misread.</summary>
    [Fact]
    public void InvalidReceiptAndMaskCombinationsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Binding().Write(new(MysteryStoreAction.Purchase, MysteryStoreStatus.Done, null, null, null, null, 0)));
        Assert.Throws<ArgumentException>(() => Binding().Write(new(MysteryStoreAction.Open, MysteryStoreStatus.Done, null, null, null, null, 1)));
        Assert.Throws<ArgumentException>(() => Binding().Write(new(MysteryStoreAction.Open, MysteryStoreStatus.Done, null, null, null, 1, 0)));
    }

    /// <summary>More cards than the client lays out are refused before any byte is written.</summary>
    [Fact]
    public void SevenCardsAreRefused()
    {
        var offers = Enumerable.Range(1, 7).Select(key => new MysteryStoreReplyOffer(key, 1, 0, "")).ToArray();
        Assert.Throws<ArgumentException>(() =>
            Binding().Write(new(MysteryStoreAction.Open, MysteryStoreStatus.Done, null, null, offers, null, 0)));
    }

    /// <summary>The build's statuses map every reply status, with zero for done alone.</summary>
    [Fact]
    public void StatusesComeFromTheBuild()
    {
        var statuses = MysteryStoreBinding.ReadStatuses(TestBuilds.Packets, Build);
        Assert.Equal(Enum.GetValues<MysteryStoreStatus>().Length, statuses.Count);
        Assert.Equal(0, statuses[MysteryStoreStatus.Done]);
        Assert.Equal(-78, statuses[MysteryStoreStatus.InsufficientCoins]);
        Assert.Throws<ConfigException>(() => MysteryStoreBinding.ReadStatuses(TestBuilds.Packets, "kr-812"));
    }
}
