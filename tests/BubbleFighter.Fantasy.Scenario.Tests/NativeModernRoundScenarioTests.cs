using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Drives complete native modern rounds through two independent encrypted game and relay connections.</summary>
[Collection("Native match listener ports")]
public sealed class NativeModernRoundScenarioTests(ITestOutputHelper output)
{
    private static readonly PacketTables Tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

    /// <summary>All three modern dialects must compose loading, relay admission, gameplay and return twice.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task NativeClientsCompleteTwoRounds(string build, bool udp)
    {
        var projection = (IReadOnlyDictionary<string, object?>)Tables.ValuesOf(build)["room_projection_fields"]!;
        var modeField = (string)projection["mode"]!;
        var feed = (IReadOnlyDictionary<string, object?>)Tables.ValuesOf(build)["round_feed"]!;
        var hitName = FeedName("Unknown38282");
        var finishName = FeedName("Unknown38277");
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var relayTable = table.ForConnection(OpcodeConnection.Relay);
        await using var server = await ScenarioServer.StartAsync((game, relay) => Rows(build, game, relay, udp), build, minimumPort: 7500);
        using var udpA = udp ? OpenUdp() : null;
        using var udpB = udp ? OpenUdp() : null;
        var cookies = new Dictionary<SimActor, byte[]>();
        byte[]? staleEnvelope = null;
        var a = await server.ConnectAsync("nativeA" + build.Replace("-", "", StringComparison.Ordinal));
        var b = await server.ConnectAsync("nativeB" + build.Replace("-", "", StringComparison.Ordinal));
        await Login(a);
        await Login(b);
        foreach (var client in new[] { a, b })
        {
            var cursor = client.Cursor;
            await client.Send(table.C2S("gcpReqGotoLobby"), []);
            await client.WaitFor(table.S2C("gspGotoRoom"), cursor);
        }
        var createCursor = a.Cursor;
        await a.Send(table.C2S("gcpReqCreateSession"), Body("RoomCreateRequest", new()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "native-round", ["session_state"] = 2, ["open_seats"] = 4,
                ["game_setting"] = new Dictionary<string, object?>
                { [modeField] = 1 },
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        }));
        var installed = await a.WaitFor(table.S2C("gspGotoRoom"), createCursor);
        var roomId = BinaryPrimitives.ReadUInt16LittleEndian(installed.Payload);
        var seatA = Seat(await a.WaitFor(frame => frame.Opcode == table.S2C("gspRoomSetSlot") && Seat(frame).Name == a.Name, "own room seat", createCursor));
        var joinCursor = b.Cursor;
        await b.Send(table.C2S("gcpReqJoinSession"), Body("RoomJoinRequest", new() { ["room_id"] = (int)roomId, ["password"] = "" }));
        await b.WaitFor(table.S2C("gspGotoRoom"), joinCursor);
        var seatB = Seat(await b.WaitFor(frame => frame.Opcode == table.S2C("gspRoomSetSlot") && Seat(frame).Name == b.Name, "own room seat", joinCursor));
        for (var round = 0; round < 2; round++)
        {
            output.WriteLine("{0}: round {1}, room {2}, seats {3}/{4}", build, round + 1, roomId, seatA.Slot, seatB.Slot);
            var readyCursor = a.Cursor;
            await b.Send(table.C2S("gcpRoomReqReadyStatus"), Body("RoomReadyRequest", new() { ["ready"] = true }));
            await a.WaitFor(frame => frame.Opcode == table.S2C("gspRoomSetSlot") && Seat(frame).Name == b.Name
                && (bool)((IReadOnlyDictionary<string, object?>)PacketCodec.Read(Tables, build, "RoomSeatUpdate", frame.Payload)["user"]!)["ready"]!,
                "peer ready acknowledgement", readyCursor);
            var startA = a.Cursor;
            var startB = b.Cursor;
            await a.Send(table.C2S("gcpRoomReqEnterGame"), Body("RoomEnterGameRequest", new()
            {
                ["map_key"] = "rel_Village01_Byway", ["unknown_36"] = "", ["unknown_64"] = "rel_Village01_Byway",
                ["death_match_time_limit_ms"] = 5000, ["time_attack_time_limit_ms"] = 5000,
            }));
            var entered = await a.WaitFor(table.S2C("gspRoomEnterGame"), startA);
            var stage = PacketCodec.Read(Tables, build, "RoomEnterGame", entered.Payload);
            Assert.Equal("rel_Village01_Byway", stage["map_key"]);
            Assert.Equal(3, Convert.ToInt32(stage["room_state"]));
            await b.WaitFor(table.S2C("gspRoomEnterGame"), startB);
            var relayA = await Load(a, seatA);
            var relayB = await Load(b, seatB);
            await a.WaitFor(table.S2C("gspGameStartGame"), startA);
            var start = await b.WaitFor(table.S2C("gspGameStartGame"), startB);
            Assert.Equal(build == "tw-454" ? 12 : 8, start.Payload.Length);
            var actorA = 1000000 + 100000 * seatA.Slot;
            var actorB = 1000000 + 100000 * seatB.Slot;
            var endA = a.Cursor;
            var endB = b.Cursor;
            var peer = relayB.Cursor;
            if (udp)
            {
                foreach (var relayClient in new[] { relayA, relayB })
                {
                    var udpOffer = await relayClient.WaitFor(relayTable.S2C("rspUdpBypassInfo"), 0);
                    Assert.Equal(10, udpOffer.Payload.Length);
                    Assert.Equal(server.RelayPort, BinaryPrimitives.ReadUInt16LittleEndian(udpOffer.Payload));
                    cookies.Add(relayClient, udpOffer.Payload.AsSpan(2).ToArray());
                }
                await Probe(udpA!, relayA);
                if (round == 0) await Probe(udpB!, relayB);
                if (staleEnvelope is not null)
                {
                    await udpA!.SendToAsync(Frame(20003, staleEnvelope), SocketFlags.None,
                        new IPEndPoint(IPAddress.Loopback, server.RelayPort), server.Ct);
                    using var noStale = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
                    noStale.CancelAfter(TimeSpan.FromMilliseconds(200));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                        await relayB.WaitFor(relayTable.S2C("gppWanderLean"), peer).WaitAsync(noStale.Token));
                }
                staleEnvelope = cookies[relayA].Concat(Movement(actorA)).ToArray();
                await SendUdp(udpA!, relayA, 20003, Movement(actorA));
                if (round == 0) Assert.Equal(Movement(actorA), await ReceiveUdp(udpB!, relayTable.S2C("gppWanderLean"), relayB));
                else Assert.Equal(Movement(actorA), (await relayB.WaitFor(relayTable.S2C("gppWanderLean"), peer)).Payload);
            }
            else
            {
                await relayA.Send(relayTable.C2S("gppWanderLean"), Movement(actorA));
                Assert.Equal(Movement(actorA), (await relayB.WaitFor(relayTable.S2C("gppWanderLean"), peer)).Payload);
            }
            peer = relayB.Cursor;
            var delta = Body("MovementSample", new()
            { ["actor"] = actorA, ["tick"] = (ushort)8, ["mask"] = (byte)1, ["x"] = 9.5f + round });
            if (udp) await SendUdp(udpA!, relayA, 20004, delta);
            else await a.Send(table.C2S("gppWanderLeanOptimize"), delta);
            var mergedBody = udp && round == 0 ? await ReceiveUdp(udpB!, relayTable.S2C("gppWanderLean"), relayB)
                : (await relayB.WaitFor(relayTable.S2C("gppWanderLean"), peer)).Payload;
            var merged = PacketCodec.Read(Tables, build, "MovementReport", mergedBody);
            Assert.Equal(9.5f + round, merged["x"]);
            Assert.Equal(2.5f, merged["y"]);
            Assert.Equal(3.5f, merged["z"]);
            Assert.Equal((ushort)8, Convert.ToUInt16(merged["tick"]));
            peer = relayB.Cursor;
            var spawn = Body("StormDamagerItemId", new() { ["character_key"] = actorA, ["unknown_12"] = -1, ["item_id"] = 7001, ["damager_key"] = 77 });
            await relayA.Send(relayTable.C2S("gppStormDamagerItemId"), spawn);
            Assert.Equal(spawn, (await relayB.WaitFor(relayTable.S2C("gppStormDamagerItemId"), peer)).Payload);
            peer = relayA.Cursor;
            var death = Body("Unknown38282", new() { ["victim"] = actorB, ["source"] = actorA });
            await relayB.Send(relayTable.C2S(hitName), death);
            Assert.Equal(death, (await relayA.WaitFor(relayTable.S2C(hitName), peer)).Payload);
            foreach (var (client, cursor) in new[] { (a, endA), (b, endB) })
            {
                var knockdownScore = await client.WaitFor(frame => IsTeamScore(frame, 1, 0),
                    "knockdown without a team kill", cursor);
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(knockdownScore.Payload.AsSpan(1)));
            }
            peer = relayB.Cursor;
            var popped = Body("Unknown38277", new()
                { ["unknown_0"] = true, ["victim"] = actorB, ["actor"] = actorA });
            await relayA.Send(relayTable.C2S(finishName), popped);
            Assert.Equal(popped, (await relayB.WaitFor(relayTable.S2C(finishName), peer)).Payload);
            foreach (var (client, cursor) in new[] { (a, endA), (b, endB) })
            {
                var liveScore = await client.WaitFor(frame => IsTeamScore(frame, 1, 1),
                    "pop credited to the attacking team", cursor);
                Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(liveScore.Payload.AsSpan(1)));
            }
            peer = relayA.Cursor;
            await relayB.Send(relayTable.C2S("Unknown32836"), Body("Dead", new() { ["actor"] = actorB }));
            await relayA.WaitFor(relayTable.S2C("Unknown32836"), peer);
            var resetCursor = b.Cursor;
            await b.Send(table.Find(PacketDirection.ClientToServer, "gcpRespawn") ?? table.C2S("Unknown602"),
                Body("RoundResetRequest", new() { ["actor"] = actorB }));
            await b.WaitFor(table.Find(PacketDirection.ServerToClient, "gspRespawn") ?? table.S2C("Unknown632"), resetCursor);
            await relayB.Send(relayTable.C2S("Unknown32926"), Body("RoundResetConfirmation", new() { ["actor"] = actorB }));
            // Native-life rounds finish on the server clock, not the per-death client stage report.
            await a.Send(table.Find(PacketDirection.ClientToServer, "gcpReqEndStage_") ?? table.C2S("Unknown625"), []);
            foreach (var (client, cursor) in new[] { (a, endA), (b, endB) })
            {
                await client.WaitFor(ResultOpcode("gspRoundResultOpen_", "Unknown612"), cursor);
                var board = await client.WaitFor(ResultOpcode("gspRoundScoreboard_", "Unknown614"), cursor);
                Assert.Equal((byte)2, board.Payload[0]);
                Assert.Equal((byte)1, board.Payload[1]);
                Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(board.Payload.AsSpan(2)));
                Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(board.Payload.AsSpan(6)));
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(board.Payload.AsSpan(18)));
                Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(board.Payload.AsSpan(30)));
                await client.WaitFor(ResultOpcode("gspRoundRewardStage_", "Unknown615"), cursor);
                await client.WaitFor(ResultOpcode("gspRoundStageReset_", "Unknown620"), cursor);
            }
        }

        bool IsTeamScore(SimFrame frame, byte team, ushort total) =>
            frame.Opcode == table.S2C("gspUpdateScoreData") && frame.Payload.Length >= 3
            && frame.Payload[0] == team && BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(1)) == total;

        async Task Probe(Socket socket, SimActor relay)
        {
            var cursor = relay.Cursor;
            await socket.SendToAsync(Frame(20001, cookies[relay]), SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, server.RelayPort), server.Ct);
            Assert.Empty(await ReceiveUdp(socket, relayTable.ForConnection(OpcodeConnection.Udp).S2C("rspUdpBypassVerify_"), relay));
            await SendUdp(socket, relay, 20002, []);
            await relay.WaitFor(relayTable.S2C("rspUdpBypassReady_"), cursor);
        }

        async Task SendUdp(Socket socket, SimActor relay, ushort opcode, byte[] body) =>
            await socket.SendToAsync(Frame(opcode, cookies[relay].Concat(body).ToArray()), SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, server.RelayPort), server.Ct);

        async Task<byte[]> ReceiveUdp(Socket socket, ushort opcode, SimActor relay)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var buffer = new byte[4096];
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                Assert.Equal(new IPEndPoint(IPAddress.Loopback, server.RelayPort), received.RemoteEndPoint);
                Assert.True(received.ReceivedBytes >= 6);
                var actual = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
                if (actual == relayTable.ForConnection(OpcodeConnection.Udp).S2C("rspUdpBypassVerify_") && actual != opcode)
                {
                    Assert.Equal(6, received.ReceivedBytes);
                    await SendUdp(socket, relay, 20002, []);
                    continue;
                }
                Assert.Equal(opcode, actual);
                return buffer.AsSpan(6, received.ReceivedBytes - 6).ToArray();
            }
        }

        string FeedName(string key) => (string)((IReadOnlyDictionary<string, object?>)feed[key]!)["opcode"]!;

        ushort ResultOpcode(string name, string unnamed) =>
            table.Find(PacketDirection.ServerToClient, name) ?? table.S2C(unnamed);

        byte[] Body(string structure, Dictionary<string, object?> values) => PacketCodec.Write(Tables, build, structure, values);

        async Task Login(SimActor client)
        {
            var hello = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(hello, TestWireDialects.Get(build).Profile.ClientVersionMajor);
            await client.Send(table.C2S(PatchRequestName(table)), hello);
            await client.WaitFor(table.S2C("gspRepGamePatch"), 0);
            await client.Send(table.C2S("gcpReqLogin"), SimActor.LauncherLogin(client.Name));
            await client.WaitFor(table.S2C("gspGotoRoom"), 0);
        }

        (int Slot, int User, string Name) Seat(SimFrame frame)
        {
            var decoded = PacketCodec.Read(Tables, build, "RoomSeatUpdate", frame.Payload);
            var user = (IReadOnlyDictionary<string, object?>)decoded["user"]!;
            return (Convert.ToInt32(decoded["slot"]), Convert.ToInt32(user["user_number"]), (string)user["nickname"]!);
        }

        async Task<SimActor> Load(SimActor client, (int Slot, int User, string Name) seat)
        {
            var cursor = client.Cursor;
            var actor = 1000000 + 100000 * seat.Slot;
            await client.Send(table.C2S("gcpReqAddObject"), Body("RoundObjectAddRequest", new() { ["object_id"] = actor }));
            await client.Send(table.C2S("gcpGameReportMapLoadingComplete"), Body("RoundMapLoadingReport", new()));
            var offer = await client.WaitFor(table.S2C("gspConnectToRelayServer"), cursor);
            var key = PacketCodec.Read(Tables, build, "RelayConnectionOffer", offer.Payload)["key"];
            var relay = await server.ConnectRelayAsync(client.Name);
            await relay.Send(relayTable.C2S("rcpReqJoinRelaySession"), Body("RelayJoinRequest", new()
            {
                ["key"] = key, ["unknown_n3"] = seat.User, ["unknown_n4"] = seat.Name,
            }));
            await relay.WaitFor(relayTable.S2C("rspRelayJoinAccepted_"), 0);
            await relay.Send(relayTable.C2S("rcpReqReportSceneData_"), Body("RelaySceneReport", new()
            {
                ["unknown_28"] = seat.User, ["entity_id"] = actor, ["nickname"] = seat.Name,
            }));
            await relay.WaitFor(relayTable.S2C("rspRelaySceneAccepted_"), 0);

            await client.Send(table.C2S("gcpReportRelayConnected"), Body("RelayConnectionReport", new() { ["unknown_4"] = true }));
            return relay;
        }
    }

    private static Socket OpenUdp()
    {
        for (var port = 7600; port < 7700; port++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); return socket; }
            catch (SocketException) { socket.Dispose(); }
        }
        throw new InvalidOperationException("No isolated UDP scenario port available.");
    }

    private static byte[] Frame(ushort opcode, byte[] body)
    {
        var frame = new byte[6 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, opcode);
        body.CopyTo(frame, 6);
        return frame;
    }

    private static byte[] Movement(int actor)
    {
        // cn-774 sub_489FC0 / tw-454 sub_4771A0: actor, position, three packed bytes and tick.
        var body = new byte[21];
        BinaryPrimitives.WriteInt32LittleEndian(body, actor);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), 2.5f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(12), 3.5f);
        body[16] = 4; body[17] = 5; body[18] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(19), 7);
        return body;
    }

    private static List<PluginRow> Rows(string build, int gamePort, int relayPort, bool udp)
    {
        var profile = TestWireDialects.Get(build).Profile;
        var rows = new List<PluginRow>();
        foreach (var plugin in new[] { "core.protocol-registry", "core.session-registry", "core.packet-router", "core.message-router",
            "core.client-profiles", "core.opcode-tables", "core.wire-dialects", "persistence.memory" })
            Add(plugin, plugin);
        Add("dialect", "dialect", TestWireDialects.RowConfig(build));
        var opcodes = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"), profile);
        Add("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(RepositoryRoot(), "profiles", build, "opcodes.yaml"),
            ["patch_request_name"] = PatchRequestName(opcodes), ["bootstrap_status"] = 2 });
        Add("dialect.messages", "dialect.messages", TestWireDialects.MessagesConfig(new()
        {
            ["protocol"] = build + "/game", ["login"] = true, ["main_menu_init"] = true,
            ["inventory"] = true, ["equipment"] = true, ["equipment_view"] = true, ["session_signature"] = true,
        }));
        Add("dialect.rooms", "dialect.rooms", TestWireDialects.ModernRoomsConfig(new()
        {
            ["protocol"] = build + "/game", ["relay_protocol"] = build + "/relay", ["waiting_rooms_only"] = false,
            ["udp_protocol"] = udp ? build + "/udp" : "",
        }));
        Add("lobby", "lobby", new() { ["protocol"] = build + "/game", ["auto_create_players"] = true, ["authentication"] = "open",
            ["inventory_source"] = "mysql", ["money_source"] = "mysql" });
        Add("equipment", "equipment", new() { ["protocol"] = build + "/game", ["client_profile"] = build, ["version_major"] = profile.ClientVersionMajor });
        Add("inventoryview", "inventoryview", new() { ["protocol"] = build + "/game", ["client_profile"] = build,
            ["version_major"] = profile.ClientVersionMajor, ["enabled"] = true, ["publish_equipped_view_push"] = true });
        Add("game-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{gamePort}", ["protocol"] = build + "/game" });
        Add("relay-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{relayPort}", ["protocol"] = build + "/relay" });
        if (udp)
        {
            Add("udp-listener", "net.udp", new() { ["listen"] = $"127.0.0.1:{relayPort}", ["protocol"] = build + "/udp" });
            Add("match-udp-directory", "match-udp-directory", new() { ["protocol"] = build + "/relay" });
            Add("match-udp-relay-setup", "match-udp-relay-setup", new()
            { ["game_protocol"] = build + "/game", ["relay_protocol"] = build + "/relay" });
        }
        Add("dialect.match", "dialect.match", new() { ["protocol"] = build + "/relay", ["native_handshake"] = "capability", ["packets_directory"] = SharedDialectRows.PacketsDirectory() });
        Add("dialect.match-offers", "dialect.match-offers", new() { ["protocol"] = build + "/game", ["packets_directory"] = SharedDialectRows.PacketsDirectory() });
        Add("dialect.match-replies", "dialect.match-replies", new() { ["protocol"] = build + "/relay", ["packets_directory"] = SharedDialectRows.PacketsDirectory(), ["udp_enabled"] = udp, ["udp_protocol"] = build + "/udp" });
        Add("match-connection", "match-connection", new() { ["protocol"] = build + "/relay", ["capability_authentication"] = true, ["require_capability_scene_report"] = true });
        Add("ingame", "ingame", new()
        {
            ["game_protocol"] = build + "/game", ["relay_protocol"] = build + "/relay", ["relay_advertise_ipv4"] = "127.0.0.1", ["relay_advertise_port"] = relayPort,
            ["start_matches"] = true, ["native_match_relay"] = true, ["server_map_draw"] = true, ["assign_spawn_teams_on_start"] = true,
            ["native_life_mode_ids"] = new object?[] { 1 }, ["start_timer_ms"] = 50, ["start_flag0"] = 1, ["start_flag2"] = 1,
            ["result_scoreboard_delay_ms"] = 100, ["result_reward_delay_ms"] = 200, ["result_close_grace_ms"] = 300,
            ["send_plaza_snapshot"] = true, ["send_room_list"] = true, ["republish_room_ui_on_join"] = true,
            ["republish_room_ui_on_position"] = true, ["relay_character_positions"] = true,
            ["room_install_state"] = 2, ["room_state"] = 2, ["prime_session_state_before_install"] = false,
            ["plaza_enter_requires_finished_login"] = true, ["score_based_rewards"] = true,
        });
        return rows;

        void Add(string id, string plugin, Dictionary<string, object?>? config = null) => rows.Add(new()
        {
            Id = id, Plugin = plugin, Source = "native-modern-round-scenario", Family = "modern", Config = config,
            ClientBuild = new(profile.Region, profile.ClientVersionMajor, profile.ResourceVersion), Lineage = Tables.LineageOf(build).ToArray(),
        });
    }

    private static string PatchRequestName(IOpcodeTable table) => table.All.Single(entry =>
        entry.Direction == PacketDirection.ClientToServer && entry.Name is "gcpFirstConnect" or "gcpReqGamePatch").Name;

    private static string RepositoryRoot() => Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
}
