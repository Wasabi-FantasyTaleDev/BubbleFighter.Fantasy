using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Runs persisted equipment selection and view publication over native and renumbered TCP on kr-1256.</summary>
public sealed class EquipmentRemapScenarioTests
{
    private const int Shift = 3000;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(201003, 201001)]
    [InlineData(204003, 202001)]
    public async Task Kr1256RenumberedTablePreservesLoginViewsAndEquipmentChanges(int selected, int displaced)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var directory = Path.Combine(Path.GetTempPath(), $"bff-equipment-kr1256-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var original = await RunAsync(directory, 0, selected, displaced, limit.Token);
            var shifted = await RunAsync(directory, Shift, selected, displaced, limit.Token);
            Assert.Equal(original.Select(f => (int)f.Opcode), shifted.Select(f => f.Opcode - Shift));
            Assert.Equal(original.Count, shifted.Count);
            for (var i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].Opcode == 3 ? ValidateLoginAndReadTail(original[i].Payload) : original[i].Payload,
                    original[i].Opcode == 3 ? ValidateLoginAndReadTail(shifted[i].Payload) : shifted[i].Payload);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<IReadOnlyList<SimFrame>> RunAsync(
        string directory, int shift, int selected, int displaced, CancellationToken ct)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ct);
        var tablePath = Path.Combine(directory, $"opcodes-{shift}.yaml");
        var table = Regex.Replace(source, @"(?m)^(\s*- value: )([0-9]+)", match =>
            match.Groups[1].Value + checked((ushort)(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + shift))
                .ToString(CultureInfo.InvariantCulture));
        // The bootstrap frames stay wire-fixed: the simulated client sends C2S 1 and waits for S2C 2.
        table = Regex.Replace(table, @"(?m)^(\s*- value: )[0-9]+(\r?\n\s+direction: c2s\r?\n\s+name: gcpReqGamePatch\b)", "${1}1${2}");
        table = Regex.Replace(table, @"(?m)^(\s*- value: )[0-9]+(\r?\n\s+direction: s2c\r?\n\s+name: gspRepGamePatch\b)", "${1}2${2}");
        if (shift != 0) Assert.NotEqual(source, table);
        await File.WriteAllTextAsync(tablePath, table, ct);
        var port = FreePort();
        ushort Op(int value) => checked((ushort)(value + shift));
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "equipment-remap-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starting_lucci"] = 12345, ["starting_cash"] = 6789,
            }),
            SharedDialectRows.Kr1256(new() { ["inventory"] = true }),
            Row("inventoryview", new() { ["enabled"] = true, ["publish_equipped_view_push"] = true }),
            Row("equipment", new() { ["persist_worn_state"] = true }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("equipment", new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
            // The transport bootstrap precedes table-driven game messages.
            await client.SendAsync(1, handshake, ct, 0);
            Assert.Equal(new byte[8], (await client.WaitAsync(0, 2, ReplyTimeout, ct)).Payload);
            Assert.Single(client.Since(0));
            var start = client.Cursor;
            await client.SendAsync(Op(3), LauncherLogin(client.Name), ct, 0);
            await client.WaitAsync(start, Op(163), ReplyTimeout, ct);
            await BarrierAsync();
            var login = client.Since(start);
            // The shared login burst adds the empty gate table and the empty option sync between the counters and
            // the solo party (the modern row's old options for them are gone; the build data states the order).
            Assert.Equal(new[] { 3, 105, 106, 108, 17, 201, 1557, 802, 803, 808, 810, 117, 836, 837, 806, 163, 521 },
                login.Select(f => f.Opcode - shift));
            var identity = BinaryPrimitives.ReadInt32LittleEndian(login[14].Payload);
            Assert.True(identity > 0);
            AssertView(login[12].Payload, login[13].Payload, login[14].Payload, identity, 202001);

            // sub_80DA80 writes two integers and a string list; no response is proven for this request.
            var cursor = client.Cursor;
            await client.SendAsync(Op(806), ViewRequest(), ct, 0);
            await BarrierAsync();
            Assert.Equal(Op(521), Assert.Single(client.Since(cursor)).Opcode);

            cursor = client.Cursor;
            await client.SendAsync(Op(804), BitConverter.GetBytes((long)selected), ct, 0);
            await client.WaitAsync(cursor, Op(806), ReplyTimeout, ct);
            await BarrierAsync();
            var equipped = client.Since(cursor);
            Assert.Equal(new[] { 811, 803, 836, 837, 806, 521 }, equipped.Select(f => f.Opcode - shift));
            AssertAck(equipped[0].Payload, selected, success: true);
            var worn = AssertView(equipped[2].Payload, equipped[3].Payload, equipped[4].Payload, identity,
                selected is >= 202000 and < 205000 ? selected : 202001);
            Assert.Contains(selected, worn);
            Assert.DoesNotContain(displaced, worn);
            Assert.Equal(worn.Order(), ReadWorn(equipped[1].Payload).Order());

            cursor = client.Cursor;
            await client.SendAsync(Op(805), BitConverter.GetBytes((long)selected), ct, 0);
            await client.WaitAsync(cursor, Op(806), ReplyTimeout, ct);
            await BarrierAsync();
            var removed = client.Since(cursor);
            Assert.Equal(new[] { 812, 803, 836, 837, 806, 521 }, removed.Select(f => f.Opcode - shift));
            AssertAck(removed[0].Payload, selected, success: true);
            worn = AssertView(removed[2].Payload, removed[3].Payload, removed[4].Payload, identity,
                202001);
            Assert.DoesNotContain(selected, worn);
            Assert.Contains(displaced, worn);
            Assert.Equal(worn.Order(), ReadWorn(removed[1].Payload).Order());

            // An unowned item fails without an inventory/view push.
            foreach (var (request, reply, key) in new[] { (804, 811, 204999L) })
            {
                cursor = client.Cursor;
                await client.SendAsync(Op(request), BitConverter.GetBytes(key), ct, 0);
                await client.WaitAsync(cursor, Op(reply), ReplyTimeout, ct);
                await BarrierAsync();
                var failed = client.Since(cursor);
                Assert.Equal(new[] { reply, 521 }, failed.Select(f => f.Opcode - shift));
                AssertAck(failed[0].Payload, key, success: false);
            }
            cursor = client.Cursor;
            await client.SendAsync(Op(805), BitConverter.GetBytes(300001L), ct, 0);
            await client.WaitAsync(cursor, Op(806), ReplyTimeout, ct);
            await BarrierAsync();
            var requiredCharacter = client.Since(cursor);
            AssertAck(requiredCharacter[0].Payload, 300001L, success: true);
            Assert.Contains(300001, AssertView(requiredCharacter[2].Payload, requiredCharacter[3].Payload,
                requiredCharacter[4].Payload, identity, 202001));
            return client.Since(start);

            async Task BarrierAsync()
            {
                var before = client.Cursor;
                await client.SendAsync(Op(521), [], ct, 0);
                await client.WaitAsync(before, Op(521), ReplyTimeout, ct);
            }
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static void AssertAck(byte[] payload, long key, bool success)
    {
        // sub_9B34D0 / sub_9B3E00 read i32 result followed by i64 item key.
        Assert.Equal(12, payload.Length);
        Assert.Equal(success, BinaryPrimitives.ReadInt32LittleEndian(payload) == 0);
        Assert.Equal(key, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(4)));
    }

    private static int[] AssertView(byte[] index, byte[] presets, byte[] view, int identity, int primary)
    {
        Assert.Equal(new byte[5], index);
        // sub_9C3880 / sub_115C000: index, identity, three primary/special/unknown records, empty byte vector.
        Assert.Equal(49, presets.Length);
        var reader = new PacketReader(presets);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(identity, reader.ReadInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(primary, reader.ReadInt32());
            Assert.Equal(205001, reader.ReadInt32());
            Assert.Equal(0, reader.ReadInt32());
        }
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        reader = new PacketReader(view);
        Assert.Equal(identity, reader.ReadInt32());
        Assert.Equal(300001, reader.ReadInt32());
        var count = reader.ReadUInt32();
        Assert.InRange(count, 1u, 5u);
        var worn = new int[count];
        for (var i = 0; i < worn.Length; i++) worn[i] = reader.ReadInt32();
        Assert.Contains(300001, worn);
        for (var i = 0; i < 4; i++) Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        return worn;
    }

    private static int[] ReadWorn(byte[] body)
    {
        using var source = new MemoryStream(body, 4, body.Length - 4);
        using var zip = new ZLibStream(source, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zip.CopyTo(plain);
        var reader = new PacketReader(plain.ToArray());
        var count = reader.ReadUInt32();
        var worn = new List<int>();
        for (var i = 0u; i < count; i++)
        {
            var id = reader.ReadInt32();
            reader.ReadInt32(); reader.ReadInt64(); reader.ReadWideStringPrefixed();
            Assert.Equal(0u, reader.ReadUInt32());
            if ((reader.ReadByte() & 1) != 0) worn.Add(id);
        }
        return worn.ToArray();
    }

    private static byte[] ViewRequest()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(1); writer.WriteInt32(2); writer.WriteUInt32(2);
        writer.WriteWideStringPrefixed("AB"); writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] LauncherLogin(string account)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(account);
        writer.WriteWideStringPrefixed(string.Empty); writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ValidateLoginAndReadTail(byte[] payload)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(0, reader.ReadInt32());
        var sid = reader.ReadWideStringPrefixed();
        Assert.Matches(@"\Abff-[1-9][0-9]*\z", sid);
        Assert.True(long.TryParse(sid.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id));
        Assert.True(id > 0);
        return payload[reader.Offset..];
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
