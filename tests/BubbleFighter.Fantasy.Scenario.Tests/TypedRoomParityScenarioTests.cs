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
/// Runs one request script for two clients against the typed kr-1256 room path with the live in-game options
/// of the committed profile (bundles ingame and kr-1256). The raw path this transcript was once compared
/// against step by step is gone; the script keeps pinning the typed frames it verified, among them the join
/// probe's seat-naming S2C 518.
/// </summary>
public sealed class TypedRoomParityScenarioTests : IClassFixture<ParityRun>
{
    private readonly ParityRun _run;
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class around the shared script run.</summary>
    /// <param name="run">The packets of the run.</param>
    /// <param name="output">Receives the per-step packet listing.</param>
    public TypedRoomParityScenarioTests(ParityRun run, ITestOutputHelper output)
    {
        _run = run;
        _output = output;
    }

    /// <summary>
    /// The probe step's S2C 518 names the probing seat: the client's reader decides (kr-1256 sub_9D9930 case
    /// 0x206 reads three words and kr-1256 sub_978F20 uses the first as the seat index of its ping history),
    /// so the first word is the prober's seat, 12 bytes in all.
    /// </summary>
    [Fact]
    public void Kr1256JoinProbeNamesTheProbingSeat()
    {
        var typed = _run.Typed["B 517 probe"]["B"];
        var reply = Assert.Single(typed, frame => frame.Opcode == 518);
        var seat = (int)PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomSeatUpdate",
            _run.Typed["B 403 join"]["B"].First(frame => frame.Opcode == 503).Body)["slot"]!;
        Assert.Equal(12, reply.Body.Length);
        Assert.Equal(seat, (int)PacketCodec.Read(ParityRun.Tables, "kr-1256", "gspRepPingUser", reply.Body)["slot"]!);
    }

    /// <summary>Lists every step's frames, so a failing run shows the whole transcript at once.</summary>
    [Fact]
    public void Kr1256ListsEveryStepsFrames()
    {
        foreach (var step in ParityRun.Steps)
            foreach (var client in new[] { "A", "B" })
                _output.WriteLine($"{step} / {client}: [{Describe(_run.Typed[step][client])}]");
    }

    /// <summary>A plaza enter sent before the login finished is ignored, with the same silence.</summary>
    [Fact]
    public async Task Kr1256PlazaEnterBeforeTheLoginBurstEndsGetsNoReply()
    {
        var typed = await EarlyPlazaAsync();
        _output.WriteLine($"typed [{Describe(typed)}]");
        Assert.DoesNotContain(typed, f => f.Opcode == 116);
    }

    // The request goes out after the version exchange and before the login, so the session has no player yet and the
    // gate is deterministic; a request racing the burst itself would depend on when the lobby row moves the phase on.
    private static async Task<List<ParityFrame>> EarlyPlazaAsync()
    {
        await using var server = await ParityRun.StartAsync();
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

    private static string Describe(IReadOnlyList<ParityFrame> frames) =>
        string.Join(" ", frames.Select(f => $"{f.Opcode}({f.Body.Length})"));
}

/// <summary>One S2C packet as a client received it.</summary>
/// <param name="Opcode">S2C opcode.</param>
/// <param name="Body">Decrypted payload.</param>
public sealed record ParityFrame(ushort Opcode, byte[] Body);

/// <summary>The script run once, shared by the tests of the class.</summary>
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

    /// <summary>Frames per step and client name.</summary>
    public Dictionary<string, Dictionary<string, List<ParityFrame>>> Typed { get; private set; } = [];

    /// <inheritdoc />
    public async Task InitializeAsync() => Typed = await RunAsync();

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
    /// Starts the kr-1256 rows with the live in-game options of the committed profile (bundles ingame and
    /// kr-1256); the rows are the typed room path, the profile's only path.
    /// </summary>
    internal static Task<ScenarioServer> StartAsync(bool rounds = false, bool initialTutorial = false, string tutorialKind = "pirate_prologue") =>
        ScenarioServer.StartAsync((game, relay) => Rows(game, relay, rounds, initialTutorial, tutorialKind));

    private static List<PluginRow> Rows(int game, int relay, bool rounds, bool initialTutorial, string tutorialKind)
    {
        var rows = ScenarioServer.Rows(game, relay);
        if (initialTutorial)
            Replace(rows, "dialect.rooms", new()
            {
                ["protocol"] = "kr-1256/game", ["modern_rooms"] = true, ["initial_tutorial"] = true, ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
                ["initial_tutorial_kind"] = tutorialKind,
            });
        var ingame = new Dictionary<string, object?>
        {
            ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay",
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
            ["plaza_enter_requires_finished_login"] = true,
            ["plaza_replay_positions_on_enter"] = false,
            ["plaza_echo_positions"] = false,
            ["plaza_vacate_peers_on_menu_return"] = false,
        };
        if (rounds)
        {
            ingame["native_match_relay"] = true;
            ingame["server_map_draw"] = true;
            ingame["assign_spawn_teams_on_start"] = true;
            ingame["native_life_mode_ids"] = new object?[] { 1 };
            // The round script plays to the result: short result stages keep the run within the test timeout.
            ingame["result_scoreboard_delay_ms"] = 300;
            ingame["result_reward_delay_ms"] = 600;
            ingame["result_close_grace_ms"] = 900;
        }
        Replace(rows, "ingame", ingame);
        if (rounds)
        {
            // The round admits the relay by its room-issued key: the capability admission, with the
            // game-side offer and connection report on their own row.
            Replace(rows, "match-connection", new()
            {
                ["protocol"] = "kr-1256/relay", ["capability_authentication"] = true, ["require_capability_scene_report"] = true,
            });
            // The base rows already mount the capability admission and the offers row; the rounds
            // run only adds the scene-report requirement.
            Replace(rows, "dialect.match", new()
            {
                ["protocol"] = "kr-1256/relay", ["native_handshake"] = "capability",
                ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
            });
        }
        else
            rows.RemoveAll(row => row.Id is "match-connection" or "relay-listener");
        rows.RemoveAll(row => row.Id is "match-relay");
        return rows;
    }

    internal static void Replace(List<PluginRow> rows, string id, Dictionary<string, object?> config)
    {
        var index = rows.FindIndex(row => row.Id == id);
        rows[index] = ScenarioServer.Row(id, rows[index].Plugin, config);
    }

    internal static byte[] Body(string structure, Dictionary<string, object?> fields) =>
        PacketCodec.Write(Tables, "kr-1256", structure, fields);

    private static async Task<Dictionary<string, Dictionary<string, List<ParityFrame>>>> RunAsync()
    {
        await using var server = await StartAsync();
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
