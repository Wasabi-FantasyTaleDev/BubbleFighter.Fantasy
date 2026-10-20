using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Two kr-812 clients over real sockets: A invites B, B gets the native prompt and accepts, both get the party list
/// with A first and the join notice; B leaves and both are parties of one again; a refused invitation answers A.
/// The full invitation bodies are pinned as hex so the shared binding's legacy bytes stay put while the modern row
/// moves onto it.
/// </summary>
public sealed class PartyScenarioTests_Legacy
{
    [Fact]
    public async Task InviteAcceptAndLeaveReachBothPlayers()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var directory = TestTempDirectories.ForTest("bff-party-scenario");
        var tablePath = Path.Combine(directory, "opcodes.yaml");
        var pins = new List<string>();
        var missing = false;
        void Pin(string name, string expected, byte[] payload) =>
            missing |= PartyPins.Capture(pins, name, expected, payload);
        var entries = new (int Value, string Direction, string Name)[]
        {
            (3, "c2s", "gcpReqLogin"), (3, "s2c", "gspRepLogin"), (105, "s2c", "gspUserStat"),
            (108, "c2s", "gcpReqInvite"), (109, "c2s", "gcpReqMoveToInviter"), (111, "c2s", "gcpReqMoveToFriendAccept"),
            (202, "c2s", "gcpReqLeaveParty"), (302, "c2s", "gcpReqKickFromParty"), (303, "c2s", "gcpReqChangePartyLeader"),
            (111, "s2c", "gspRepInvite"), (112, "s2c", "gspNotifyInvite"), (201, "s2c", "gspUpdateParty"),
            (203, "s2c", "gspUpdatePartyLeader"), (204, "s2c", "gspNotifyPartyLeaveSuccess"), (205, "s2c", "gspNotifyMemberLeave"),
            (206, "s2c", "gspNotifyNewMemberJoin"), (302, "s2c", "gspRepKickFromParty"), (303, "s2c", "gspRepChangePartyLeader"),
            (304, "s2c", "gspNotifyKickFromParty"), (307, "s2c", "gspNotifyKickMember"),
            // The shared login row publishes the bag on the login burst; the row's inventory: true states it.
            (801, "s2c", "gspInventoryUpdateAllInventorySlot"), (803, "s2c", "gspInventoryUpdateInventorySlots"),
        };
        await File.WriteAllTextAsync(tablePath, "profile: kr-812\nopcodes:\n" + string.Concat(entries.Select(e =>
            $"  - value: {e.Value}\n    direction: {e.Direction}\n    name: {e.Name}\n    name_source: label@kr-812\n    evidence: verified\n")), ct);
        var port = FreePort();
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "party-scenario", ClientBuild = new("kr", 812, 458), Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-812")),
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1 }),
            Row("dialect.messages", new() { ["protocol"] = "kr-812/game", ["inventory"] = true, ["party"] = true, ["packets_directory"] = RepositoryPackets.Directory() }),
            Row("lobby", new() { ["protocol"] = "kr-812/game", ["auto_create_players"] = true, ["online_players"] = true }),
            Row("party", new() { ["protocol"] = "kr-812/game" }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-812/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            var a = await ConnectAsync(port, ct);
            await LoginAsync(a, "partya", ct);
            var b = await ConnectAsync(port, ct);
            await LoginAsync(b, "partyb", ct);

            // A invites B: B gets the prompt naming A, A the "sent" answer naming B.
            int ca = a.Cursor, cb = b.Cursor;
            await a.SendAsync(108, [.. Word(1), .. Text("partyb")], ct, 0);
            var prompt = await b.WaitAsync(cb, 112, TimeSpan.FromSeconds(10), ct);
            Assert.Equal([.. Word(1), .. Text("partya"), .. Word(0), 0, 0, .. Word(0)], prompt.Payload);
            Pin("prompt", PartyPins.LegacyPrompt, prompt.Payload);
            var sent = await a.WaitAsync(ca, 111, TimeSpan.FromSeconds(10), ct);
            Assert.Equal([.. Word(0), .. Text("partyb")], sent.Payload);
            Pin("sent", PartyPins.LegacySent, sent.Payload);

            // B accepts: both get the list (A first) and the join notice naming B.
            ca = a.Cursor; cb = b.Cursor;
            await b.SendAsync(109, [.. Word(1), .. Text("partya"), .. Word(0), 0, 0], ct, 0);
            foreach (var (client, from, who) in new[] { (a, ca, "a"), (b, cb, "b") })
            {
                var list = await client.WaitAsync(from, 201, TimeSpan.FromSeconds(10), ct);
                Assert.Equal(["partya", "partyb"], Members(list.Payload));
                Pin("list-" + who, PartyPins.LegacyList, list.Payload);
                var joined = await client.WaitAsync(from, 206, TimeSpan.FromSeconds(10), ct);
                Assert.Equal(Text("partyb"), joined.Payload);
                Pin("joined-" + who, PartyPins.LegacyJoined, joined.Payload);
            }

            // B leaves: B gets its confirmation and a party of one; A the leave notice and a party of one.
            ca = a.Cursor; cb = b.Cursor;
            await b.SendAsync(202, [], ct, 0);
            Pin("leave-confirm", PartyPins.LegacyLeaveConfirm,
                (await b.WaitAsync(cb, 204, TimeSpan.FromSeconds(10), ct)).Payload);
            var soloB = await b.WaitAsync(cb, 201, TimeSpan.FromSeconds(10), ct);
            Assert.Equal(["partyb"], Members(soloB.Payload));
            Pin("solo-b", PartyPins.LegacySoloB, soloB.Payload);
            Pin("leave-notice", PartyPins.LegacyLeaveNotice,
                (await a.WaitAsync(ca, 205, TimeSpan.FromSeconds(10), ct)).Payload);
            var soloA = await a.WaitAsync(ca, 201, TimeSpan.FromSeconds(10), ct);
            Assert.Equal(["partya"], Members(soloA.Payload));
            Pin("solo-a", PartyPins.LegacySoloA, soloA.Payload);

            // A invites again and B refuses (answer 3): A is told with the code that clears its pending row.
            ca = a.Cursor; cb = b.Cursor;
            await a.SendAsync(108, [.. Word(1), .. Text("partyb")], ct, 0);
            await b.WaitAsync(cb, 112, TimeSpan.FromSeconds(10), ct);
            await a.WaitAsync(ca, 111, TimeSpan.FromSeconds(10), ct);
            ca = a.Cursor;
            await b.SendAsync(111, [.. Text("partya"), .. Word(3)], ct, 0);
            var refused = await a.WaitAsync(ca, 111, TimeSpan.FromSeconds(10), ct);
            Assert.Equal([.. Word(14), .. Text("partyb")], refused.Payload);
            Pin("refused", PartyPins.LegacyRefused, refused.Payload);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
        PartyPins.FailWhenMissing(pins, missing);
    }

    private static string[] Members(byte[] payload)
    {
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(payload));
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        var names = new string[count];
        var at = 8;
        for (var i = 0; i < count; i++)
        {
            at += 4;
            var length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));
            names[i] = Encoding.Unicode.GetString(payload, at + 4, length * 2);
            at += 4 + length * 2 + 12;
        }
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at)));
        return names;
    }

    private static async Task<SimClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync("party", new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-812"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 812);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        return client;
    }

    private static async Task LoginAsync(SimClient client, string name, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(name);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        var cursor = client.Cursor;
        await client.SendAsync(3, buffer.WrittenSpan.ToArray(), ct, 0);
        await client.WaitAsync(cursor, 105, TimeSpan.FromSeconds(10), ct);
    }

    private static byte[] Word(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Text(string text) => [.. Word(text.Length), .. Encoding.Unicode.GetBytes(text)];

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
