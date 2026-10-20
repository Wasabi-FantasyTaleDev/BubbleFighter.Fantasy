using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Runs one request script for two clients against the raw kr-1256 room path and against the typed path
/// (<c>room_binding: typed</c>, the kr-1256 default since profiles/kr-1256/raw.patch.yaml became the opt-out), both with the live in-game
/// options, and compares what each client receives step by step. The typed path is room-only, so the script
/// stops before a round could start.
/// </summary>
public sealed class TypedRoomParityScenarioTests_Kr1256 : IClassFixture<ParityRun>
{
    // Structures whose seat-occupancy bytes the raw writer fills as 0x0101+slot style words and the packet
    // tables write as canonical 1 (kr-1256 sub_1143710); those frames are compared after a read/write round trip.
    private static readonly Dictionary<ushort, string> Canonical = new()
    {
        [117] = "gspGotoRoom", [503] = "RoomSeatUpdate", [511] = "RoomSeatLeave",
        // The session restatement carries the same GameSetting as the install; it is compared decoded so the
        // all-random presentation applies to it too.
        [501] = "SessionSetting",
    };

    /// <summary>The comparisons that currently differ, with the observed difference; each has its own skipped test.</summary>
    internal static readonly Dictionary<(string Step, string Client), string> Known = new()
    {
        [("B 517 probe", "B")] = "S2C 518 is checked against the client's reader in JoinProbeNamesTheProbingSeat.",
    };

    private readonly ParityRun _run;
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class around the shared script run.</summary>
    /// <param name="run">The packets of both runs.</param>
    /// <param name="output">Receives the per-step packet listing.</param>
    public TypedRoomParityScenarioTests_Kr1256(ParityRun run, ITestOutputHelper output)
    {
        _run = run;
        _output = output;
    }

    /// <summary>Every (step, client) pair compared by <see cref="StepMatches"/>.</summary>
    /// <returns>Theory rows.</returns>
    public static IEnumerable<object[]> Comparisons() =>
        ParityRun.Steps.SelectMany(step => new[] { "A", "B" }.Select(client => new object[] { step, client }))
            .Where(row => !Known.ContainsKey(((string)row[0], (string)row[1])));

    /// <summary>One client's packets of one step are the same on the raw and the typed path.</summary>
    /// <param name="step">Script step name.</param>
    /// <param name="client">Client name, A (host) or B (guest).</param>
    [Theory]
    [MemberData(nameof(Comparisons))]
    public void StepMatches(string step, string client)
    {
        var difference = Compare(_run.Raw[step][client], _run.Typed[step][client]);
        Assert.True(difference is null, $"{step} / client {client}: {difference}");
    }

