using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Drives kr-1256 native character creation end to end over TCP through the real host rows: the launcher login
/// opens the creation stage (S2C 12 empty, no login burst), the name check answers result 0 with the echo
/// (S2C 13), the client's five-piece create body is accepted (S2C 14 four bytes) and the empty
/// login-after-create frame runs the login burst carrying the created nickname and selected character item.
/// A login-after frame with no creation open closes the connection cleanly instead of faulting the row.
/// Readers: kr-1256 sub_9D2FF0 case 12 (open), kr-1256 sub_5CCC60 (C2S 31), kr-1256 sub_9A4E90 (S2C 13 and the
/// C2S 32 five-piece body) and kr-1256 sub_9A46A0 (S2C 14 and the empty C2S 33).
/// </summary>
public sealed class NativeCreationScenarioTests
{
    private static readonly TimeSpan Reply = TimeSpan.FromSeconds(10);

    // The frame order of a kr-1256 login burst (packets/builds.yaml login.burst): reply, user state, wallet and
    // room counters, the empty gate table, the solo party, the empty option sync, the bag family, the kind-1
    // main-menu install and the first-move-channel notice.
    private static readonly ushort[] Burst =
        [3, 105, 106, 108, 17, 201, 1557, 802, 803, 808, 810, 117, 163];

