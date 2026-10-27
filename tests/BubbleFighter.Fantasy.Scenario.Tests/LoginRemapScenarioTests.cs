using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
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

/// <summary>Login is dispatched by packet name, not number: the same kr-812 / cn-717 scenario
/// runs against a table whose every opcode value is shifted by a fixed offset, and the server
/// answers on the shifted numbers with byte-identical payloads; the kr-1256 family's shared
/// login and main-menu path proves the same over its own modern row set.</summary>
public sealed class LoginRemapScenarioTests
{
    private const int Shift = 3000;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("kr", 812, 458)]
    [InlineData("cn", 717, 0)]
    public async Task RenumberedTableAnswersLoginWithTheSameBurst(string region, int build, int patch)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var directory = Path.Combine(Path.GetTempPath(), $"bff-login-remap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var reference = await LoginBurstAsync(directory, region, build, patch, 0, ct);
            var shifted = await LoginBurstAsync(directory, region, build, patch, Shift, ct);

            Assert.Equal(Shift, shifted.CenterList - reference.CenterList);
            Assert.Equal([3, 105, 110, 201, 108], reference.Frames.Select(f => (int)f.Opcode));
            Assert.Equal(reference.Frames.Select(f => (int)f.Opcode), shifted.Frames.Select(f => f.Opcode - Shift));
            for (var i = 0; i < reference.Frames.Count; i++)
            {
                var expected = reference.Frames[i].Payload;
                var actual = shifted.Frames[i].Payload;
                // gspRepLogin's f64 OLE-date server time is the only wall-clock field in the burst.
                Assert.Equal(i == 0 ? WithoutServerTime(expected) : expected,
                    i == 0 ? WithoutServerTime(actual) : actual);
                if (i == 0)
                    Assert.InRange(DateTime.FromOADate(ServerTime(actual)), DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Preserves the kr-1256 login burst's order and payloads under a shifted table: reply, user state, wallet and
    /// room counters, the empty gate table, the solo party, the empty option sync, the bag family, the kind-1
    /// main-menu install and the first-move-channel notice.
    /// </summary>
    [Fact]
    public async Task Kr1256RenumberedTablePreservesLoginAndMainMenu()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var directory = Path.Combine(Path.GetTempPath(), $"bff-login-kr1256-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var original = await ModernLoginBurstAsync(directory, 0, limit.Token);
            var shifted = await ModernLoginBurstAsync(directory, Shift, limit.Token);
            int[] expected =
                [3, 105, 106, 108, 17, 201, 1557, 802, 803, 808, 810, 117, 163];
            Assert.Equal(expected, original.Select(f => (int)f.Opcode));
            Assert.Equal(expected, shifted.Select(f => (int)f.Opcode - Shift));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i] == 3 ? ValidateLoginAndReadTail(original[i].Payload) : original[i].Payload,
                    expected[i] == 3 ? ValidateLoginAndReadTail(shifted[i].Payload) : shifted[i].Payload);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(ushort CenterList, IReadOnlyList<(ushort Opcode, byte[] Payload)> Frames)> LoginBurstAsync(
        string directory, string region, int build, int patch, int shift, CancellationToken ct)
    {
        var profile = $"{region}-{build}";
        var tablePath = Path.Combine(directory, $"opcodes-{shift}.yaml");
        await File.WriteAllTextAsync(tablePath, Table(profile, build, shift), ct);
        var port = FreePort();
        ushort Op(int value) => checked((ushort)(value + shift));
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "login-remap-scenario", ClientBuild = new(region, build, patch), Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig(profile)),
            Row("protocol", new()
            {
                ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1,
                ["patch_request_name"] = "gcpFirstConnect",
                ["center_gates"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["key"] = 6, ["sub_key"] = 1, ["unknown1"] = 0,
                        ["address"] = "127.0.0.1", ["port"] = port,
                    },
                },
            }),
            Row("dialect.messages", new() { ["protocol"] = profile + "/game", ["inventory"] = true, ["packets_directory"] = PacketsDirectory() }),
            Row("dialect.rooms", new() { ["protocol"] = profile + "/game", ["packets_directory"] = PacketsDirectory() }),
            Row("lobby", new() { ["protocol"] = profile + "/game" }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = profile + "/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("remap", new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get(profile), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, build);
            await client.SendAsync(1, handshake, ct, 0);
            await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct); // gspRepGamePatch is wire-fixed
            var gates = await client.WaitAsync(0, Op(17), TimeSpan.FromSeconds(5), ct);
            Assert.Equal(22, gates.Payload.Length);

            var body = new ArrayBufferWriter<byte>();
            var writer = new PacketWriter(body);
            writer.WriteWideStringPrefixed(client.Name);
            writer.WriteWideStringPrefixed(string.Empty);
            writer.WriteWideStringPrefixed(string.Empty);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            var cursor = client.Cursor;
            await client.SendAsync(Op(3), body.WrittenSpan.ToArray(), ct, 0);
            await client.WaitAsync(cursor, Op(108), TimeSpan.FromSeconds(5), ct);
            return (gates.Opcode, client.Since(cursor).Select(f => (f.Opcode, f.Payload)).ToArray());
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static string Table(string profile, int build, int shift)
    {
        var entries = new List<(int Value, string Direction, string Name)>
        {
            (3, "c2s", "gcpReqLogin"), (3, "s2c", "gspRepLogin"), (17, "s2c", "gspCenterList"),
            (105, "s2c", "gspUserStat"), (801, "s2c", "gspInventoryUpdateAllInventorySlot"),
            (803, "s2c", "gspInventoryUpdateInventorySlots"),
            (106, "c2s", "gcpReqGotoLobby"), (109, "s2c", "gspGotoLobby"),
            (110, "s2c", "gspGotoRoom"), (108, "s2c", "gspGotoMenu"), (201, "s2c", "gspUpdateParty"),
            (405, "c2s", "gcpReqCreateTutorialSession"), (920, "c2s", "gcpReqSingleStageInfo"),
            (107, "c2s", "gcpReqPlayTutorial"), (401, "c2s", "gcpReqListSession"),
            (402, "c2s", "gcpReqCreateSession"), (505, "c2s", "gcpRoomReqReadyStatus"),
            (503, "c2s", "gcpRoomReqEnterGame"), (2025, "c2s", "gcpUserHostStart"),
            (986, "s2c", "gspUpdateScoreData"), (729, "s2c", "gspGameBroadcastPlayerScoreData"),
            (732, "s2c", "gspGameTimeOverEnd"), (733, "s2c", "gspGameShowResult_"),
        };
        if (build is 812 or 717)
            entries.AddRange(
            [
                (710, "c2s", "gcpGameUpdateScore"),
                (701, "c2s", "gcpGameReportMapLoadingComplete"), (32857, "c2s", "gppCreateMobsFromSpawnGroup"),
                (903, "c2s", "gcpReqStartNewWave"), (908, "c2s", "gcpReqWaveAllSpawnMobs"),
                (32892, "c2s", "gppCreateAIObject"), (32862, "c2s", "gppCreateSpawnGroupMemberMob"),
            ]);
        // The protocol row resolves the bootstrap frames by name; they stay at 1 and 2 in every remap.
        var handshake = $"  - value: 1\n    direction: c2s\n    name: gcpFirstConnect\n    name_source: label@{profile}\n    evidence: verified\n"
            + $"  - value: 2\n    direction: s2c\n    name: gspRepGamePatch\n    name_source: label@{profile}\n    evidence: verified\n";
        return $"profile: {profile}\nopcodes:\n" + handshake + string.Concat(entries.Select(e =>
            $"  - value: {e.Value + shift}\n    direction: {e.Direction}\n    name: {e.Name}\n    name_source: {(e.Name.EndsWith('_') ? "coined" : "label@" + profile)}\n    evidence: verified\n"));
    }

    private static byte[] WithoutServerTime(byte[] payload)
    {
        var reader = new PacketReader(payload);
        reader.ReadInt32();                 // result
        reader.ReadWideStringPrefixed();    // account
        reader.ReadInt32();                 // userNumber
        reader.ReadWideStringPrefixed();    // nickname
        reader.ReadBytes(16);               // characterType, unknown2, unknown3, unknown4
        var masked = payload.ToArray();
        Array.Clear(masked, reader.Offset, sizeof(double));
        return masked;
    }

    private static double ServerTime(byte[] payload)
    {
        var reader = new PacketReader(payload);
        reader.ReadInt32();
        reader.ReadWideStringPrefixed();
        reader.ReadInt32();
        reader.ReadWideStringPrefixed();
        reader.ReadBytes(16);
        return reader.ReadDouble();
    }

    private static async Task<IReadOnlyList<SimFrame>> ModernLoginBurstAsync(
        string directory, int shift, CancellationToken ct)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ct);
        var tablePath = Path.Combine(directory, $"opcodes-{shift}.yaml");
        // Retain names, directions and connection scopes, including UnknownNNN names, across renumbering.
        var table = Regex.Replace(source, @"(?m)^(\s*- value: )([0-9]+)", match =>
            match.Groups[1].Value + checked((ushort)(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + shift))
                .ToString(CultureInfo.InvariantCulture));
        // The bootstrap frames stay wire-fixed: the simulated client sends C2S 1 and waits for S2C 2.
        table = Regex.Replace(table, @"(?m)^(\s*- value: )[0-9]+(\r?\n\s+direction: c2s\r?\n\s+name: gcpReqGamePatch\b)", "${1}1${2}");
        table = Regex.Replace(table, @"(?m)^(\s*- value: )[0-9]+(\r?\n\s+direction: s2c\r?\n\s+name: gspRepGamePatch\b)", "${1}2${2}");
        if (shift != 0)
            Assert.NotEqual(source, table);
        await File.WriteAllTextAsync(tablePath, table, ct);
        var port = FreePort();
        ushort Op(int value) => checked((ushort)(value + shift));
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "login-remap-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            SharedDialectRows.Kr1256(new() { ["inventory"] = true }),
            // A stored wallet proves the binding passes the login's wallet into the S2C 106 words.
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
                ["starting_lucci"] = 12345, ["starting_cash"] = 6789,
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("remap", new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
            // Protocol bootstrap C2S 1 / S2C 2 precedes table-driven messages and remains wire-fixed.
            await client.SendAsync(1, handshake, ct, 0);
            var patch = await client.WaitAsync(0, 2, ReplyTimeout, ct);
            Assert.Equal(new byte[8], patch.Payload);
            Assert.Single(client.Since(0));

            var cursor = client.Cursor;
            await client.SendAsync(Op(3), LauncherLogin(client.Name), ct, 0);
            await client.WaitAsync(cursor, Op(163), ReplyTimeout, ct);
            // The empty main-menu probe is also table-driven on the game scope; the shared row answers the
            // empty session signature.
            await client.SendAsync(Op(521), [], ct, 0);
            await client.WaitAsync(cursor, Op(521), ReplyTimeout, ct);
            var frames = client.Since(cursor);
            Assert.Equal(7, Assert.Single(frames, f => f.Opcode == Op(521)).Payload.Length);
            var wallet = Assert.Single(frames, f => f.Opcode == Op(106)).Payload;
            Assert.Equal(24, wallet.Length);
            // The binding copies the stored wallet into the two balance words the client keeps.
            Assert.Equal(12345, BinaryPrimitives.ReadInt32LittleEndian(wallet.AsSpan(16)));
            Assert.Equal(6789, BinaryPrimitives.ReadInt32LittleEndian(wallet.AsSpan(20)));
            Assert.True(Assert.Single(frames, f => f.Opcode == Op(802)).Payload.Length > sizeof(uint));
            Assert.True(Assert.Single(frames, f => f.Opcode == Op(803)).Payload.Length > sizeof(uint));
            return frames.Where(frame => frame.Opcode != Op(521)).ToArray();
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    private static byte[] LauncherLogin(string account)
    {
        // Launcher serializer sub_10C6A00: three strings, counted i32 vector, two i32 and one string.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(account);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ValidateLoginAndReadTail(byte[] payload)
    {
        // sub_10C6E80 reads result then a prefixed session id. Only that generated id may differ.
        var reader = new PacketReader(payload);
        Assert.Equal(0, reader.ReadInt32());
        var sessionId = reader.ReadWideStringPrefixed();
        Assert.Matches(@"\Abff-[1-9][0-9]*\z", sessionId);
        Assert.True(long.TryParse(sessionId.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id));
        Assert.True(id > 0);
        return payload[reader.Offset..];
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
