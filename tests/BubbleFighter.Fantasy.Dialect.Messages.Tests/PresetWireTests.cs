using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-812 weapon-preset wire family: C2S 830 (<c>kr-812 sub_97A8D0</c> writes
/// <c>i32 context + u8 slot</c>), S2C 832 (<c>kr-812 sub_83B880</c> reads <c>u8 slot + i32 key</c>),
/// and the S2C 833 preset bar (<c>kr-812 sub_83B820</c> + <c>kr-812 sub_812190</c>). Bodies are pinned
/// byte for byte.
/// </summary>
public sealed class PresetWireTests
{
    [Fact]
    public void Kr812ChangeRequestParsesTheCapturedMenuAndMatchContexts()
    {
        // FFFFFFFF00/01 in the menu, 40420F00xx in-match.
        Assert.Equal(new WeaponPresetChangeRequested(-1, 0), PresetWire.ReadChangeRequest(TestBuilds.Packets, "kr-812", Convert.FromHexString("FFFFFFFF00")));
        Assert.Equal(new WeaponPresetChangeRequested(-1, 1), PresetWire.ReadChangeRequest(TestBuilds.Packets, "kr-812", Convert.FromHexString("FFFFFFFF01")));
        Assert.Equal(new WeaponPresetChangeRequested(1000000, 1), PresetWire.ReadChangeRequest(TestBuilds.Packets, "kr-812", Convert.FromHexString("40420F0001")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Kr812ChangeRequestRejectsEveryOtherLength(int length) =>
        Assert.Throws<PacketFormatException>(() => PresetWire.ReadChangeRequest(TestBuilds.Packets, "kr-812", new byte[length]));

    [Fact]
    public void Kr812ChangeReplyIsTheFiveByteStatusTheClientUnlatchesOn()
    {
        // The granted slot byte and success status zero.
        Assert.Equal(Convert.FromHexString("0100000000"), PresetWire.WriteChangeReply(TestBuilds.Packets, "kr-812", 1, 0));
        Assert.Equal(Convert.FromHexString("0000000000"), PresetWire.WriteChangeReply(TestBuilds.Packets, "kr-812", 0, 0));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void RefusalStatusIsPreservedRatherThanEncodedAsSuccess(string build)
    {
        Assert.Equal(Convert.FromHexString("0201000000"),
            PresetWire.WriteChangeReply(TestBuilds.Packets, build, 2, 1));
        Assert.Equal(Convert.FromHexString("02FFFFFFFF"),
            PresetWire.WriteChangeReply(TestBuilds.Packets, build, 2, -1));
    }

    [Fact]
    public void Kr812UpdateMatchesTheRecordedFortySevenByteBar()
    {
        // Active slot 1, account 282. The
        // reference bar carries account-bound flags in vector one; this emulator's slots
        // derive from the worn set, so the pin uses two equal bound ids.
        var body = PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 1, 282, [202000, 202000, 0, 0, 0]);
        Assert.Equal(47, body.Length);
        Assert.Equal(
            Convert.FromHexString(
                "01" + "1A010000" + "05000000" +
                "10150300" + "10150300" + "00000000" + "00000000" + "00000000" +
                "05000000" + "0101000000" +
                "05000000" + "0000000000"),
            body);
    }

    [Fact]
    public void Kr812UpdateCarriesFiveSlotsOrThrows()
    {
        Assert.Throws<ArgumentException>(() => PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 0, 1, [202001, 201001, 205001, 207001]));
        Assert.Throws<ArgumentException>(() => PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 0, 1, [202001, 201001, 205001, 207001, 0, 0]));
        Assert.Throws<ArgumentNullException>(() => PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 0, 1, null!));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void EveryAuditedPresetBuildPreservesTheRecordedBar(string build)
    {
        var expected = Convert.FromHexString("011A010000050000001015030010150300000000000000000000000000050000000101000000050000000000000000");
        Assert.Equal(expected, PresetWire.WriteUpdate(TestBuilds.Packets, build, 1, 282, [202000, 202000, 0, 0, 0]));
        Assert.Equal(Convert.FromHexString("0100000000"), PresetWire.WriteChangeReply(TestBuilds.Packets, build, 1, 0));
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-735")]
    public void ABuildWithoutAnAuditedPresetPathCannotSendAnEmptyPlaceholder(string build)
    {
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() =>
            PresetWire.WriteUpdate(TestBuilds.Packets, build, 0, 1, [202001, 0, 0, 0, 0]));
    }

    [Fact]
    public void PresetSlotsDeriveTheBarFromTheWornFamilies()
    {
        // Starter worn set {300001, 202001, 201001, 205001, 207001}: primary first, then
        // sidearm, special and melee; the fifth hotkey stays unbound.
        Assert.Equal([202001, 201001, 205001, 207001, 0],
            WeaponPresetSlots.FromWorn([300001, 202001, 201001, 205001, 207001]));
        Assert.Equal([0, 0, 0, 0, 0], WeaponPresetSlots.FromWorn([300001]));
        Assert.Equal([203003, 201003, 0, 207001, 0],
            WeaponPresetSlots.FromWorn([300001, 201003, 203003, 207001]));
    }
}
