using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Two clients over real sockets invite, answer and leave parties on both families. kr-1256 runs through the mounted
/// modern party binding and the shared party row: A invites B (C2S 110), B gets the prompt with its token (S2C 121)
/// and A the delivery notice (S2C 120); a refusal that echoes the token (C2S 111) answers A; a second invitation is
/// accepted with kind 1 or granted-visit kind 0 (C2S 115) and both get the party list and the join notice, while kind 2 and
/// a refusal with a stale token change nothing. kr-812 runs through the native binding: B accepts A's invitation
/// (C2S 109) and both get the list and the join notice; B leaves (C2S 202) and both are parties of one again; a
/// refused invitation answers A.
/// The full bodies are pinned as (opcode, hex): the modern prompt's server-issued token is the only value that
/// varies, so it is zeroed before its pin.
/// </summary>
public sealed class PartyScenarioTests
{
    /// <summary>Owns the no-op scene follow in membership scenarios whose players are already in the same menu.</summary>
    [Plugin("test.party-menu-scene", Requires = [typeof(IMessageRouter), typeof(IPartyDirectory), typeof(IOnlinePlayers)])]
    public sealed class MenuSceneOwner : IPlugin
    {
        /// <summary>Checks the committed membership and both menu states before accepting the scene request.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var protocol = context.Config.Bind<MenuSceneOptions>().Protocol;
            var parties = context.Services.Get<IPartyDirectory>();
            var online = context.Services.Get<IOnlinePlayers>();
            context.Effect(context.Services.Get<IMessageRouter>().For(context).On<PartySceneFollowRequested>(protocol,
                (session, request, _) =>
                {
                    Assert.True(session.TryFeature<IPlayerSession>(out var member));
                    Assert.Equal(request.LeaderUserNum, parties.PartyOf(member!.Identity.UserNum)!.Leader.UserNum);
                    Assert.Equal(SessionPhase.MainMenu, member.Phase);
                    Assert.True(online.FindByUserNum(request.LeaderUserNum)!.TryFeature<IPlayerSession>(out var leader));
                    Assert.Equal(SessionPhase.MainMenu, leader!.Phase);
                    return ValueTask.CompletedTask;
                }));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Selects the protocol scope of the menu-only scenario scene owner.</summary>
    public sealed class MenuSceneOptions
    {
        /// <summary>Gets or sets the scenario's game message scope.</summary>
        public string Protocol { get; set; } = "";
    }

