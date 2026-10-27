using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Pins the inventory action bodies and opcode pairs the native clients exchange today, so a later widening of
/// the action cannot move the existing throw exchange or the delete reply.
/// </summary>
public sealed class InventoryActionWirePinTests
{
    // 202003 = 0x00031513.
    private static readonly byte[] ItemBody = [0x13, 0x15, 0x03, 0x00];

    /// <summary>The identity then result pair, result zero.</summary>
    private static readonly byte[] SuccessReplyBody = [0x13, 0x15, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>The identity then result pair, native general failure.</summary>
    private static readonly byte[] FailureReplyBody = [0x13, 0x15, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00];

    /// <summary>Rejects truncated or extended identity words for each audited throw request layout.</summary>
    /// <param name="build">The client layout whose request is decoded.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    public void TheThrowRequestIsOneUnsignedWord(string build)
    {
        var tables = Tables();
        var values = PacketCodec.ReadScalars(tables, build, "InventoryActionRequest", ItemBody);
        Assert.Equal(202003, Assert.IsType<int>(values["item_id"]));
        Assert.Equal(4, ItemBody.Length);
        var shortBody = new byte[3];
        Array.Copy(ItemBody, shortBody, shortBody.Length);
        Assert.Throws<PacketFormatException>(() =>
        {
            _ = PacketCodec.ReadScalars(tables, build, "InventoryActionRequest", shortBody);
        });
        var longBody = new byte[5];
        Array.Copy(ItemBody, longBody, ItemBody.Length);
        Assert.Throws<PacketFormatException>(() =>
        {
            _ = PacketCodec.ReadScalars(tables, build, "InventoryActionRequest", longBody);
        });
    }

    /// <summary>Pins identity/result ordering for successful and failed delete and throw replies.</summary>
    /// <param name="build">The client layout whose reply is encoded.</param>
    /// <param name="structure">The inventory reply structure to check.</param>
    [Theory]
    [InlineData("kr-812", "InventoryThrowReply")]
    [InlineData("kr-1256", "InventoryThrowReply")]
    [InlineData("kr-812", "InventoryDeleteReply")]
    [InlineData("kr-1256", "InventoryDeleteReply")]
    public void TheReplyIsTheIdentityThenTheResult(string build, string structure)
    {
        var tables = Tables();
        var success = PacketCodec.Write(tables, build, structure,
            new Dictionary<string, object?> { ["item_id"] = 202003, ["result"] = 0 });
        Assert.Equal(8, success.Length);
        Assert.Equal(SuccessReplyBody, success);
        var failure = PacketCodec.Write(tables, build, structure,
            new Dictionary<string, object?> { ["item_id"] = 202003, ["result"] = 1 });
        Assert.Equal(8, failure.Length);
        Assert.Equal(FailureReplyBody, failure);
        var values = PacketCodec.ReadScalars(tables, build, structure, success);
        Assert.Equal(202003, Assert.IsType<int>(values["item_id"]));
        Assert.Equal(0, Assert.IsType<int>(values["result"]));
    }

    /// <summary>Separates the request and reply opcode spaces for the audited throw exchanges.</summary>
    /// <param name="build">The client opcode table to resolve.</param>
    /// <param name="request">The expected game request number.</param>
    /// <param name="reply">The expected game reply number.</param>
    [Theory]
    [InlineData("kr-812", 807, 812)]
    [InlineData("kr-1256", 808, 815)]
    public void TheThrowOpcodePairIsPinned(string build, int request, int reply)
    {
        var table = Opcodes(build);
        Assert.Equal((ushort)request, table.C2S("gcpReqInventoryThrowItem"));
        Assert.Equal((ushort)reply, table.S2C("gspRepInventoryThrowItem"));
    }

    /// <summary>Preserves delete reply numbers without confusing the modern use-item request with legacy delete.</summary>
    [Fact]
    public void TheDeleteReplyKeepsItsNumberAndTheModernUseRequestKeeps806()
    {
        // The kr-812 client sends the delete request on C2S 806 and reads its reply on S2C 811.
        var legacy = Opcodes("kr-812");
        Assert.Equal((ushort)806, legacy.C2S("gcpReqInventoryDeleteItem"));
        Assert.Equal((ushort)811, legacy.S2C("gspRepInventoryDeleteItem"));
        Assert.Equal((ushort)812, legacy.S2C("gspRepInventoryThrowItem"));

        // The inspected kr-1256 removal UI uses throw; its C2S 806 is use-item.
        // These opcode pins do not prove the absence of a delete sender elsewhere.
        var modern = Opcodes("kr-1256");
        Assert.Equal((ushort)814, modern.S2C("gspRepInventoryDeleteItem"));
        Assert.Equal((ushort)815, modern.S2C("gspRepInventoryThrowItem"));
        Assert.Equal((ushort)806, modern.C2S("gcpReqInventoryUseItem"));
        Assert.Equal((ushort)808, modern.C2S("gcpReqInventoryThrowItem"));
    }

    private static PacketTables Tables() => PacketTables.Load(Path.Combine(Root(), "packets"));

    private static IOpcodeTable Opcodes(string build) =>
        OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", build, "opcodes.yaml"), Profile(build))
            .ForConnection(OpcodeConnection.Game);

    private static ClientProfile Profile(string build) =>
        new(build, build[..2], int.Parse(build[3..], CultureInfo.InvariantCulture), 0, 0, build);

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }
}
