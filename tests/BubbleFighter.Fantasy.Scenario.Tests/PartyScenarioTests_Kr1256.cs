using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// Two kr-1256 clients over real sockets through the mounted modern party binding and the shared party row: A invites B
/// (C2S 110), B gets the prompt with its token (S2C 121) and A the delivery notice (S2C 120); a refusal that echoes the
/// token (C2S 111) answers A; a second invitation is accepted with kind 1 (C2S 115) and both get the party list and the
/// join notice, while accepts of kinds 0 and 2 and a refusal with a stale token change nothing.
/// The full bodies are pinned as (opcode, hex): the prompt's server-issued token is the only value that varies, so it
/// is zeroed before its pin.
/// </summary>
public sealed class PartyScenarioTests_Kr1256
{
    [Fact]
    public async Task InviteRefuseAndAcceptReachBothPlayers()
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
            Row("dialect.messages", TestWireDialects.ModernMessagesConfig(new()
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

            // A invites again: the token is new. A refusal with the old token and accepts of kinds 0 and 2 do nothing.
            ca = a.Cursor; cb = b.Cursor;
            await a.SendAsync(110, [.. Text("partyb"), .. Word(0)], ct, 0);
            var secondPrompt = await b.WaitAsync(cb, 121, TimeSpan.FromSeconds(10), ct);
            var second = Token(secondPrompt.Payload, "partya");
            Assert.NotEqual(first, second);
            PinHex("prompt2", PartyPins.Kr1256Prompt, PartyPins.MaskedToken(secondPrompt.Payload, "partya"));
            PinBytes("delivered2", PartyPins.Kr1256Delivered, (await a.WaitAsync(ca, 120, TimeSpan.FromSeconds(10), ct)).Payload);
            ca = a.Cursor; cb = b.Cursor;
            await b.SendAsync(111, Word((int)first), ct, 0);
            await b.SendAsync(115, Accept(0), ct, 0);
            await b.SendAsync(115, Accept(2), ct, 0);
            await Task.Delay(500, ct);
            Assert.Empty(a.Since(ca));
            Assert.Empty(b.Since(cb));

            // The accept of the prompt (kind 1) joins B to A: both get the list with A first, then the join notice.
            await b.SendAsync(115, Accept(1), ct, 0);
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
    internal const string LegacyList = "02000000020000000100000006000000700061007200740079006100000000000000000000000000020000000600000070006100720074007900620000000000000000000000000000000000";
    internal const string LegacyJoined = "06000000700061007200740079006200";
    internal const string LegacyLeaveConfirm = "";
    internal const string LegacyLeaveNotice = "06000000700061007200740079006200";
    internal const string LegacySoloA = "0200000001000000010000000600000070006100720074007900610000000000000000000000000000000000";
    internal const string LegacySoloB = "0200000001000000020000000600000070006100720074007900620000000000000000000000000000000000";
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