    /// <summary>Exercises invitation refusal, stale answers and both client-proven party move kinds over TCP.</summary>
    /// <param name="acceptKind">The invitation prompt kind 1 or granted visit move kind 0.</param>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public async Task Kr1256InviteRefuseAndAcceptReachBothPlayers(byte acceptKind)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = limit.Token;
        var port = FreePort();
        var tables = PacketTables.Load(RepositoryPackets.Directory());
        var pins = new List<string>();
        var missing = false;
        void PinBytes(string name, string expected, byte[] payload) =>
            missing |= PartyPins.Capture(pins, name, expected, payload);
        void PinHex(string name, string expected, string hex) =>
            missing |= PartyPins.Capture(pins, name, expected, hex);
        PluginRow Row(string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = plugin, Plugin = plugin, Source = "party-scenario", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var host = new PluginHost(
        [
            Row("core.protocol-registry"), Row("core.session-registry"), Row("core.packet-router"),
            Row("core.message-router"), Row("core.client-profiles"), Row("core.opcode-tables"), Row("core.wire-dialects"),
            Row("persistence.memory"), Row("dialect", TestWireDialects.RowConfig("kr-1256")),
            Row("protocol", new() { ["opcode_table_path"] = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), ["bootstrap_status"] = 1 }),
            // The login burst, main-menu install and the party invitation packets live on the shared message row.
            Row("dialect.messages", TestWireDialects.MessagesConfig(new()
            {
                ["protocol"] = "kr-1256/game", ["login"] = true, ["main_menu_init"] = true, ["inventory"] = true,
                ["party"] = true,
            })),
            Row("lobby", new()
            {
                ["inventory_source"] = "mysql", ["money_source"] = "mysql", ["starting_lucci"] = 1000, ["starting_cash"] = 5000,
                ["online_players"] = true,
            }),
            Row("party", new() { ["protocol"] = "kr-1256/game", ["max_members"] = 12 }),
            Row("test.party-menu-scene", new() { ["protocol"] = "kr-1256/game" }),
            Row("net.tcp", new() { ["listen"] = $"127.0.0.1:{port}", ["protocol"] = "kr-1256/game" }),
        ], PluginCatalog.Discover([Path.Combine(AppContext.BaseDirectory, "plugins")]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(ct);
            await using var a = await ConnectAsync(port, "partya", ct);
            await using var b = await ConnectAsync(port, "partyb", ct);

            // A invites B: B gets the prompt naming A with a token, A the delivery notice naming B.
            int ca = a.Cursor, cb = b.Cursor;
            await a.SendAsync(110, [.. Text("partyb"), .. Word(1)], ct, 0);
            var prompt = await b.WaitAsync(cb, 121, TimeSpan.FromSeconds(10), ct);
            var first = Token(prompt.Payload, "partya");
            PinHex("prompt", PartyPins.Kr1256Prompt, PartyPins.MaskedToken(prompt.Payload, "partya"));
            PinBytes("delivered", PartyPins.Kr1256Delivered, (await a.WaitAsync(ca, 120, TimeSpan.FromSeconds(10), ct)).Payload);
            Assert.Single(a.Since(ca));

            // B refuses with the token: A is told the invitation was refused.
            ca = a.Cursor;
            await b.SendAsync(111, Word((int)first), ct, 0);
            PinBytes("refused", PartyPins.Kr1256Refused, (await a.WaitAsync(ca, 119, TimeSpan.FromSeconds(10), ct)).Payload);
            Assert.Single(a.Since(ca));

            // A invites again: the token is new. A refusal with the old token and kind 2 do nothing.
            ca = a.Cursor; cb = b.Cursor;
            await a.SendAsync(110, [.. Text("partyb"), .. Word(0)], ct, 0);
            var secondPrompt = await b.WaitAsync(cb, 121, TimeSpan.FromSeconds(10), ct);
            var second = Token(secondPrompt.Payload, "partya");
            Assert.NotEqual(first, second);
            PinHex("prompt2", PartyPins.Kr1256Prompt, PartyPins.MaskedToken(secondPrompt.Payload, "partya"));
            PinBytes("delivered2", PartyPins.Kr1256Delivered, (await a.WaitAsync(ca, 120, TimeSpan.FromSeconds(10), ct)).Payload);
            ca = a.Cursor; cb = b.Cursor;
            await b.SendAsync(111, Word((int)first), ct, 0);
            await b.SendAsync(115, Accept(2), ct, 0);
            await Task.Delay(500, ct);
            Assert.Empty(a.Since(ca));
            Assert.Empty(b.Since(cb));

            // kr-1256 sub_9A8DF0 sends kind 1; kr-1256 sub_9A9710 sends kind 0 for a granted visit.
            await b.SendAsync(115, Accept(acceptKind), ct, 0);
            foreach (var (client, from, who) in new[] { (a, ca, "a"), (b, cb, "b") })
            {
                var list = await client.WaitAsync(from, 201, TimeSpan.FromSeconds(10), ct);
                var members = Assert.IsAssignableFrom<IReadOnlyList<object?>>(PacketCodec.Read(tables, "kr-1256", "SoloPartySnapshot", list.Payload)["members"]);
                Assert.Equal(["partya", "partyb"], members.Select(row => (string)((IReadOnlyDictionary<string, object?>)row!)["nickname"]!));
                await client.WaitAsync(from, 206, TimeSpan.FromSeconds(10), ct);
                PinHex("accept-" + who, PartyPins.Kr1256Accepted, PartyPins.Frames(client.Since(from)));
            }
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
        PartyPins.FailWhenMissing(pins, missing);
    }

    /// <summary>
    /// Two kr-812 clients over real sockets through the native party binding: A invites B, B gets the native prompt
    /// and accepts, both get the party list with A first and the join notice; B leaves and both are parties of one
    /// again; a refused invitation answers A. The full invitation bodies are pinned as hex so the shared binding's
    /// legacy bytes stay put while the modern row moves onto it.
    /// </summary>
    [Fact]
    public async Task Kr812InviteAcceptAndLeaveReachBothPlayers()
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
            // The protocol row resolves the bootstrap frames by name, so the table states them.
            (1, "c2s", "gcpFirstConnect"), (2, "s2c", "gspRepGamePatch"),
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
            Row("protocol", new() { ["opcode_table_path"] = tablePath, ["bootstrap_status"] = 1, ["patch_request_name"] = "gcpFirstConnect" }),
            Row("dialect.messages", new() { ["protocol"] = "kr-812/game", ["inventory"] = true, ["party"] = true, ["packets_directory"] = RepositoryPackets.Directory() }),
            Row("lobby", new() { ["protocol"] = "kr-812/game", ["auto_create_players"] = true, ["online_players"] = true }),
            Row("party", new() { ["protocol"] = "kr-812/game" }),
            Row("test.party-menu-scene", new() { ["protocol"] = "kr-812/game" }),
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

    // The prompt: wstr inviter, u32 token, u32 ip, u16 port, two words (26 bytes for a 6-character name).
    private static uint Token(byte[] payload, string inviter)
    {
        Assert.Equal(Text(inviter), payload[..(4 + inviter.Length * 2)]);
        Assert.Equal(4 + inviter.Length * 2 + 18, payload.Length);
        var token = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4 + inviter.Length * 2));
        Assert.NotEqual(0u, token);
        return token;
    }

