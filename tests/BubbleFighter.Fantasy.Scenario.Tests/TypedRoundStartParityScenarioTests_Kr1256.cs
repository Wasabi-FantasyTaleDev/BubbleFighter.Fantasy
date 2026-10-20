using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Runs one round-start script for two clients against the raw kr-1256 start path and against the typed
/// path (<c>typed_rounds</c>, the kr-1256 default since profiles/kr-1256/raw.patch.yaml became the opt-out), and compares what each client
/// receives on its game and relay connections, step by step: the start roster, signature and stage enter, the
/// object-add acknowledgement, the relay offer, the relay admission, the loading gates, the countdown, the round
/// start, the in-round traffic, a bubble, pop and reset, and the timed end through the result back to the room.
/// </summary>
public sealed class TypedRoundStartParityScenarioTests_Kr1256 : IClassFixture<RoundStartParityRun>
{
    private readonly RoundStartParityRun _run;
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test class around the shared script run.</summary>
    /// <param name="run">The packets of both runs.</param>
    /// <param name="output">Receives the per-step packet listing.</param>
    public TypedRoundStartParityScenarioTests_Kr1256(RoundStartParityRun run, ITestOutputHelper output)
    {
        _run = run;
        _output = output;
    }

    /// <summary>Every (step, connection) pair of the script.</summary>
    /// <returns>Theory rows.</returns>
    public static IEnumerable<object[]> Comparisons() =>
        RoundStartParityRun.Steps.SelectMany(step => RoundStartParityRun.Connections.Select(connection => new object[] { step, connection }));

    /// <summary>One connection's packets of one step are the same on the raw and the typed path.</summary>
    /// <param name="step">Script step name.</param>
    /// <param name="connection">A or B for the game connections, A relay or B relay for the relay connections.</param>
    [Theory]
    [MemberData(nameof(Comparisons))]
    public void StepMatches(string step, string connection)
    {
        var raw = Normalize(_run.Raw[step][connection]);
        var typed = Normalize(_run.Typed[step][connection]);
        _output.WriteLine($"raw [{Describe(raw)}] typed [{Describe(typed)}]");
        var difference = TypedRoomParityScenarioTests_Kr1256.Compare(raw, typed);
        Assert.True(difference is null, $"{step} / {connection}: {difference}");
    }

    /// <summary>The script reaches the round start on both paths, so the comparisons above cover the whole sequence.</summary>
    [Fact]
    public void BothPathsStartTheRound()
    {
        foreach (var run in new[] { _run.Raw, _run.Typed })
            foreach (var client in new[] { "A", "B" })
                Assert.Contains(run["round start"][client], frame => frame.Opcode == 608);
    }

    /// <summary>
    /// The in-round steps carry traffic on both paths, so their comparisons are not of two empty lists: the other
    /// player's relay gets the merged movement, the action and the spawn, and the sender's relay the echoed death.
    /// </summary>
    [Fact]
    public void BothPathsCarryTheRoundsTraffic()
    {
        foreach (var run in new[] { _run.Raw, _run.Typed })
        {
            Assert.Equal(21, Assert.Single(run["A move full"]["B relay"], frame => frame.Opcode == 32885).Body.Length);
            var merged = Assert.Single(run["A move partial"]["B relay"], frame => frame.Opcode == 32885).Body;
            Assert.Equal(9.5f, BitConverter.ToSingle(merged, 4));
            Assert.Equal(2.5f, BitConverter.ToSingle(merged, 8));
            Assert.Equal(7, merged[18]);
            Assert.Single(run["A dash"]["B relay"], frame => frame.Opcode == 32779);
            Assert.Empty(run["A dash"]["A relay"]);
            Assert.Single(run["A dead"]["A relay"], frame => frame.Opcode == 32836);
            Assert.Single(run["A dead"]["B relay"], frame => frame.Opcode == 32836);
            Assert.Single(run["A damager spawn"]["B relay"], frame => frame.Opcode == 32899);
            Assert.Empty(run["B move partial first"]["A relay"]);
        }
    }

