using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Exercises the cn-279 plugin composition over TCP, including named login and room routing.</summary>
public sealed class Cn279LoginScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoPlayersAndReconnectKeepTheirIdentityAcrossTheLoginAndRoomPackets(bool remapOpcodes)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var port = FreePort();
        var tablePath = Path.Combine(Path.GetTempPath(), $"bff-cn279-{Guid.NewGuid():N}.yaml");
        var table = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cn-279-opcodes.yaml"), ct);
        if (remapOpcodes)
        {
            table = table.Replace("value: 3\n", "value: 6003\n", StringComparison.Ordinal)
                .Replace("value: 3\r\n", "value: 6003\r\n", StringComparison.Ordinal)
                .Replace("value: 108", "value: 6108", StringComparison.Ordinal)
                .Replace("value: 110", "value: 6110", StringComparison.Ordinal);
        }
        await File.WriteAllTextAsync(tablePath, table, ct);
        var host = BuildHost(port, tablePath);
        try
        {
            await host.StartAsync(ct);
            await using var second = await ConnectAsync("cd", port, ct);
            int firstId;
            await using (var first = await ConnectAsync("ab", port, ct))
            {
                firstId = await LoginAsync(first, port, remapOpcodes, ct);
                var secondId = await LoginAsync(second, port, remapOpcodes, ct);
                Assert.NotEqual(firstId, secondId);
            }
            await using var reconnected = await ConnectAsync("ab", port, ct);
            Assert.Equal(firstId, await LoginAsync(reconnected, port, remapOpcodes, ct));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            File.Delete(tablePath);
        }
    }

    private static Task<SimClient> ConnectAsync(string name, int port, CancellationToken ct) =>
        SimClient.ConnectAsync(name, new DnsEndPoint("127.0.0.1", port), TestWireDialects.Get("cn-279"),
            ushort.MaxValue, ct);

    [Theory]
    [InlineData(1, "\u76AE\u86CB", false)]
    [InlineData(2, "\u9ED1\u599E", false)]
    [InlineData(1, "\u76AE\u86CB", true)]
    [InlineData(2, "\u9ED1\u599E", true)]
    public async Task NativeCreationPreservesOrRepeatsChosenCharacterOnReconnect(int characterType, string nickname, bool alwaysCreate)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var port = FreePort();
        var host = BuildHost(port, Path.Combine(AppContext.BaseDirectory, "cn-279-opcodes.yaml"),
            useOldMap: true, creationPreview: true, alwaysCreate: alwaysCreate);
        try
        {
            await host.StartAsync(ct);
            int identity;
            await using (var client = await ConnectAsync("ab", port, ct))
            {
                identity = await LoginAsync(client, port, false, ct, characterType, nickname);
                var move = await client.WaitAsync(0, 10, TimeSpan.FromSeconds(5), ct);
                Assert.Equal(12, move.Payload.Length);
                var tutorialNotice = await client.WaitAsync(move.Index + 1, 11, TimeSpan.FromSeconds(5), ct);
                Assert.Empty(tutorialNotice.Payload);
            }
            await using var reconnected = await ConnectAsync("ab", port, ct);
            var nextCharacter = alwaysCreate ? 3 - characterType : characterType;
            var nextName = alwaysCreate ? "cd" : nickname;
            Assert.Equal(identity, await LoginAsync(reconnected, port, false, ct,
                creationType: alwaysCreate ? nextCharacter : 0, nickname: nextName, expectedCharacter: nextCharacter));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "rel_Tutorial_01")]
    [InlineData(true, "rel_prologue_01")]
    public async Task OldTutorialRequestsKeepTheNativeMapAndSeatBeforeEnter(bool useOldMap, string requestedMap)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = limit.Token;
        var port = FreePort();
        var tablePath = Path.Combine(AppContext.BaseDirectory, "cn-279-opcodes.yaml");
        var host = BuildHost(port, tablePath, useOldMap);
        try
        {
            await host.StartAsync(ct);
            await using var client = await ConnectAsync("ab", port, ct);
            var identity = await LoginAsync(client, port, remapped: false, ct);
            if (useOldMap)
            {
                var notice = await client.WaitAsync(0, 10, TimeSpan.FromSeconds(5), ct);
                var notify = await client.WaitAsync(0, 11, TimeSpan.FromSeconds(5), ct);
                Assert.Equal(12, notice.Payload.Length);
                Assert.Empty(notify.Payload);
                Assert.True(notice.Index < notify.Index);
            }
            var buffer = new ArrayBufferWriter<byte>();
            var w = new PacketWriter(buffer);
            // cn-279 sub_6F8B20's old request has four MapMeta bool32. A zero clock
            // must not be rejected by the horde-only clock requirement.
            for (var i = 0; i < 17; i++)
                w.WriteInt32(0);
            w.WriteWideStringPrefixed("");
            w.WriteWideStringPrefixed("");
            w.WriteWideStringPrefixed(requestedMap);
            w.WriteInt32(0);
            var cursor = client.Cursor;
            await client.SendAsync(107, buffer.WrittenSpan.ToArray(), ct, 0);
            var room = await client.WaitAsync(cursor, 110, TimeSpan.FromSeconds(5), ct);
            var setting = await client.WaitAsync(cursor, 505, TimeSpan.FromSeconds(5), ct);
            var seat = await client.WaitAsync(cursor, 503, TimeSpan.FromSeconds(5), ct);
            var enter = await client.WaitAsync(cursor, 510, TimeSpan.FromSeconds(5), ct);
            Assert.True(room.Index < setting.Index && setting.Index < seat.Index && seat.Index < enter.Index);
            var r = new PacketReader(setting.Payload);
            r.ReadBytes(8);
            for (var i = 0; i < 3; i++)
                Assert.Equal("", r.ReadWideStringPrefixed());
            Assert.Equal("rel_Tutorial_01", r.ReadWideStringPrefixed());
            r = new PacketReader(seat.Payload);
            Assert.Equal(0, r.ReadInt32());
            Assert.True(r.ReadBool32());
            Assert.False(r.ReadBool32());
            Assert.True(r.ReadBool32());
            Assert.Equal(0, r.ReadByte());
            Assert.Equal(identity, r.ReadInt32());
            Assert.Equal("ab", r.ReadWideStringPrefixed());
            r.ReadBytes(4);
            // cn-279 sub_820F20 maps team 2 to the blue tutorial spawn, #spawn_T21.
            Assert.Equal(2, r.ReadByte());
            r = new PacketReader(room.Payload);
            // cn-279 sub_41E700's SessionSetting, then sub_41E380's second GameSetting.
            r.ReadBytes(6);
            r.ReadWideStringPrefixed();
            r.ReadBytes(8);
            r.ReadWideStringPrefixed();
            r.ReadBytes(4);
            for (var settingCopy = 0; settingCopy < 2; settingCopy++)
            {
                r.ReadBytes(8);
                for (var field = 0; field < 4; field++)
                    r.ReadWideStringPrefixed();
                r.ReadBytes(48);
                if (settingCopy == 0)
                {
                    r.ReadBytes(12);
                    r.ReadWideStringPrefixed();
                }
            }
            r.ReadBytes(8);
            Assert.Equal(1, r.ReadInt32());
            r.ReadBytes(17);
            Assert.Equal("ab", r.ReadWideStringPrefixed());
            r.ReadBytes(4);
            Assert.Equal(2, r.ReadByte());
            r = new PacketReader(enter.Payload);
            Assert.Equal(0, r.ReadInt32());
            Assert.Equal("rel_Tutorial_01", r.ReadWideStringPrefixed());
            Assert.Equal(1408, r.Remaining);
            Assert.Equal(15, r.ReadInt32());
            cursor = client.Cursor;
            await client.SendAsync(723, new byte[] { 1, 0, 0, 0 }, ct, 0);
            var end = await client.WaitAsync(cursor, 705, TimeSpan.FromSeconds(5), ct);
            var menuRoom = await client.WaitAsync(cursor, 110, TimeSpan.FromSeconds(5), ct);
            var menu = await client.WaitAsync(cursor, 108, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(new byte[] { 0, 0, 0, 0, 3, 0, 0, 0 }, end.Payload);
            Assert.Equal(new byte[4], menu.Payload);
            Assert.True(end.Index < menuRoom.Index && menuRoom.Index < menu.Index);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task<int> LoginAsync(SimClient client, int port, bool remapped, CancellationToken ct,
        int creationType = 0, string? nickname = null, int expectedCharacter = 1)
    {
        var first = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(first);
        w.WriteInt32(279);
        for (var i = 0; i < 7; i++)
            w.WriteWideStringPrefixed(i == 0 ? new string('x', 607) : "");
        w.WriteInt32(0);
        for (var i = 0; i < 15; i++)
            w.WriteWideStringPrefixed("");
        Assert.Equal(1310, first.WrittenCount);
        await client.SendAsync(1, first.WrittenSpan.ToArray(), ct, 0);
        var patch = await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        var gates = await client.WaitAsync(0, 16, TimeSpan.FromSeconds(5), ct);
        Assert.Equal(new byte[8], patch.Payload);
        Assert.True(patch.Index < gates.Index);
        Assert.Equal(22, gates.Payload.Length);
        var gate = new PacketReader(gates.Payload);
        Assert.Equal(1, gate.ReadInt32());
        Assert.Equal(6, gate.ReadInt32());
        Assert.Equal(1, gate.ReadInt32());
        Assert.Equal(0, gate.ReadInt32());
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, gate.ReadBytes(4).ToArray());
        Assert.Equal(port, gate.ReadUInt16());
        Assert.Equal(0, gate.Remaining);

        // The original client sends its empty GPK notification between patch and login.
        await client.SendAsync(2, [], ct, 0);
        var request = new ArrayBufferWriter<byte>();
        w = new PacketWriter(request);
        w.WriteWideStringPrefixed(client.Name);
        w.WriteWideStringPrefixed("");
        w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
        var cursor = client.Cursor;
        var loginOpcode = (ushort)(remapped ? 6003 : 3);
        await client.SendAsync(loginOpcode, request.WrittenSpan.ToArray(), ct, 0);
        if (creationType != 0)
        {
            var open = await client.WaitAsync(cursor, 11, TimeSpan.FromSeconds(5), ct);
            Assert.Empty(open.Payload);
            var name = new ArrayBufferWriter<byte>();
            new PacketWriter(name).WriteWideStringPrefixed(nickname!);
            Assert.Equal(8, name.WrittenCount);
            await client.SendAsync(14, name.WrittenSpan.ToArray(), ct, 0);
            var checkedName = await client.WaitAsync(cursor, 12, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(12, checkedName.Payload.Length);
            var nameReader = new PacketReader(checkedName.Payload);
            Assert.Equal(0, nameReader.ReadInt32());
            Assert.Equal(nickname, nameReader.ReadWideStringPrefixed());
            Assert.Equal(0, nameReader.Remaining);
            var create = new ArrayBufferWriter<byte>();
            var createWriter = new PacketWriter(create);
            createWriter.WriteWideStringPrefixed(nickname!);
            createWriter.WriteInt32(creationType);
            createWriter.WriteInt32(creationType == 1 ? 1 : 0);
            createWriter.WriteWideStringPrefixed("");
            Assert.Equal(20, create.WrittenCount);
            await client.SendAsync(15, create.WrittenSpan.ToArray(), ct, 0);
            Assert.Equal(new byte[4], (await client.WaitAsync(cursor, 13, TimeSpan.FromSeconds(5), ct)).Payload);
            await client.SendAsync(16, [], ct, 0);
            expectedCharacter = creationType;
        }
        var reply = await client.WaitAsync(cursor, loginOpcode, TimeSpan.FromSeconds(5), ct);
        var room = await client.WaitAsync(cursor, (ushort)(remapped ? 6110 : 110), TimeSpan.FromSeconds(5), ct);
        var menu = await client.WaitAsync(cursor, (ushort)(remapped ? 6108 : 108), TimeSpan.FromSeconds(5), ct);
        Assert.True(reply.Index < room.Index && room.Index < menu.Index);
        Assert.Equal(new byte[4], menu.Payload);
        Assert.Equal(62, reply.Payload.Length);
        var login = new PacketReader(reply.Payload);
        Assert.Equal(0, login.ReadInt32());
        Assert.Equal(client.Name, login.ReadWideStringPrefixed());
        var identity = login.ReadInt32();
        Assert.True(identity > 0);
        Assert.Equal(nickname ?? client.Name, login.ReadWideStringPrefixed());
        var characterType = login.ReadInt32();
        Assert.Equal(expectedCharacter, characterType);
        login.ReadBytes(12);
        Assert.InRange(login.ReadDouble(), DateTime.Now.AddMinutes(-1).ToOADate(), DateTime.Now.AddMinutes(1).ToOADate());
        Assert.Equal("LOCAL", login.ReadWideStringPrefixed());
        Assert.Equal(0, login.Remaining);

        // cn-279 sub_4496E0 consumes a 102-byte two-character slot starting at 198;
        // cn-279 sub_4536F0 then reads the equipment list count at 300.
        var slot = new PacketReader(room.Payload.AsSpan(198));
        Assert.True(slot.ReadBool32());
        Assert.False(slot.ReadBool32());
        Assert.False(slot.ReadBool32());
        Assert.Equal(0xFF, slot.ReadByte());
        Assert.Equal((uint)identity, slot.ReadUInt32());
        Assert.Equal(nickname ?? client.Name, slot.ReadWideStringPrefixed());
        slot.ReadBytes(5);
        Assert.Equal(characterType, slot.ReadInt32());
        var equipped = new PacketReader(room.Payload.AsSpan(300));
        Assert.Equal(1, equipped.ReadInt32());
        Assert.Equal(identity, equipped.ReadInt32());
        Assert.Equal(300000 + characterType, equipped.ReadInt32());
        var count = equipped.ReadInt32();
        Assert.InRange(count, 1, 16);
        var items = new List<int>();
        for (var i = 0; i < count; i++)
            items.Add(equipped.ReadInt32());
        Assert.Contains(300000 + characterType, items);
        Assert.Equal(0, equipped.ReadInt32());
        Assert.Equal(0, equipped.Remaining);
        return identity;
    }

    private static PluginHost BuildHost(int port, string tablePath, bool useOldMap = false, bool creationPreview = false,
        bool alwaysCreate = false)
    {
        PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = id, Plugin = plugin, Source = "cn279-scenario", ClientBuild = new("cn", 279, 0), Config = config };
        return new PluginHost(
        [
            Row("core.protocol-registry", "core.protocol-registry"),
            Row("core.session-registry", "core.session-registry"),
            Row("core.packet-router", "core.packet-router"),
            Row("core.message-router", "core.message-router"),
            Row("core.client-profiles", "core.client-profiles"),
            Row("core.opcode-tables", "core.opcode-tables"),
            Row("core.wire-dialects", "core.wire-dialects"),
            Row("persistence.memory", "persistence.memory"),
            Row("dialect", "dialect", TestWireDialects.RowConfig("cn-279")),
            Row("protocol", "protocol", new()
            {
                ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1,
                ["center_gates"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["key"] = 6, ["sub_key"] = 1, ["unknown1"] = 0,
                        ["address"] = "127.0.0.1", ["port"] = port,
                    },
                },
            }),
            Row("game-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "cn-279/game" }),
            Row("dialect.messages", "dialect.messages", new()
            {
                ["protocol"] = "cn-279/game", ["inventory"] = true, ["character_creation_preview"] = creationPreview,
                ["packets_directory"] = PacketsDirectory(),
            }),
            Row("dialect.rooms", "dialect.rooms", new()
            {
                ["protocol"] = "cn-279/game", ["packets_directory"] = PacketsDirectory(),
                ["tutorial_use_old_map"] = useOldMap, ["tutorial_move_in"] = useOldMap,
            }),
            Row("lobby", "lobby", new()
            {
                ["protocol"] = "cn-279/game", ["character_creation_preview"] = creationPreview,
                ["inventory_source"] = creationPreview ? "mysql" : "reference",
                ["recording_always_create_character"] = alwaysCreate,
            }),
            Row("ingame", "ingame", new()
            {
                ["typed_only"] = true, ["game_protocol"] = "cn-279/game", ["version_major"] = 279,
            }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // The legacy room row reads every build's structures from the repository's packet tables.
    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }
}