    private static byte[] Accept(byte kind) => [.. Text("partya"), .. Word(0), 0, 0, kind];

    private static async Task<SimClient> ConnectAsync(int port, string name, CancellationToken ct)
    {
        var client = await SimClient.ConnectAsync(name, new DnsEndPoint("127.0.0.1", port),
            TestWireDialects.Get("kr-1256"), ushort.MaxValue, ct);
        var handshake = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(handshake, 1256);
        await client.SendAsync(1, handshake, ct, 0);
        await client.WaitAsync(0, 2, TimeSpan.FromSeconds(5), ct);
        var cursor = client.Cursor;
        await client.SendAsync(3, Login(name), ct, 0);
        await client.WaitAsync(cursor, 163, TimeSpan.FromSeconds(10), ct);
        return client;
    }

    private static byte[] Login(string name)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed(name);
        writer.WriteWideStringPrefixed(string.Empty); writer.WriteWideStringPrefixed(string.Empty);
        writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(string.Empty);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Word(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Text(string text) => [.. Word(text.Length), .. Encoding.Unicode.GetBytes(text)];

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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// The pre-move party bodies, captured from the current code before the shared adapter takes the modern row over.
/// "TODO" marks a value still to capture; the tests dump every captured value in one failure until they are all in.
/// </summary>
internal static class PartyPins
{
    internal const string Kr1256Prompt = "06000000700061007200740079006100000000000000000000000000000000000000";
    internal const string Kr1256Delivered = "06000000700061007200740079006200";
    internal const string Kr1256Refused = "060000007000610072007400790062001A000000";
    internal const string Kr1256Accepted = "00C9:020000000200000001000000060000007000610072007400790061000000000000000000000000000000000002000000060000007000610072007400790062000000000000000000000000000000000000000000;00CE:06000000700061007200740079006200";

    internal const string LegacyPrompt = "010000000600000070006100720074007900610000000000000000000000";
    internal const string LegacySent = "0000000006000000700061007200740079006200";
    internal const string LegacyList = "02000000020000000100000006000000700061007200740079006100000000000000000001000000020000000600000070006100720074007900620000000000000000000100000000000000";
    internal const string LegacyJoined = "06000000700061007200740079006200";
    internal const string LegacyLeaveConfirm = "";
    internal const string LegacyLeaveNotice = "06000000700061007200740079006200";
    internal const string LegacySoloA = "0200000001000000010000000600000070006100720074007900610000000000000000000100000000000000";
    internal const string LegacySoloB = "0200000001000000020000000600000070006100720074007900620000000000000000000100000000000000";
    internal const string LegacyRefused = "0E00000006000000700061007200740079006200";

    internal static bool Capture(List<string> pins, string name, string expected, byte[] payload) =>
        Capture(pins, name, expected, Convert.ToHexString(payload));

    internal static bool Capture(List<string> pins, string name, string expected, string actual)
    {
        pins.Add(name + "=" + actual);
        if (expected == "TODO")
            return true;
        Assert.True(expected == actual, $"{name}: expected {expected} but was {actual}");
        return false;
    }

    internal static void FailWhenMissing(List<string> pins, bool missing)
    {
        if (missing)
            Assert.Fail(string.Join("\n", pins));
    }

    /// <summary>The prompt with its server-issued token zeroed: the only bytes that change between runs.</summary>
    internal static string MaskedToken(byte[] payload, string inviter)
    {
        var copy = payload.ToArray();
        Array.Clear(copy, 4 + inviter.Length * 2, sizeof(uint));
        return Convert.ToHexString(copy);
    }

    /// <summary>One or more frames as "opcode:payload" pairs, so an extra or missing notice fails the pin.</summary>
    internal static string Frames(IReadOnlyList<SimFrame> frames) =>
        string.Join(";", frames.Select(frame => $"{frame.Opcode:X4}:{Convert.ToHexString(frame.Payload)}"));
}