    /// <summary>
    /// The life and result steps carry traffic on both paths: the pop scores a team point for both players, the
    /// reset request is granted, and the timed end opens the result, shows the scoreboard and the rewards, and
    /// returns both players to the room.
    /// </summary>
    [Fact]
    public void BothPathsPlayTheLifeCycleAndTheResult()
    {
        foreach (var (path, run) in new[] { ("raw", _run.Raw), ("typed", _run.Typed) })
        {
            string Seen(string step, string client) => $"{path} {step} / {client}: [{Describe(run[step][client])}]";
            foreach (var client in new[] { "A", "B" })
            {
                Assert.True(run["A pops B"][client].Any(frame => frame.Opcode == 985), Seen("A pops B", client));
                foreach (var opcode in new ushort[] { 612, 615, 620 })
                    Assert.True(run["round end"][client].Any(frame => frame.Opcode == opcode), $"{opcode}: {Seen("round end", client)}");
                Assert.True(run["round end"][client].Count(frame => frame.Opcode == 614) == 1, Seen("round end", client));
            }
            Assert.True(run["B reset"]["B"].Count(frame => frame.Opcode == 632) == 1, Seen("B reset", "B"));
            Assert.True(run["B reset"]["A"].Count == 0, Seen("B reset", "A"));
        }
    }

    /// <summary>Each relay offer names the listening port of the server that sent it.</summary>
    [Fact]
    public void OffersNameTheServersRelayPort()
    {
        foreach (var (run, port) in new[] { (_run.Raw, _run.RawRelayPort), (_run.Typed, _run.TypedRelayPort) })
            foreach (var client in new[] { "A", "B" })
            {
                var offer = run[client + " 578"][client].Single(frame => frame.Opcode == 571);
                var endpoint = (IReadOnlyDictionary<string, object?>)PacketCodec.Read(ParityRun.Tables, "kr-1256", "RelayConnectionOffer", offer.Body)["endpoint"]!;
                Assert.Equal(port, Convert.ToInt32(endpoint["port"]));
            }
    }

    /// <summary>The typed stage enter names one map in all three slots, as the raw stage record does for a map round.</summary>
    [Fact]
    public void TheTypedStageEnterNamesOneMap()
    {
        var stage = _run.Typed["A 503 start"]["A"].Single(frame => frame.Opcode == 510);
        var fields = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomEnterGame", stage.Body);
        Assert.False(string.IsNullOrEmpty((string)fields["map_key"]!));
        Assert.Equal(fields["map_key"], fields["stage_key"]);
        Assert.Equal(fields["map_key"], fields["map_selection"]);
        Assert.Equal(3, Convert.ToInt32(fields["room_state"]));
    }

    // The room was created with a random selection, so each server draws its own map: the S2C 510 strings are
    // compared after they are blanked. In S2C 571 the relay key is random per seat and the port is each test
    // server's own free port (checked by OffersNameTheServersRelayPort): the address is compared.
    // S2C 1500 answers the scripted client's own 15-second keepalive, so where it falls in a long step is the test
    // client's timing, not the server's: it is left out of the comparison.
    private static List<ParityFrame> Normalize(IReadOnlyList<ParityFrame> frames) =>
    [
        .. frames.Where(frame => frame.Opcode != 1500).Select(frame => frame.Opcode switch
        {
            510 => frame with { Body = BlankStage(frame.Body) },
            571 when frame.Body.Length == 26 => frame with { Body = [.. frame.Body.AsSpan(0, 4), .. new byte[22]] },
            _ => frame,
        }),
    ];

    private static byte[] BlankStage(byte[] body)
    {
        var fields = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomEnterGame", body);
        fields["map_key"] = fields["stage_key"] = fields["map_selection"] = "";
        return PacketCodec.Write(ParityRun.Tables, "kr-1256", "RoomEnterGame", fields);
    }

    private static string Describe(IReadOnlyList<ParityFrame> frames) =>
        string.Join(" ", frames.Select(f => $"{f.Opcode}({f.Body.Length})"));
}