    /// <summary>
    /// The probe step matches except S2C 518, where the client's reader decides: kr-1256 sub_9D9930 case 0x206
    /// reads three words and kr-1256 sub_978F20 uses the first as the seat index of its ping history. The raw path
    /// writes four words led by the join token, so its first word is no seat; the typed path names the prober's seat.
    /// </summary>
    [Fact]
    public void JoinProbeNamesTheProbingSeat()
    {
        var raw = _run.Raw["B 517 probe"]["B"];
        var typed = _run.Typed["B 517 probe"]["B"];
        Assert.Null(Compare([.. raw.Where(frame => frame.Opcode != 518)], [.. typed.Where(frame => frame.Opcode != 518)]));
        var reply = Assert.Single(typed, frame => frame.Opcode == 518);
        var seat = (int)PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomSeatUpdate",
            _run.Typed["B 403 join"]["B"].First(frame => frame.Opcode == 503).Body)["slot"]!;
        Assert.Equal(12, reply.Body.Length);
        Assert.Equal(seat, (int)PacketCodec.Read(ParityRun.Tables, "kr-1256", "gspRepPingUser", reply.Body)["slot"]!);
    }

    /// <summary>Lists every difference of the whole script, so a failing run shows all steps at once.</summary>
    [Fact]
    public void ListsExactlyTheKnownDifferences()
    {
        var found = new List<string>();
        foreach (var step in ParityRun.Steps)
            foreach (var client in new[] { "A", "B" })
            {
                _output.WriteLine($"{step} / {client}: raw [{Describe(_run.Raw[step][client])}] typed [{Describe(_run.Typed[step][client])}]");
                if (Compare(_run.Raw[step][client], _run.Typed[step][client]) is { } difference)
                    found.Add($"{step} / client {client}: {difference}");
            }
        Assert.Equal(Known.Count, found.Count);
        Assert.All(Known.Keys, known => Assert.Contains(found, line => line.StartsWith($"{known.Step} / client {known.Client}:", StringComparison.Ordinal)));
    }

    /// <summary>A plaza enter sent before the login finished is ignored, with the same silence on both paths.</summary>
    [Fact]
    public async Task PlazaEnterBeforeTheLoginBurstEndsGetsNoReply()
    {
        var raw = await EarlyPlazaAsync(typed: false);
        var typed = await EarlyPlazaAsync(typed: true);
        _output.WriteLine($"raw [{Describe(raw)}] typed [{Describe(typed)}]");
        Assert.DoesNotContain(raw, f => f.Opcode == 116);
        Assert.DoesNotContain(typed, f => f.Opcode == 116);
        Assert.Null(Compare(raw, typed));
    }

    // The request goes out after the version exchange and before the login, so the session has no player yet and the
    // gate is deterministic; a request racing the burst itself would depend on when the lobby row moves the phase on.
    private static async Task<List<ParityFrame>> EarlyPlazaAsync(bool typed)
    {
        await using var server = await ParityRun.StartAsync(typed);
        var a = await server.ConnectAsync("scenEarly");
        await a.SendReference(1);
        await a.WaitFor(2);
        var early = a.Cursor;
        await a.Send(106, []);
        await ParityRun.Settle(server.Ct, a);
        Assert.Equal(early, a.Cursor);
        var from = a.Cursor;
        await a.Send(3, SimActor.LauncherLogin(a.Name));
        await a.WaitFor(3, from);
        await a.SendReference(1500, last: true);
        await a.SendReference(521);
        await a.SendReference(41);
        await a.WaitFor(117, from);
        await ParityRun.Settle(server.Ct, a);
        return a.Since(from).Select(f => new ParityFrame(f.Opcode, f.Payload)).ToList();
    }

    /// <summary>Compares two packet lists; null when they are the same after the accepted normalizations.</summary>
    internal static string? Compare(IReadOnlyList<ParityFrame> raw, IReadOnlyList<ParityFrame> typed)
    {
        if (!raw.Select(f => f.Opcode).SequenceEqual(typed.Select(f => f.Opcode)))
            return $"opcode order differs: raw [{Describe(raw)}] typed [{Describe(typed)}]";
        var differences = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var (a, b) = (raw[i].Body, typed[i].Body);
            if (a.AsSpan().SequenceEqual(b))
                continue;
            // S2C 517 carries the seat's random join token, which differs between any two server runs; its length is compared.
            if (raw[i].Opcode == 517 && a.Length == b.Length)
                continue;
            // S2C 3 carries the session string "bff-<n>", where n is a process-wide counter (WireSession._nextId), so the two
            // servers of one test process number their sessions differently. Only that string is compared as a fixed token.
            if (raw[i].Opcode == 3 && WithoutSessionNumber(a).AsSpan().SequenceEqual(WithoutSessionNumber(b)))
                continue;
            if (Canonical.TryGetValue(raw[i].Opcode, out var structure) && SameAfterRoundTrip(structure, a, b))
                continue;
            var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(k => a[k] != b[k], Math.Min(a.Length, b.Length));
            differences.Add($"frame {i} S{raw[i].Opcode} bytes differ (raw {a.Length} B, typed {b.Length} B, first difference at +{at}): "
                + $"raw {Hex(a, at)} typed {Hex(b, at)}");
        }
        return differences.Count == 0 ? null : string.Join("; ", differences);
    }

    /// <summary>
    /// Replaces the session number in a gspRepLogin body (S2C 3) with a fixed token. The session string is a
    /// wide string at body offset 4 (i32 character count, then UTF-16LE), written by the login reply as
    /// <c>bff-&lt;WireSession.Id&gt;</c>. Any other shape is returned unchanged, so it still fails the comparison.
    /// </summary>
    /// <param name="body">A gspRepLogin body.</param>
    /// <returns>The body with the session number replaced, or the same body.</returns>
    internal static byte[] WithoutSessionNumber(byte[] body)
    {
        if (body.Length < 8)
            return body;
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (length < 5 || 8 + 2 * length > body.Length)
            return body;
        var session = Encoding.Unicode.GetString(body, 8, 2 * length);
        if (!session.StartsWith("bff-", StringComparison.Ordinal) || !session[4..].All(char.IsAsciiDigit))
            return body;
        var token = Encoding.Unicode.GetBytes("bff-0");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, token.Length / 2);
        return [.. body[..4], .. prefix, .. token, .. body[(8 + 2 * length)..]];
    }

    private static bool SameAfterRoundTrip(string structure, byte[] raw, byte[] typed)
    {
        try
        {
            var tables = ParityRun.Tables;
            var rawFields = PacketCodec.Read(tables, "kr-1256", structure, raw);
            var typedFields = PacketCodec.Read(tables, "kr-1256", structure, typed);
            // The raw path echoes the startup map a create carries; the typed path shows a room whose master has not
            // picked a map as the all-random entry (builds.yaml room_unchosen_map). Where the typed frame shows that
            // entry, the raw frame is compared with the same presentation.
            var unchosen = RoomUnchosenMap.Load(tables, "kr-1256");
            if (unchosen is not null)
                PresentLikeTyped(rawFields, typedFields, unchosen);
            return PacketCodec.Write(tables, "kr-1256", structure, rawFields).AsSpan()
                .SequenceEqual(PacketCodec.Write(tables, "kr-1256", structure, typedFields));
        }
        catch (PacketFormatException)
        {
            return false;
        }
    }

    private static void PresentLikeTyped(Dictionary<string, object?> raw, Dictionary<string, object?> typed, RoomUnchosenMap unchosen)
    {
        foreach (var (key, value) in typed)
        {
            if (value is not Dictionary<string, object?> typedInner || !raw.TryGetValue(key, out var rawValue)
                || rawValue is not Dictionary<string, object?> rawInner)
                continue;
            if (unchosen.Fields.All(field => typedInner.TryGetValue(field.Key, out var shown) && Equals(shown, field.Value)))
                unchosen.Apply(rawInner);
            else
                PresentLikeTyped(rawInner, typedInner, unchosen);
        }
    }

    private static string Describe(IReadOnlyList<ParityFrame> frames) =>
        string.Join(" ", frames.Select(f => $"{f.Opcode}({f.Body.Length})"));

    private static string Hex(byte[] body, int at)
    {
        var start = Math.Max(0, at - 4);
        return Convert.ToHexString(body.AsSpan(start, Math.Min(16, body.Length - start)));
    }
}

