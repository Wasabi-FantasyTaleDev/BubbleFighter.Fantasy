using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native creation readers and replies, including malformed string and packet boundaries.</summary>
public sealed class CharacterCreationBindingTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void InventoryUseMatchesReferenceRenameRequestAndNativeResponseLength(string build)
    {
        var binding = CreateBinding(build);
        var payload = Convert.FromHexString("08870100010000001A0000006300680061006E006700650055007300650072004E0061006D0065003D0054006500730074004E0061006D006500300030003100");
        Assert.Equal(64, payload.Length);
        var fields = binding.Read("InventoryUseRequest", payload);
        Assert.Equal(100104, fields["item_id"]);
        Assert.Equal(1, fields["quantity"]);
        Assert.Equal("changeUserName=TestName001", fields["parameters"]);
        Assert.Throws<PacketFormatException>(() => binding.Read("InventoryUseRequest", payload[..^1]));
        var reply = binding.Write("InventoryUseReply", new() { ["result"] = 0, ["item_id"] = 100104, ["parameters"] = fields["parameters"] });
        Assert.Equal(64, reply.Length);
        Assert.Equal(Convert.FromHexString("0000000008870100"), reply[..8]);
        Assert.Equal(payload[8..], reply[8..]);
        Assert.Equal(12, binding.Write("InventoryUseReply", new() { ["result"] = 9, ["item_id"] = 100104, ["parameters"] = "" }).Length);
    }
    [Fact]
    public void RenameConfirmationUsesTheNativeTwoByteWordAndStringLayout()
    {
        var bytes = CreateBinding("cn-324").Write("ServerNotice", new()
        {
            ["unknown_4"] = 0, ["unknown_5"] = 0, ["unknown_8"] = 0, ["text"] = "ok",
        });
        Assert.Equal(14, bytes.Length);
        Assert.Equal(Convert.FromHexString("000000000000020000006F006B00"), bytes);
    }

    [Fact]
    public void NativeCreationFieldsConsumeTheWholeBodyAndRejectTruncation()
    {
        var binding = CreateBinding();
        var body = Convert.FromHexString("0200000061006200020000000000000000000000");
        Assert.Equal(20, body.Length);
        var values = binding.Read("CharacterCreateRequest", body);
        Assert.Equal("ab", values["nickname"]);
        Assert.Equal(2, values["character_type"]);
        Assert.Equal(0, values["unknown_12"]);
        Assert.Equal("", values["unknown_16"]);
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterCreateRequest", body[..^1]));
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterCreateRequest", [.. body, 0]));
        Assert.Throws<PacketFormatException>(() => binding.Read("CharacterNameRequest", Convert.FromHexString("01040000")));
    }

    [Fact]
    public void NativeNameCheckAndCreationRepliesPinTheirExactBytes()
    {
        var binding = CreateBinding();
        var nameBody = Convert.FromHexString("0200000061006200");
        Assert.Equal(8, nameBody.Length);
        Assert.Equal("ab", binding.Read("CharacterNameRequest", nameBody)["nickname"]);
        var nameReply = binding.Write("CharacterNameReply", new() { ["result"] = 0, ["nickname"] = "ab" });
        Assert.Equal(Convert.FromHexString("000000000200000061006200"), nameReply);
        Assert.Equal(12, nameReply.Length);
        Assert.Equal(new byte[4], binding.Write("CharacterCreateReply", new() { ["result"] = 0 }));
    }

    private static CharacterCreationBinding CreateBinding(string build = "cn-279")
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(path, "builds.yaml")))
                return new(build + "/game", new UnusedOpcodeTable(), PacketTables.Load(path), build);
        }
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    private sealed class UnusedOpcodeTable : IOpcodeTable
    {
        public ClientProfile Profile => throw new NotSupportedException();
        public OpcodeConnection Connection => OpcodeConnection.Game;
        public IEnumerable<OpcodeEntry> All => [];
        public IOpcodeTable ForConnection(OpcodeConnection connection) => throw new NotSupportedException();
        public bool TryGet(PacketDirection d, ushort value, out OpcodeEntry? e) => throw new NotSupportedException();
        public string Describe(PacketDirection d, ushort value) => throw new NotSupportedException();
    }
}
