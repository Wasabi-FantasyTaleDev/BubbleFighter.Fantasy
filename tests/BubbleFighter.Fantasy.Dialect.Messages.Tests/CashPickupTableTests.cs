using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins cash pickup bodies and their independently scoped opcode rows.</summary>
public sealed class CashPickupTableTests
{
    /// <summary>The request contains exactly one word, without accepting an appended field.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RequestHasOneWordAndRequiresTheWholeBody(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CashPickupRequest",
            new Dictionary<string, object?> { ["cash_slot_key"] = 0xD4C3B2A1u });
        Assert.Equal(Convert.FromHexString("A1B2C3D4"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "CashPickupRequest", body);
        Assert.Equal(unchecked((int)0xD4C3B2A1u), fields["cash_slot_key"]);
        RejectIncompleteAndTrailing(build, "CashPickupRequest", body);
    }

    /// <summary>The baseline reply carries the cash-slot key before the result word.</summary>
    [Fact]
    public void BaselineReplyContainsTwoWords()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "CashPickupReply", ReplyValues());
        Assert.Equal(Convert.FromHexString("A1B2C3D401000000"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "CashPickupReply", body);
        Assert.Equal(2, fields.Count);
        Assert.Equal(unchecked((int)0xD4C3B2A1u), fields["cash_slot_id"]);
        Assert.Equal(1, fields["result"]);
        RejectIncompleteAndTrailing("kr-812", "CashPickupReply", body);
    }

    /// <summary>Each modern reader requires the extra word and a counted UTF-16 string.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernReplyKeepsItsPresentationTailAndRejectsMalformedStrings(string build)
    {
        var values = ReplyValues();
        values["unknown_8"] = 0x88776655u;
        values["unknown_12"] = "A\uD55C";
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CashPickupReply", values);
        Assert.Equal(Convert.FromHexString("A1B2C3D401000000556677880200000041005CD5"), body);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "CashPickupReply", body);
        Assert.Equal(4, fields.Count);
        Assert.Equal(unchecked((int)0xD4C3B2A1u), fields["cash_slot_id"]);
        Assert.Equal(1, fields["result"]);
        Assert.Equal(unchecked((int)0x88776655u), fields["unknown_8"]);
        Assert.Equal("A\uD55C", fields["unknown_12"]);
        RejectIncompleteAndTrailing(build, "CashPickupReply", body);
        body[12] = 3;
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "CashPickupReply", body));
    }

    /// <summary>An empty modern string still has its required four-byte character count.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernEmptyTailStillOccupiesSixteenBytes(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CashPickupReply", ReplyValues());
        Assert.Equal(Convert.FromHexString("A1B2C3D4010000000000000000000000"), body);
        RejectIncompleteAndTrailing(build, "CashPickupReply", body);
    }

    /// <summary>Request and reply values come from each build's game-direction table.</summary>
    [Theory]
    [InlineData("kr-812", 827, 831)]
    [InlineData("kr-1256", 829, 835)]
    [InlineData("cn-774", 829, 835)]
    [InlineData("tw-454", 829, 835)]
    public void PickupRowsDoNotUseOppositeDirectionOrRelayNumbers(string build, int request, int reply)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var path = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName,
            "profiles", build, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, profile);
        Assert.True(table.TryGet(PacketDirection.ClientToServer, (ushort)request, out var inbound));
        Assert.Equal("gcpReqPickup", inbound!.Name);
        Assert.True(table.TryGet(PacketDirection.ServerToClient, (ushort)reply, out var outbound));
        Assert.Equal("gspRepPickup", outbound!.Name);
        Assert.DoesNotContain(table.All, entry => entry.Direction == PacketDirection.ClientToServer
            && entry.Name == "gcpReqPickup" && entry.Value != request);
        Assert.DoesNotContain(table.All, entry => entry.Direction == PacketDirection.ServerToClient
            && entry.Name == "gspRepPickup" && entry.Value != reply);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.DoesNotContain(relay.All, entry => entry.Name is "gcpReqPickup" or "gspRepPickup");
    }

    private static Dictionary<string, object?> ReplyValues() => new()
    {
        ["cash_slot_id"] = 0xD4C3B2A1u,
        ["result"] = 1,
    };

    private static void RejectIncompleteAndTrailing(string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, truncated));
        }
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
