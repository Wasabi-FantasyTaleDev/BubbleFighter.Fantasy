using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Starts the real plugin host with cn-774's and tw-454's login rows and drives a launcher login over TCP:
/// the shared <c>dialect.messages</c> row must mount the login and main-menu-probe bindings, answer the
/// probe with the empty session signature and write the build's whole login burst. The payloads are the
/// deterministic bodies of this scenario (auto-created player, starter bag), pinned byte for byte; only the
/// server-issued session id inside S2C gspRepLogin may differ, and the test masks it the way the kr-1256
/// remap test does.
/// </summary>
public sealed class LoginModernRowsScenarioTests
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The fixed gcpFirstConnect opcode, as the remap test sends it.</summary>
    private const ushort FirstConnect = 1;

    /// <summary>The fixed gspRepGamePatch opcode, as the remap test waits for it.</summary>
    private const ushort GamePatchReply = 2;

    /// <summary>The login burst the modern reply form writes for a build without the gate list and option sync.</summary>
    private static readonly string[] BurstNames =
    [
        "gspRepLogin", "gspUserStat", "gspUserMoneyStat", "gspUserPcroomStat", "gspUpdateParty",
        "gspInventoryUpdateAllInventorySlot", "gspInventoryUpdateInventorySlots", "gspUpdateAllArticleSlot",
        "gspCashInvenUpdateAllInventorySlot", "gspGotoRoom", "gspNotifyFirstMoveChannel",
    ];

    /// <summary>
    /// The shared login row, mounted for a build whose bundle also mounts it, answers the launcher login and
    /// the empty main-menu probe exactly as the merged bundle does.
    /// </summary>
    /// <param name="build">The client profile id (cn-774 or tw-454).</param>
    /// <param name="major">The client build's executable major version.</param>
    [Theory]
    [MemberData(nameof(Builds))]
    public async Task SharedLoginRowServesTheBuildsBurstOverTcp(string build, int major)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var port = FreePort();
        var opcodes = OpcodeNumbers(build);
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new()
            {
                Id = plugin, Plugin = plugin, Source = "login-modern-rows", ClientBuild = BuildFor(build), Family = "modern",
                Config = config,
            };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig(build)),
            Row("protocol", new()
            {
                ["opcode_table_path"] = Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"),
                ["patch_request_name"] = "gcpFirstConnect",
                // The cn-774 and tw-454 bundles answer the patch request with status 2; no center gate is
                // configured, so the protocol row sends no gspCenterList frame before the login.
                ["bootstrap_status"] = 2,
            }),
            Row("dialect.messages", TestWireDialects.MessagesConfig(new()
            {
                ["protocol"] = build + "/game", ["login"] = true, ["main_menu_init"] = true, ["inventory"] = true,
            })),
            Row("lobby", new()
            {
                ["protocol"] = build + "/game", ["auto_create_players"] = true, ["authentication"] = "open",
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = build + "/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var client = await SimClient.ConnectAsync("login-" + build, new DnsEndPoint("127.0.0.1", port),
                TestWireDialects.Get(build), ushort.MaxValue, ct);
            var handshake = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(handshake, major);
            await client.SendAsync(FirstConnect, handshake, ct, 0);
            await client.WaitAsync(0, GamePatchReply, ReplyTimeout, ct);

            var cursor = client.Cursor;
            await client.SendAsync(Opcode(opcodes, "gcpReqLogin"), LauncherLogin(client.Name), ct, 0);
            await client.WaitAsync(cursor, Opcode(opcodes, "gspNotifyFirstMoveChannel"), ReplyTimeout, ct);
            var frames = client.Since(cursor);

            Assert.Equal(BurstNames, frames.Select(frame => NameOf(opcodes, frame.Opcode)).ToArray());
            var actual = frames.Select((frame, index) => index == 0 ? MaskSessionId(frame.Payload) : Convert.ToHexString(frame.Payload)).ToArray();
            var expected = ExpectedBodies(build);
            if (expected.Length == 0)
                Assert.Fail(string.Join("\n", frames.Select((frame, index) => BurstNames[index] + "=" + actual[index])));
            Assert.Equal(expected, actual);
            ValidateLogin(frames[0].Payload);
            Assert.True(Assert.Single(frames, frame => NameOf(opcodes, frame.Opcode) == "gspInventoryUpdateAllInventorySlot").Payload.Length > sizeof(uint));
            Assert.True(Assert.Single(frames, frame => NameOf(opcodes, frame.Opcode) == "gspInventoryUpdateInventorySlots").Payload.Length > sizeof(uint));

            var probe = client.Cursor;
            await client.SendAsync(Opcode(opcodes, "Unknown521"), [], ct, 0);
            var signature = await client.WaitAsync(probe, Opcode(opcodes, "gspSessionSignatureData"), ReplyTimeout, ct);
            Assert.Equal(7, signature.Payload.Length);
            Assert.Equal("00000000000000", Convert.ToHexString(signature.Payload));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    /// <summary>The two builds whose bundles mount the shared login row.</summary>
    public static TheoryData<string, int> Builds => new()
    {
        { "cn-774", 774 },
        { "tw-454", 454 },
    };

    private static string[] ExpectedBodies(string build) => build switch
    {
        "cn-774" => [
            // gspRepLogin
            "00000000SESSION010000000C0000006C006F00670069006E002D0063006E002D00370037003400010000000000000063000000000000000000000000000000000000000100000000",
            // gspUserStat
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000C0000006C006F00670069006E002D0063006E002D0037003700340001000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000E80300000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000C0000006C006F00670069006E002D0063006E002D003700370034000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            // gspUserMoneyStat
            "000000000000000000000000000000000000000000000000",
            // gspUserPcroomStat
            "0000000000000000000000000000",
            // gspUpdateParty
            "0200000001000000010000000C0000006C006F00670069006E002D0063006E002D00370037003400000000000000000000000000000000000100000001000000E193040000000000FFFFFFFF0000000000000000000000000000000000000000",
            // gspInventoryUpdateAllInventorySlot
            "13000000E19304009F86010000000000F069F840000000000000000001291103009F86010000000000F069F8400000000000000000012B1103009F86010000000000F069F8400000000000000000002C1103009F86010000000000F069F8400000000000000000002D1103009F86010000000000F069F840000000000000000000111503009F86010000000000F069F840000000000000000001131503009F86010000000000F069F840000000000000000000141503009F86010000000000F069F840000000000000000000161503009F86010000000000F069F840000000000000000000F91803009F86010000000000F069F840000000000000000000FB1803009F86010000000000F069F840000000000000000000FC1803009F86010000000000F069F840000000000000000000FD1803009F86010000000000F069F840000000000000000000E11C03009F86010000000000F069F840000000000000000000E31C03009F86010000000000F069F840000000000000000000E41C03009F86010000000000F069F840000000000000000000E51C03009F86010000000000F069F840000000000000000000C92003009F86010000000000F069F840000000000000000001992803009F86010000000000F069F840000000000000000001",
            // gspInventoryUpdateInventorySlots
            "13000000E19304009F86010000000000006AF840000000000000000001291103009F86010000000000006AF8400000000000000000012B1103009F86010000000000006AF8400000000000000000002C1103009F86010000000000006AF8400000000000000000002D1103009F86010000000000006AF840000000000000000000111503009F86010000000000006AF840000000000000000001131503009F86010000000000006AF840000000000000000000141503009F86010000000000006AF840000000000000000000161503009F86010000000000006AF840000000000000000000F91803009F86010000000000006AF840000000000000000000FB1803009F86010000000000006AF840000000000000000000FC1803009F86010000000000006AF840000000000000000000FD1803009F86010000000000006AF840000000000000000000E11C03009F86010000000000006AF840000000000000000000E31C03009F86010000000000006AF840000000000000000000E41C03009F86010000000000006AF840000000000000000000E51C03009F86010000000000006AF840000000000000000000C92003009F86010000000000006AF840000000000000000001992803009F86010000000000006AF840000000000000000001",
            // gspUpdateAllArticleSlot
            "00000000",
            // gspCashInvenUpdateAllInventorySlot
            "00000000",
            // gspGotoRoom
            "010001000000080000004D00610069006E004D0065006E007500000000000000000000000000000001000000080000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000000000000000000000010000000C0000006C006F00670069006E002D0063006E002D0037003700340000000000020100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FFFFFFFF0000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000E193040000000000FFFFFFFF00000000000000000000000000000000000000000000000000000000010000000201000000000000000B0000000100000002000000030000000400000005000000060000000700000008000000090000000A0000000B00000000000000",
            // gspNotifyFirstMoveChannel
            "",
        ],
        "tw-454" => [
            // gspRepLogin
            "00000000SESSION010000000C0000006C006F00670069006E002D00740077002D00340035003400010000000000000063000000000000000000000000000000000000000100000000",
            // gspUserStat
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000C0000006C006F00670069006E002D00740077002D0034003500340001000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000E8030000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000C0000006C006F00670069006E002D00740077002D003400350034000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            // gspUserMoneyStat
            "000000000000000000000000000000000000000000000000",
            // gspUserPcroomStat
            "0000000000000000000000000000",
            // gspUpdateParty
            "0200000001000000010000000C0000006C006F00670069006E002D00740077002D00340035003400000000000000000000000000000000000100000001000000E193040000000000FFFFFFFF00000000000000000000000000000000",
            // gspInventoryUpdateAllInventorySlot
            "13000000E19304009F86010000000000F069F840000000000000000001291103009F86010000000000F069F8400000000000000000012B1103009F86010000000000F069F8400000000000000000002C1103009F86010000000000F069F8400000000000000000002D1103009F86010000000000F069F840000000000000000000111503009F86010000000000F069F840000000000000000001131503009F86010000000000F069F840000000000000000000141503009F86010000000000F069F840000000000000000000161503009F86010000000000F069F840000000000000000000F91803009F86010000000000F069F840000000000000000000FB1803009F86010000000000F069F840000000000000000000FC1803009F86010000000000F069F840000000000000000000FD1803009F86010000000000F069F840000000000000000000E11C03009F86010000000000F069F840000000000000000000E31C03009F86010000000000F069F840000000000000000000E41C03009F86010000000000F069F840000000000000000000E51C03009F86010000000000F069F840000000000000000000C92003009F86010000000000F069F840000000000000000001992803009F86010000000000F069F840000000000000000001",
            // gspInventoryUpdateInventorySlots
            "13000000E19304009F86010000000000006AF840000000000000000001291103009F86010000000000006AF8400000000000000000012B1103009F86010000000000006AF8400000000000000000002C1103009F86010000000000006AF8400000000000000000002D1103009F86010000000000006AF840000000000000000000111503009F86010000000000006AF840000000000000000001131503009F86010000000000006AF840000000000000000000141503009F86010000000000006AF840000000000000000000161503009F86010000000000006AF840000000000000000000F91803009F86010000000000006AF840000000000000000000FB1803009F86010000000000006AF840000000000000000000FC1803009F86010000000000006AF840000000000000000000FD1803009F86010000000000006AF840000000000000000000E11C03009F86010000000000006AF840000000000000000000E31C03009F86010000000000006AF840000000000000000000E41C03009F86010000000000006AF840000000000000000000E51C03009F86010000000000006AF840000000000000000000C92003009F86010000000000006AF840000000000000000001992803009F86010000000000006AF840000000000000000001",
            // gspUpdateAllArticleSlot
            "00000000",
            // gspCashInvenUpdateAllInventorySlot
            "00000000",
            // gspGotoRoom
            "010001000000080000004D00610069006E004D0065006E00750000000000000000000000000000000100000000000000000000000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000000000000000000000010000000C0000006C006F00670069006E002D00740077002D0034003500340000000000020100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FFFFFFFF0000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000001000000E193040000000000FFFFFFFF000000000000000000000000000000000000000000000000010000000201000000000000000B0000000100000002000000030000000400000005000000060000000700000008000000090000000A0000000B00000000000000",
            // gspNotifyFirstMoveChannel
            "",
        ],
        _ => [],
    };

    private static ClientBuild BuildFor(string build) => build switch
    {
        "cn-774" => ClientBuild.Parse("cn:774:0"),
        "tw-454" => ClientBuild.Parse("tw:454:0"),
        _ => throw new ArgumentOutOfRangeException(nameof(build), build, "No build identity for this login row test."),
    };

    private static ushort Opcode(Dictionary<(string Direction, string Name), ushort> map, string name)
    {
        if (map.TryGetValue(("c2s", name), out var value) || map.TryGetValue(("s2c", name), out value))
            return value;
        throw new InvalidOperationException($"The build names no opcode '{name}'.");
    }

    private static string NameOf(Dictionary<(string Direction, string Name), ushort> map, ushort opcode) =>
        map.Where(pair => pair.Key.Direction == "s2c" && pair.Value == opcode).Select(pair => pair.Key.Name).FirstOrDefault()
        ?? throw new InvalidOperationException($"The build has no S2C name for opcode {opcode}.");

    /// <summary>Loads the build's own opcode table names from the repository's profile data.</summary>
    private static Dictionary<(string Direction, string Name), ushort> OpcodeNumbers(string build)
    {
        var map = new Dictionary<(string, string), ushort>();
        ushort? value = null;
        string? direction = null;
        foreach (var raw in File.ReadLines(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml")))
        {
            var line = raw.Trim();
            if (line.StartsWith("- value: ", StringComparison.Ordinal))
            {
                value = ushort.Parse(line["- value: ".Length..], CultureInfo.InvariantCulture);
                direction = null;
            }
            else if (line.StartsWith("direction: ", StringComparison.Ordinal))
                direction = line["direction: ".Length..];
            else if (line.StartsWith("name: ", StringComparison.Ordinal) && value is { } number && direction is not null)
                map.TryAdd((direction, line["name: ".Length..].Trim()), number);
        }
        return map;
    }

    /// <summary>The launcher serializer kr-1256 sub_10C6A00: three strings, counted i32 vector, two i32 and one string.</summary>
    private static byte[] LauncherLogin(string account)
    {
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

    /// <summary>Checks the accepted S2C gspRepLogin: result 0 and the server-issued session id shape.</summary>
    private static void ValidateLogin(byte[] payload)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Matches(@"\Abff-[1-9][0-9]*\z", reader.ReadWideStringPrefixed());
    }

    /// <summary>Replaces the session id's characters with a fixed marker so one pinned body fits every run.</summary>
    private static string MaskSessionId(byte[] payload)
    {
        var reader = new PacketReader(payload);
        reader.ReadInt32();
        var count = checked((int)reader.ReadUInt32());
        return Convert.ToHexString(payload[..4]) + "SESSION" + Convert.ToHexString(payload[(8 + count * 2)..]);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