    /// <summary>The native creation exchange, then the login burst of the created character.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(25)]
    [InlineData(27)]
    public async Task Kr1256CreationOpensTheStageThenLogsInTheCreatedCharacter(int characterType)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var port = FreePort();
        var tables = PacketTables.Load(RepositoryPackets.Directory());
        var host = Host(port);
        try
        {
            await host.StartAsync(ct);
            await using var client = await ConnectAsync(port, ct);
            var cursor = await LoginAsync(client, "creator", ct);

            // The account has no character: the shared binding sends the empty creation-opened frame instead of
            // the login reply (kr-1256 sub_9D2FF0 case 12 reads no payload field).
            var opened = await client.WaitAsync(cursor, 12, Reply, ct);
            var opening = client.Since(cursor);
            Assert.Equal(new ushort[] { 1500, 12 }, opening.Select(frame => frame.Opcode));
            CheckCreationContent(tables, opening[0].Payload);
            Assert.Empty(opened.Payload);
            Assert.DoesNotContain(client.Since(cursor), frame => frame.Opcode == 3);

            // C2S 31 name check: result 0 and the echoed name (kr-1256 sub_9A4E90).
            await client.SendAsync(31, Wide("Ab"), ct, 0);
            Assert.Equal(Convert.FromHexString("000000000200000041006200"),
                (await client.WaitAsync(cursor, 13, Reply, ct)).Payload);

            // C2S 32 create submit, the client's five-piece body: prefixed name, the selected record's two words,
            // an empty wstring and one zero word (24 bytes for "Ab").
            var create = new ArrayBufferWriter<byte>();
            var writer = new PacketWriter(create);
            writer.WriteWideStringPrefixed("Ab");
            writer.WriteInt32(characterType);
            writer.WriteInt32(4);
            writer.WriteWideStringPrefixed(string.Empty);
            writer.WriteInt32(0);
            Assert.Equal(24, create.WrittenCount);
            await client.SendAsync(32, create.WrittenSpan.ToArray(), ct, 0);
            Assert.Equal(new byte[4], (await client.WaitAsync(cursor, 14, Reply, ct)).Payload);
            Assert.DoesNotContain(client.Since(cursor), frame => frame.Opcode == 3);

            // C2S 33 empty: the account now has a character, so the login burst runs (kr-1256 sub_9A46A0).
            cursor = client.Cursor;
            await client.SendAsync(33, [], ct, 0);
            await client.WaitAsync(cursor, 163, Reply, ct);
            var burst = client.Since(cursor);
            Assert.Equal(Burst, burst.Select(frame => frame.Opcode));
            // Pin inflated bag lengths: the selected character changes the compressed stream's size.
            Assert.Equal(new[] { 63, 486, 24, 14, 4, 68, 0, 504, 512, 16, 4, 628, 0 },
                burst.Select((frame, index) => index is 7 or 8 ? Inflate(frame.Payload).Length : frame.Payload.Length));

            var userNumber = CheckLogin(burst[0].Payload, "Ab", characterType);
            Assert.Equal(new byte[24], burst[2].Payload); // user money stat: all-zero balances
            Assert.Equal(new byte[14], burst[3].Payload); // pcroom stat
            Assert.Equal(new byte[4], burst[4].Payload);  // empty gate table
            Assert.Empty(burst[6].Payload);               // empty option sync
            Assert.Equal(new byte[4], Inflate(burst[9].Payload)); // articles: an empty counted list
            Assert.Equal(new byte[4], burst[10].Payload);         // cash inventory: an empty counted list
            Assert.Empty(burst[12].Payload);              // first-move-channel notice

            CheckPartySnapshot(tables, burst[5].Payload, userNumber, "Ab", 300000 + characterType);
            CheckBag(tables, "InventoryUpdate", burst[7].Payload, 504, 0x40F869F000000000L, 300000 + characterType);
            CheckBag(tables, "InventorySlots", burst[8].Payload, 512, 0x40F86A0000000000L, 300000 + characterType);
            CheckMainMenuInstall(tables, burst[11].Payload, userNumber, "Ab", 300000 + characterType);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// kr-1256 sends C2S 33 after any S2C 14 result while the creation stage is live. Without a created
    /// character the lobby refuses: it logs the reason, closes the connection with a clear reason, and the row
    /// keeps serving other sessions.
    /// </summary>
    [Fact]
    public async Task Kr1256LoginAfterCreateWithoutACreationClosesTheConnectionCleanly()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var port = FreePort();
        var host = Host(port);
        try
        {
            await host.StartAsync(ct);
            await using var client = await ConnectAsync(port, ct);
            var cursor = await LoginAsync(client, "refused", ct);
            Assert.Empty((await client.WaitAsync(cursor, 12, Reply, ct)).Payload);

            // No name check and no create submit: the session has nothing to finish, so the server closes it.
            var afterOpen = client.Cursor;
            await client.SendAsync(33, [], ct, 0);
            await Assert.ThrowsAsync<IOException>(() => client.WaitAsync(afterOpen, 3, Reply, ct));
            Assert.Empty(client.Since(afterOpen));

            // The refusal is an error path on one session: the row still serves a new connection.
            await using var survivor = await ConnectAsync(port, ct);
            Assert.Equal(new byte[8], (await survivor.WaitAsync(0, 2, Reply, ct)).Payload);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    // S2C 3 gspRepLogin in the kr-1256 table form: result 0, a server-issued session id, the created user number
    // and nickname, the selected character type, channel 99, a zero server time and
    // national result 1. The optional 0x3A block is absent and marked by the field's default tag byte 0.
    private static int CheckLogin(byte[] payload, string nickname, int characterType)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Matches(@"\Abff-[1-9][0-9]*\z", reader.ReadWideStringPrefixed());
        var userNumber = reader.ReadInt32();
        Assert.True(userNumber > 0);
        Assert.Equal(nickname, reader.ReadWideStringPrefixed());
        Assert.Equal(characterType, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());  // unknown_64
        Assert.Equal(99, reader.ReadInt32()); // channel_id arms the room-list poll
        Assert.Equal(0, reader.ReadInt32());  // unknown_72
        reader.ReadDouble();                  // server_time, not pinned
        Assert.Equal(0, reader.ReadInt32());  // unknown_88
        Assert.Equal(1, reader.ReadInt32());  // national_result
        Assert.Equal(0, reader.ReadByte());   // absent 0x3A block: field-default tag
        Assert.Equal(0, reader.Remaining);
        return userNumber;
    }

    private static void CheckCreationContent(PacketTables tables, byte[] payload)
    {
        Assert.Equal(1 << 14, BinaryPrimitives.ReadInt32LittleEndian(payload));
        var plain = Inflate(payload[4..]);
        Assert.Equal(76, plain.Length);
        var content = PacketCodec.Read(tables, "kr-1256", "CreationContent", plain);
        Assert.Equal("Ui/DlgCreateUser3.ui", content["ui_path"]);
        Assert.Equal(new object?[] { 10, 19, 25, 27 }, Assert.IsType<object?[]>(content["character_types"]));
        Assert.Empty(Assert.IsType<object?[]>(content["preview_overrides"]));
    }

    // S2C 201 solo party snapshot: state 2, the created member and the profile entry carrying the equipped
    // character item (kr-1256 sub_905D20 reads the member; kr-1256 sub_10C4290 the profile entry).
    private static void CheckPartySnapshot(PacketTables tables, byte[] payload, int userNumber, string nickname,
        int characterItemId)
    {
        var snapshot = PacketCodec.Read(tables, "kr-1256", "SoloPartySnapshot", payload);
        Assert.Equal(2, snapshot["state"]);
        var members = Assert.IsAssignableFrom<IReadOnlyList<object?>>(snapshot["members"]);
        var member = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(members));
        Assert.Equal(userNumber, member["user_number"]);
        Assert.Equal(nickname, member["nickname"]);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<object?>>(snapshot["profile_entries"]);
        var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(entries));
        Assert.Equal(userNumber, profile["user_number"]);
        Assert.Equal(characterItemId, profile["character_id"]);
    }

    // The login bag family: the whole character bag (S2C 802, InventoryUpdate) and the equipped-container
    // publication (S2C 803, InventorySlots with its extra empty article container and removed-id list). Both are
    // the bag family's compressed envelope over the kr-1256 slot record; the created character is a worn row.
    private static void CheckBag(PacketTables tables, string structure, byte[] envelope, int plainLength,
        long marker, int characterItemId)
    {
        var plain = Inflate(envelope);
        Assert.Equal(plainLength, plain.Length);
        var slots = Assert.IsAssignableFrom<IReadOnlyList<object?>>(
            PacketCodec.Read(tables, "kr-1256", structure, plain)["slots"]);
        Assert.Equal(20, slots.Count); // The original starter bag plus the selected character.
        var character = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(slots,
            slot => (int)((IReadOnlyDictionary<string, object?>)slot!)["item_id"]! == characterItemId));
        Assert.Equal(1, (byte)character["flags"]! & 1); // worn
        Assert.Equal(99999, character["quantity"]);
        Assert.Equal(marker, character["unknown_8"]);
        Assert.Equal(string.Empty, character["unknown_36"]);
    }

    // S2C 117 main-menu install (kr-1256 sub_10D4520): one occupied seat names the local player and both
    // profile maps carry the created character item (UserSlot@kr-1256 has no character_type field; the profile
    // record's character_id is the item the avatar gate reads).
    private static void CheckMainMenuInstall(PacketTables tables, byte[] payload, int userNumber, string nickname,
        int characterItemId)
    {
        var install = PacketCodec.Read(tables, "kr-1256", "gspGotoRoom", payload);
        var seats = Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["user_slots"]);
        var seat = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(seats));
        Assert.Equal(true, seat["occupied"]);
        Assert.Equal((byte)2, seat["team"]);
        Assert.Equal(userNumber, seat["user_number"]);
        Assert.Equal(nickname, seat["nickname"]);
        foreach (var field in new[] { "profile_entries", "unknown_588" })
        {
            var entries = Assert.IsAssignableFrom<IReadOnlyList<object?>>(install[field]);
            var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(entries));
            Assert.Equal(userNumber, profile["user_number"]);
            Assert.Equal(characterItemId, profile["character_id"]);
        }
    }

    // The bag family's envelope wraps the plain body in one zlib stream behind a u32 compressed length
    // (kr-1256 sub_FA5520); the header counts the compressed input bytes of this frame.
    private static byte[] Inflate(byte[] envelope)
    {
        Assert.True(envelope.Length > 4);
        Assert.Equal(envelope.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(envelope));
        using var source = new MemoryStream(envelope, 4, envelope.Length - 4);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        zlib.CopyTo(target);
        return target.ToArray();
    }

    private static async Task<SimClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync("creation", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
        await client.SendAsync(1, handshake, ct, 0);
        Assert.Equal(new byte[8], (await client.WaitAsync(0, 2, Reply, ct)).Payload);
        return client;
    }

    private static async Task<int> LoginAsync(SimClient client, string account, CancellationToken ct)
    {
        var cursor = client.Cursor;
        await client.SendAsync(3, LauncherLogin(account), ct, 0);
        return cursor;
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

    private static byte[] Wide(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        new PacketWriter(buffer).WriteWideStringPrefixed(text);
        return buffer.WrittenSpan.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static PluginHost Host(int port)
    {
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "native-creation-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        return new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ["bootstrap_status"] = 1 }),
            // The shared row owns the login burst, the bag publication and native creation; the kr-1256 table
            // names its open frame gspNotifyCreateUserInfo and its login-after request Unknown33.
            Row("dialect.messages", TestWireDialects.MessagesConfig(new()
            {
                ["protocol"] = "kr-1256/game", ["login"] = true, ["main_menu_init"] = true, ["inventory"] = true,
                ["character_creation"] = true,
            })),
            // A fresh account gets the native creation stage: no auto-created player, store-backed inventory
            // (the validation requires it) and the kr-1256 starter set the row already defaults to.
            Row("lobby", new()
            {
                ["character_creation"] = true, ["auto_create_players"] = false,
                ["build_values"] = true,
                ["packets_directory"] = RepositoryPackets.Directory(),
                ["inventory_source"] = "mysql", ["money_source"] = "mysql",
            }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
    }
}
