using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the distinct early room inventory command, its native selector bounds and its unload behavior.</summary>
public sealed class EarlyRoomInventoryTests
{
    /// <summary>Each build uses its own selector count and named opcode, including when the opcode table is remapped.</summary>
    [Theory]
    [InlineData("cn-279", 4, false)]
    [InlineData("cn-279", 4, true)]
    [InlineData("cn-324", 5, false)]
    [InlineData("cn-324", 5, true)]
    public async Task NativeSelectionDispatchesPublishesAndUnloads(string build, int count, bool remap)
    {
        var profile = new ClientProfile(build, "cn", count == 4 ? 279 : 324, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(row => row with { Value = checked((ushort)(row.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        using var data = fixture.Services.Register<IClientDataStore>(new Data());
        var selectors = count == 4 ? new[] { 2001, 1001, 7001, 5001 } : [2001, 1001, 7001, 5001, -1];
        byte[] Request(int[] words, int character = 2) => PacketCodec.Write(TestBuilds.Packets, build,
            "RoomInventoryChangeRequest", new Dictionary<string, object?>
            { ["character_selection"] = character, ["unknown_8"] = -1234567, ["weapon_selections"] = words });
        var body = Request(selectors);
        Assert.Equal(12 + count * 4, body.Length);
        Assert.Equal(count, BitConverter.ToInt32(body, 8));
        if (count == 4)
            Assert.Equal("020000007929EDFF04000000D1070000E9030000591B000089130000", Convert.ToHexString(body));
        else
            Assert.Equal("020000007929EDFF05000000D1070000E9030000591B000089130000FFFFFFFF", Convert.ToHexString(body));
        try
        {
            var binding = new RoomInventoryBinding(protocol, table, TestBuilds.Packets, build, NullLogger.Instance);
            binding.Mount(context);
            async Task Dispatch(byte[] bytes) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpRoomChangeInventory"), bytes), default);
            await Dispatch(body);
            var request = Assert.IsType<RoomInventoryChangeRequested>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(2, request.Selection.CharacterSelection);
            Assert.Equal(selectors, request.Selection.WeaponSelections);
            foreach (var invalid in new[] { body[..^1], body.Concat(new byte[1]).ToArray(), Request(selectors[..^1]),
                Request([.. selectors, 0]), Request(selectors, 999), Request([30001, .. selectors.Skip(1)]),
                Request([1001, .. selectors.Skip(1)]) })
                await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch(invalid));
            if (count == 5)
                await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch(Request([2001, 1001, 7001, 5001, 99])));
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoomInventoryChanged>>());
            await sender.SendAsync(session, new(3, request.Selection), default);
            Assert.Equal(table.S2C("gspRoomChangeInventory"), session.Sent[^1]);
            var changed = PacketCodec.Read(TestBuilds.Packets, build, "RoomInventoryChanged", session.Bodies[^1]);
            Assert.Equal(3, changed["unknown_4"]);
            Assert.Equal(2, changed["unknown_8"]);
            Assert.Equal(0, changed["unknown_12"]);
            Assert.Equal(16 + 4 * count, session.Bodies[^1].Length);
            Assert.Equal(selectors, ((object?[])changed["unknown_16"]!).Cast<int>());
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(body));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoomInventoryChangeRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoomInventoryChanged>>());
    }

    /// <summary>A full native seat snapshot retains the same short selectors, while other envelope maps stay unchanged.</summary>
    [Theory]
    [InlineData("cn-279", 4)]
    [InlineData("cn-324", 5)]
    public void SnapshotProjectionUsesAuditedSlotFields(string build, int count)
    {
        var values = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot",
            PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", new Dictionary<string, object?>()));
        var selectors = count == 4 ? new[] { 2001, 1001, 7001, 5001 } : [2001, 1001, 7001, 5001, -1];
        var selection = new RoomInventorySelection(2, selectors);
        RoomInventoryProjection.Apply(TestBuilds.Packets, build, values, selection);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", values));
        var fields = RoomInventoryProjection.Fields(TestBuilds.Packets, build);
        Assert.Equal((ushort)2, decoded[fields.Character]);
        Assert.Equal(selectors.Select(word => unchecked((ushort)word)), fields.Weapons.Select(field => (ushort)decoded[field]!));
        var other = new Dictionary<string, object?> { ["slot"] = 3 };
        RoomInventoryProjection.Apply(TestBuilds.Packets, build, other, selection);
        Assert.Single(other);
    }

    private sealed class Data : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path)
        {
            var sheets = path.Contains("Fighter", StringComparison.Ordinal)
                ? Sheet("FighterConfig", "[Field]", ["1", "2"])
                : string.Concat(new[] { ("Rifle", "2001"), ("Shotgun", "3001"), ("SniperRifle", "4001"),
                    ("Pistol", "1001"), ("Near", "7001"), ("Grenade", "5001") }
                    .Select(pair => Sheet(pair.Item1, "id", [pair.Item2])));
            return Encoding.UTF8.GetBytes("<Workbook xmlns='urn:schemas-microsoft-com:office:spreadsheet' "
                + "xmlns:ss='urn:schemas-microsoft-com:office:spreadsheet'>" + sheets + "</Workbook>");
        }
        private static string Sheet(string name, string key, string[] ids) => "<Worksheet ss:Name='" + name + "'><Table><Row>"
            + string.Concat(new[] { key, "description" }.Concat(ids).Select(value => "<Cell><Data ss:Type='String'>" + value + "</Data></Cell>"))
            + "</Row></Table></Worksheet>";
    }
}