/// <summary>One S2C packet as a client received it.</summary>
/// <param name="Opcode">S2C opcode.</param>
/// <param name="Body">Decrypted payload.</param>
public sealed record ParityFrame(ushort Opcode, byte[] Body);

/// <summary>The script run once on each path, shared by the tests of the class.</summary>
public sealed class ParityRun : IAsyncLifetime
{
    /// <summary>Script step names in order.</summary>
    public static readonly string[] Steps =
    [
        "login", "A 106", "B 106", "A 401", "A 402 create", "B 403 join", "B 517 probe", "B 505 ready", "B 505 shop",
        "B 505 inventory", "B 505 clear", "B 515 team", "A 502 chat", "A 509 lock", "B 404 leave", "A 404 leave",
        "A 105 menu", "A 106 again", "A 402 create again",
    ];

    // After a step, both clients must be silent this long before the next request; the cap bounds a chatty step.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(8);

    internal static readonly PacketTables Tables = PacketTables.Load(RepositoryPackets.Directory());

    /// <summary>Frames per step and client name on the raw path.</summary>
    public Dictionary<string, Dictionary<string, List<ParityFrame>>> Raw { get; private set; } = [];

    /// <summary>Frames per step and client name on the typed path.</summary>
    public Dictionary<string, Dictionary<string, List<ParityFrame>>> Typed { get; private set; } = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var raw = RunAsync(typed: false);
        var typed = RunAsync(typed: true);
        Raw = await raw;
        Typed = await typed;
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Waits until none of the clients has received anything for the quiet period, or the cap passes.</summary>
    internal static async Task Settle(CancellationToken ct, params SimActor[] clients)
    {
        var deadline = DateTime.UtcNow + Cap;
        var last = clients.Select(c => c.Cursor).ToArray();
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow - since < Quiet && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
            var now = clients.Select(c => c.Cursor).ToArray();
            if (!now.SequenceEqual(last))
            {
                last = now;
                since = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Starts the kr-1256 rows with the live in-game options of the committed profile (bundles ingame and kr-1256);
    /// the typed variant applies the kr-1256 default rows that <c>profiles/kr-1256/raw.patch.yaml</c> reverts.
    /// </summary>
    internal static Task<ScenarioServer> StartAsync(bool typed, bool rounds = false) =>
        ScenarioServer.StartAsync((game, relay) => Rows(game, relay, typed, rounds));

    private static List<PluginRow> Rows(int game, int relay, bool typed, bool rounds)
    {
        var rows = ScenarioServer.Rows(game, relay);
        var ingame = new Dictionary<string, object?>
        {
            ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay", ["version_major"] = 1256,
            ["relay_advertise_ipv4"] = "127.0.0.1", ["relay_advertise_port"] = relay,
            ["max_matches"] = 64, ["max_players_per_match"] = 16, ["start_timer_ms"] = 7000,
            ["reward_base_exp"] = 150, ["reward_exp_per_kill"] = 0, ["reward_win_multiplier"] = 2, ["reward_lucci_percent"] = 35,
            ["score_based_rewards"] = true, ["reward_exp_multiplier"] = 100, ["reward_lucci_multiplier"] = 100,
            ["start_matches"] = true, ["send_plaza_snapshot"] = true, ["send_room_list"] = true,
            ["republish_room_ui_on_join"] = true, ["republish_room_ui_on_position"] = true, ["broadcast_room_chat"] = true,
            ["relay_character_positions"] = true, ["republish_full_room_on_team_change"] = false,
            ["room_install_state"] = 2, ["room_state"] = 2, ["prime_session_state_before_install"] = false,
            ["send_party_update"] = true, ["send_wait_user_list"] = false, ["send_seat_records"] = false,
            ["start_flag0"] = 1, ["start_flag1"] = 0, ["start_flag2"] = 1, ["result_close"] = 0,
            ["result_scoreboard_delay_ms"] = 1000, ["result_reward_delay_ms"] = 5000, ["result_close_grace_ms"] = 10000,
        };
        var messages = TestWireDialects.ModernMessagesConfig();
        if (typed)
        {
            messages["room_binding"] = "typed";
            ingame["typed_only"] = true;
            ingame["plaza_enter_requires_finished_login"] = true;
            ingame["plaza_replay_positions_on_enter"] = false;
            ingame["plaza_echo_positions"] = false;
            ingame["plaza_vacate_peers_on_menu_return"] = false;
            if (rounds)
            {
                messages["typed_rounds"] = true;
                messages["round_loading_flags"] = new object?[] { 1, 0, 1 };
                ingame["native_match_relay"] = true;
                ingame["server_map_draw"] = true;
                ingame["assign_spawn_teams_on_start"] = true;
                ingame["native_life_mode_ids"] = new object?[] { 1 };
            }
        }
        if (rounds)
        {
            // The round script plays to the result: short result stages keep the run within the test timeout.
            ingame["result_scoreboard_delay_ms"] = 300;
            ingame["result_reward_delay_ms"] = 600;
            ingame["result_close_grace_ms"] = 900;
        }
        Replace(rows, "ingame", ingame);
        Replace(rows, "dialect.modern.messages", messages);
        if (typed && rounds)
        {
            Replace(rows, "match-connection", new()
            {
                ["protocol"] = "kr-1256/relay", ["capability_authentication"] = true, ["require_capability_scene_report"] = true,
            });
            rows.RemoveAll(row => row.Id is "match-relay");
        }
        else if (typed)
            rows.RemoveAll(row => row.Id is "match-relay" or "match-connection" or "relay-listener");
        return rows;
    }

    internal static void Replace(List<PluginRow> rows, string id, Dictionary<string, object?> config)
    {
        var index = rows.FindIndex(row => row.Id == id);
        rows[index] = ScenarioServer.Row(id, rows[index].Plugin, config);
    }

    internal static byte[] Body(string structure, Dictionary<string, object?> fields) =>
        PacketCodec.Write(Tables, "kr-1256", structure, fields);

    private static async Task<Dictionary<string, Dictionary<string, List<ParityFrame>>>> RunAsync(bool typed)
    {
        await using var server = await StartAsync(typed);
        var a = await server.ConnectAsync("scenA");
        var b = await server.ConnectAsync("scenB");
        var result = new Dictionary<string, Dictionary<string, List<ParityFrame>>>();
        var roomId = 0;

        async Task Step(string name, Func<Task> act)
        {
            var (fromA, fromB) = (a.Cursor, b.Cursor);
            await act();
            await Settle(server.Ct, a, b);
            result[name] = new()
            {
                ["A"] = a.Since(fromA).Select(f => new ParityFrame(f.Opcode, f.Payload)).ToList(),
                ["B"] = b.Since(fromB).Select(f => new ParityFrame(f.Opcode, f.Payload)).ToList(),
            };
        }

        await Step("login", async () => { await a.Login(); await b.Login(); });
        await Step("A 106", () => a.Send(106, []));
        await Step("B 106", () => b.Send(106, []));
        await Step("A 401", () => a.SendReference(401));
        await Step("A 402 create", async () =>
        {
            var from = a.Cursor;
            await a.SendReference(402);
            var install = await a.WaitFor(117, from);
            var reader = new PacketReader(install.Payload);
            roomId = SimRoomList.ReadSessionSetting(ref reader).Id;
        });
        await Step("B 403 join", () => b.Send(403, Body("RoomJoinRequest", new() { ["room_id"] = roomId, ["password"] = "" })));
        await Step("B 517 probe", () => b.Send(517, Body("RoomPingRequest", new() { ["unknown_n0"] = 0 })));
        await Step("B 505 ready", () => b.Send(505, Body("RoomReadyRequest", new() { ["ready"] = true, ["unknown_5"] = false, ["unknown_6"] = false })));
        await Step("B 505 shop", () => b.Send(505, Body("RoomReadyRequest", new() { ["ready"] = false, ["unknown_5"] = true, ["unknown_6"] = false })));
        await Step("B 505 inventory", () => b.Send(505, Body("RoomReadyRequest", new() { ["ready"] = false, ["unknown_5"] = false, ["unknown_6"] = true })));
        await Step("B 505 clear", () => b.Send(505, Body("RoomReadyRequest", new() { ["ready"] = false, ["unknown_5"] = false, ["unknown_6"] = false })));
        await Step("B 515 team", () => b.Send(515, [0xBA, 0xBA]));
        await Step("A 502 chat", () => a.Send(502, Body("RoomChatRequest", new() { ["channel"] = 0, ["text"] = "hello", ["unknown_n2"] = "" })));
        await Step("A 509 lock", () => a.Send(509, Body("RoomSeatLock", new() { ["slot"] = 3, ["closed"] = true })));
        await Step("B 404 leave", () => b.Send(404, []));
        await Step("A 404 leave", () => a.Send(404, []));
        await Step("A 105 menu", () => a.Send(105, []));
        await Step("A 106 again", () => a.Send(106, []));
        await Step("A 402 create again", () => a.SendReference(402));
        return result;
    }
}
