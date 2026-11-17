using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// The clan requests after login, on the mounted rows: what a logged-in clan connection is answered for its
/// home, its profile and a create request, frame for frame.
/// </summary>
public sealed partial class ClanMountTests
{
    private const string NoDisband = "00000000B0D5F140";

    /// <summary>A player without a clan is answered one home frame that asks for nothing further.</summary>
    [Fact]
    public async Task HomeOfAPlayerWithoutAClanIsTheEmptyHome()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHomeRequest"), []);
        Assert.Equal([(rig.ClanTable.S2C("ClanHome"), "00" + "00000000" + "00000000" + "0000" + NoDisband)], clan.Pins());
    }

    /// <summary>
    /// Founding a clan answers the result with the client's own item word echoed (kr-1256 sub_517FE0 case 1 uses
    /// it as an item id), and the home the client then asks for states the new clan with the master role and the
    /// profile flag set.
    /// </summary>
    [Fact]
    public async Task CreateAnswersTheResultAndTheNextHomeStatesTheMaster()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", 100819, 30));
        Assert.Equal([(rig.ClanTable.S2C("ClanCreateResult"), "01000000" + "D3890100" + "1E000000")], clan.Pins());

        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHomeRequest"), []);
        // Stored clan ids start at 1000 (0x3E8); the master role is 101 (0x65).
        Assert.Equal([(rig.ClanTable.S2C("ClanHome"), "01" + "E8030000" + "65000000" + Wc("Wolves") + NoDisband)], clan.Pins());
    }

    /// <summary>The profile of the founded clan: its name, the founder as master, no deputy, level 1, one member.</summary>
    [Fact]
    public async Task ProfileStatesTheFoundedClan()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", 100819, 30));
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanProfileRequest"), []);

        var frame = Assert.Single(clan.Frames);
        Assert.Equal(rig.ClanTable.S2C("ClanProfile"), frame.Opcode);
        var reader = new PacketReader(frame.Payload);
        Assert.Equal("Wolves", reader.ReadWideCString());
        Assert.Equal((uint)UserNum, reader.ReadUInt32());
        Assert.Equal("clanner", reader.ReadWideCString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal("", reader.ReadWideCString());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        _ = reader.ReadBytes(8);
        Assert.Equal(NoDisband, Convert.ToHexString(reader.ReadBytes(8)));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        // The notice (+176) comes before the introduction (+200).
        Assert.Equal("", reader.ReadWideCString());
        Assert.Equal("hi", reader.ReadWideCString());
        Assert.Equal(0u, reader.ReadUInt32());
        // The 30-member card founds a clan of 30.
        Assert.Equal(30u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>A second clan by the same founder is refused with the "already in a clan" code and no item, and stores nothing.</summary>
    [Fact]
    public async Task CreateByAPlayerWithAClanIsRefused()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Wolves", "hi", 100819, 30));
        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanCreate"), CreateBody("Bears", "hi", 100819, 30));
        // -105: "already in a clan; leave it before creating one".
        Assert.Equal([(rig.ClanTable.S2C("ClanCreateResult"), "97FFFFFF" + "00000000" + "00000000")], clan.Pins());

        clan.Frames.Clear();
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHomeRequest"), []);
        Assert.Contains(Wc("Wolves"), clan.Pins()[0].Hex);
    }

    /// <summary>A player without a seat who asks for a profile is sent nothing.</summary>
    [Fact]
    public async Task ProfileWithoutASeatSendsNothing()
    {
        await using var rig = await Rig.StartAsync();
        var clan = await LoginClanAsync(rig);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanProfileRequest"), []);
        Assert.Empty(clan.Frames);
    }

    /// <summary>A clan request on a connection that has not logged in is an error, not an answer.</summary>
    [Fact]
    public async Task ClanRequestBeforeLoginFails()
    {
        await using var rig = await Rig.StartAsync();
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHomeRequest"), []));
        Assert.Empty(clan.Frames);
    }

    /// <summary>
    /// Logs a clan connection in the way the client does and returns it with no recorded frame. The player's bag
    /// holds two 30-member creation cards.
    /// </summary>
    private static async Task<RecordingSession> LoginClanAsync(Rig rig)
    {
        await rig.Capture.Inventory.ReplaceSlotsAsync(UserNum, InventoryBag.CharacterBag,
            [new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, 1, Card30, 2, 0)], CancellationToken.None);
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await rig.DispatchAsync("gcpReqClanPassport", []);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLogin"), EchoPassport(rig.Session.Frames[^1].Payload));
        Assert.Equal("01000000", clan.Pins()[^1].Hex);
        clan.Frames.Clear();
        return clan;
    }

    /// <summary>
    /// Logs another player's clan connection in through that player's own game session, as <see cref="LoginClanAsync"/>
    /// does for the default player, and returns it with no recorded frame.
    /// </summary>
    private static async Task<RecordingSession> LoginOtherClanAsync(Rig rig, long userNum, string nickname, long connectionId)
    {
        var game = new RecordingSession(connectionId: connectionId + 100, userNum: userNum, nickname: nickname);
        await rig.DispatchAsync("kr-1256/game", game, rig.OpcodeOf("gcpReqClanPassport"), []);
        var clan = new RecordingSession(loggedIn: false, connectionId: connectionId);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLogin"), EchoPassport(game.Frames[^1].Payload));
        Assert.Equal("01000000", clan.Pins()[^1].Hex);
        clan.Frames.Clear();
        return clan;
    }

    /// <summary>The create request as kr-1256 sub_5275B0 writes it.</summary>
    private static byte[] CreateBody(string name, string introduction, int third, int fourth)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideCString(name);
        writer.WriteUInt32(0);
        writer.WriteWideCString(introduction);
        writer.WriteUInt32((uint)third);
        writer.WriteUInt32((uint)fourth);
        return buffer.WrittenSpan.ToArray();
    }

    private static string Wc(string text) => Convert.ToHexString(Encoding.Unicode.GetBytes(text)) + "0000";
}