/// <summary>The round-start script run once on each path, shared by the tests of the class.</summary>
public sealed class RoundStartParityRun : IAsyncLifetime
{
    /// <summary>Script step names in order.</summary>
    public static readonly string[] Steps =
    [
        "A 503 start", "A load", "B load", "A 578", "B 578", "A relay join", "B relay join", "A relay scene",
        "B relay scene", "A 571", "A 941", "B 571", "B 941", "round start", "A move full", "A move partial",
        "A dash", "A dead", "A damager spawn", "B move partial first", "B bubbled", "A pops B", "B death cleaned",
        "B reset", "B reset confirm", "round end",
    ];

    /// <summary>The connections: both game connections and both relay connections.</summary>
    public static readonly string[] Connections = ["A", "B", "A relay", "B relay"];

    /// <summary>Frames per step and connection on the raw path.</summary>
    public Dictionary<string, Dictionary<string, List<ParityFrame>>> Raw { get; private set; } = [];

    /// <summary>Frames per step and connection on the typed path.</summary>
    public Dictionary<string, Dictionary<string, List<ParityFrame>>> Typed { get; private set; } = [];

    /// <summary>The raw run's relay listener port.</summary>
    public int RawRelayPort { get; private set; }

    /// <summary>The typed run's relay listener port.</summary>
    public int TypedRelayPort { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var raw = RunAsync(typed: false);
        var typed = RunAsync(typed: true);
        (Raw, RawRelayPort) = await raw;
        (Typed, TypedRelayPort) = await typed;
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(Dictionary<string, Dictionary<string, List<ParityFrame>>>, int)> RunAsync(bool typed)
    {
        await using var server = await ParityRun.StartAsync(typed, rounds: true);
        var a = await server.ConnectAsync("scenRoundA");
        var b = await server.ConnectAsync("scenRoundB");
        await a.Login();
        await b.Login();
        await a.Send(106, []);
        await b.Send(106, []);
        await ParityRun.Settle(server.Ct, a, b);
        var from = a.Cursor;
        await a.Send(402, TimedScoreRoom(a.Reference(402)));
        var install = await a.WaitFor(117, from);
        var reader = new PacketReader(install.Payload);
        var roomId = SimRoomList.ReadSessionSetting(ref reader).Id;
        var seatA = Seat(await a.WaitFor(503, from));
        await ParityRun.Settle(server.Ct, a, b);
        from = b.Cursor;
        await b.Send(403, ParityRun.Body("RoomJoinRequest", new() { ["room_id"] = roomId, ["password"] = "" }));
        var seatB = Seat(await b.WaitFor(503, from));
        await ParityRun.Settle(server.Ct, a, b);
        await b.Send(505, ParityRun.Body("RoomReadyRequest", new() { ["ready"] = true, ["unknown_5"] = false, ["unknown_6"] = false }));
        await ParityRun.Settle(server.Ct, a, b);

        var relays = new Dictionary<string, SimActor>();
        var offers = new Dictionary<string, byte[]>();
        var result = new Dictionary<string, Dictionary<string, List<ParityFrame>>>();
        var map = "";

        async Task Step(string name, Func<Task> act, ushort? waitFor = null, double seconds = 15)
        {
            var actors = new Dictionary<string, SimActor> { ["A"] = a, ["B"] = b };
            foreach (var (client, relay) in relays)
                actors[client + " relay"] = relay;
            var cursors = actors.ToDictionary(pair => pair.Key, pair => pair.Value.Cursor);
            await act();
            if (waitFor is { } opcode)
            {
                await a.WaitFor(opcode, cursors["A"], seconds);
                await b.WaitFor(opcode, cursors["B"], seconds);
            }
            await ParityRun.Settle(server.Ct, [.. actors.Values.Concat(relays.Values).Distinct()]);
            result[name] = RoundStartParityRun.Connections.ToDictionary(connection => connection, connection =>
                actors.TryGetValue(connection, out var actor) && cursors.TryGetValue(connection, out var cursor)
                    ? actor.Since(cursor).Select(f => new ParityFrame(f.Opcode, f.Payload)).ToList()
                    : relays.TryGetValue(connection.Replace(" relay", "", StringComparison.Ordinal), out var opened)
                        ? opened.Since(0).Select(f => new ParityFrame(f.Opcode, f.Payload)).ToList()
                        : []);
        }

        // kr-1256 sub_9D7570: the resolved key, the map name and the master's selection, then the rule record.
        await Step("A 503 start", async () =>
        {
            var cursor = a.Cursor;
            await a.Send(503, StartRequest());
            var stage = await a.WaitFor(510, cursor);
            map = (string)PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomEnterGame", stage.Payload)["map_key"]!;
        });
        // kr-1256 sub_755330: the map report, then the local character's scene object.
        await Step("A load", () => Load(a, seatA.Slot));
        await Step("B load", () => Load(b, seatB.Slot));
        await Step("A 578", () => MapLoaded(a, "A"));
        await Step("B 578", () => MapLoaded(b, "B"));
        await Step("A relay join", () => Join("A", seatA));
        await Step("B relay join", () => Join("B", seatB));
        await Step("A relay scene", () => relays["A"].Send(12, Scene(seatA)));
        await Step("B relay scene", () => relays["B"].Send(12, Scene(seatB)));
        await Step("A 571", () => a.Send(571, ParityRun.Body("RelayConnectionReport", new() { ["unknown_4"] = true })));
        await Step("A 941", () => a.Send(941, MapKeyReport(map)));
        await Step("B 571", () => b.Send(571, ParityRun.Body("RelayConnectionReport", new() { ["unknown_4"] = true })));
        await Step("B 941", () => b.Send(941, MapKeyReport(map)));
        await Step("round start", () => Task.CompletedTask, waitFor: 608);
        var actorA = 1000000 + 100000 * seatA.Slot;
        var actorB = 1000000 + 100000 * seatB.Slot;
        // In the round: a full movement record and a partial one on the game connection (merged into a full record
        // for the other player), an action, an action the sender also gets back, and a damager spawn on the relay.
        await Step("A move full", () => a.Send(32885, FullMovement(actorA, 7)));
        await Step("A move partial", () => a.Send(32923, PartialMovement(actorA, 8)));
        await Step("A dash", () => relays["A"].Send(32779, Words(actorA, 5)));
        await Step("A dead", () => relays["A"].Send(32836, Words(actorA)));
        await Step("A damager spawn", () => relays["A"].Send(32899, new byte[19]));
        // A partial before any full record has nothing to merge into and goes nowhere.
        await Step("B move partial first", () => b.Send(32923, PartialMovement(actorB, 1)));
        // The life cycle of the timed score mode: A bubbles B (victim, source, then +24 zero = bubbled), pops it
        // (success, victim, popper) for a team point, B's death is cleaned, and B asks for and confirms its reset.
        await Step("B bubbled", () => relays["B"].Send(38282, Life(40, (0, actorB), (4, actorA))));
        await Step("A pops B", () => relays["A"].Send(38277, Life(32, (0, 1), (4, actorB), (8, actorA))));
        await Step("B death cleaned", () => relays["B"].Send(32836, Words(actorB)));
        await Step("B reset", () => b.Send(602, [.. Words(actorB), 0xBA, 0xBA, .. Words(0)]));
        await Step("B reset confirm", () => relays["B"].Send(32926, [.. Words(actorB), 0xBA, 0xBA, .. new byte[5]]));
        // The round clock of the start request runs out on the server; the result stages end with the room return.
        await Step("round end", () => Task.CompletedTask, waitFor: 620, seconds: RoundSeconds + 20);
        return (result, server.RelayPort);

        async Task Load(SimActor actor, int slot)
        {
            await actor.Send(579, new byte[4]);
            await actor.Send(607, ParityRun.Body("RoundObjectAddRequest", new() { ["object_id"] = 1000000 + 100000 * slot }));
        }

        async Task MapLoaded(SimActor actor, string client)
        {
            var cursor = actor.Cursor;
            await actor.Send(578, ParityRun.Body("RoundMapLoadingReport", new() { ["unknown_4"] = 0, ["unknown_8"] = 0, ["unknown_12"] = 0, ["unknown_16"] = 0 }));
            offers[client] = (await actor.WaitFor(571, cursor)).Payload;
        }

        // kr-1256 sub_95FD30: the offered key, a byte and a word, then the account and the nickname.
        async Task Join(string client, (int Slot, int User, string Nickname) seat)
        {
            relays[client] = await server.ConnectRelayAsync(client == "A" ? a.Name : b.Name);
            var key = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RelayConnectionOffer", offers[client])["key"];
            await relays[client].Send(1, ParityRun.Body("RelayJoinRequest", new()
            {
                ["key"] = key, ["unknown_28"] = (byte)0, ["unknown_32"] = 0, ["unknown_n3"] = seat.User, ["unknown_n4"] = seat.Nickname,
            }));
            await relays[client].WaitFor(1, 0);
        }
    }

    private static (int Slot, int User, string Nickname) Seat(SimFrame update)
    {
        var fields = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomSeatUpdate", update.Payload);
        var user = (IReadOnlyDictionary<string, object?>)fields["user"]!;
        return (Convert.ToInt32(fields["slot"]), Convert.ToInt32(user["user_number"]), (string)user["nickname"]!);
    }

    private static byte[] StartRequest()
    {
        var fields = new Dictionary<string, object?>
        {
            ["unknown_4"] = false, ["map_key"] = "rel_Village01_Byway", ["unknown_36"] = "", ["unknown_64"] = "Random_Hot",
            ["death_match_time_limit_ms"] = RoundSeconds * 1000, ["time_attack_time_limit_ms"] = RoundSeconds * 1000 + 1000,
        };
        return ParityRun.Body("RoomEnterGameRequest", fields);
    }

    // The scene report the relay reads (ModernMatchCodec.ReadScene): account, actor and nickname, then empty records.
    private static byte[] Scene((int Slot, int User, string Nickname) seat)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(seat.User);
        w.WriteInt32(1000000 + 100000 * seat.Slot);
        w.WriteWideStringPrefixed(seat.Nickname);
        w.WriteInt32(0);
        w.WriteInt32(0);
        for (var i = 0; i < 5; i++) w.WriteUInt32(0);
        w.WriteInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteWideStringPrefixed("");
        w.WriteByte(0);
        for (var i = 0; i < 4; i++) w.WriteUInt16(0);
        w.WriteByte(0);
        w.WriteUInt32(0);
        w.WriteInt32(0);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    // kr-1256 sub_95E920: actor, three f32, yaw, pitch, state, tick.
    private static byte[] FullMovement(int actor, ushort tick)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(actor);
        w.WriteSingle(1.5f);
        w.WriteSingle(2.5f);
        w.WriteSingle(3.5f);
        w.WriteByte(4);
        w.WriteByte(5);
        w.WriteByte(6);
        w.WriteUInt16(tick);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    // kr-1256 sub_6CAFD0: actor, tick, mask, then the selected components (here x and state).
    private static byte[] PartialMovement(int actor, ushort tick)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(actor);
        w.WriteUInt16(tick);
        w.WriteByte(0x01 | 0x20);
        w.WriteSingle(9.5f);
        w.WriteByte(7);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    // The reference create request with the room's mode set to the timed score mode (GameModeId.Pvp), whose life
    // cycle, team score and scoreboard both paths implement.
    private static byte[] TimedScoreRoom(byte[] reference)
    {
        var fields = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomCreateRequest", reference);
        var setting = (Dictionary<string, object?>)fields["session_setting"]!;
        var game = (Dictionary<string, object?>)setting["game_setting"]!;
        game["mode"] = (ushort)1;
        return PacketCodec.Write(ParityRun.Tables, "kr-1256", "RoomCreateRequest", fields);
    }

    // Long enough for the in-round steps, short enough that the timed end comes soon after them.
    private const int RoundSeconds = 25;

    private static byte[] Life(int length, params (int Offset, int Value)[] words)
    {
        var body = new byte[length];
        foreach (var (offset, value) in words)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), value);
        return body;
    }

    private static byte[] Words(params int[] words)
    {
        var body = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
            BitConverter.TryWriteBytes(body.AsSpan(i * 4), words[i]);
        return body;
    }

    // The resolved map key report: a word, then the key (kr-1256 C2S 941).
    private static byte[] MapKeyReport(string map)
    {
        var w = PacketWriter.Create();
        w.WriteInt32(map.Length);
        w.WriteWideStringPrefixed(map);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
