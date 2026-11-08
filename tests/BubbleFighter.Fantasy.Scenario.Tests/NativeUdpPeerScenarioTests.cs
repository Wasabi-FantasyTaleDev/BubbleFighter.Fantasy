using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Composes early native peer setup with encrypted game/relay streams and real UDP discovery.</summary>
[Collection("Native match listener ports")]
public sealed class NativeUdpPeerScenarioTests
{
    [Theory]
    [InlineData("cn-279", false, false)]
    [InlineData("cn-324", false, false)]
    [InlineData("cn-406", false, false)]
    [InlineData("id-400", false, false)]
    [InlineData("cn-404", false, false)]
    [InlineData("cn-408", false, false)]
    [InlineData("cn-705", false, false)]
    [InlineData("cn-706", false, false)]
    [InlineData("kr-812", true, false)]
    [InlineData("cn-717", true, false)]
    [InlineData("id-1809", true, false)]
    [InlineData("cn-279", false, true)]
    [InlineData("cn-324", false, true)]
    [InlineData("cn-406", false, true)]
    [InlineData("id-400", false, true)]
    public async Task TwoNativePeersDiscoverAcknowledgeAndRestoreRelayDelivery(string build, bool bypass, bool delayedDiscovery)
    {
        var packets = SharedDialectRows.PacketsDirectory();
        var root = Directory.GetParent(packets)!.FullName;
        var tables = PacketTables.Load(packets);
        var profile = TestWireDialects.Get(build).Profile;
        var ops = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var relayOps = ops.ForConnection(OpcodeConnection.Relay);
        await using var server = await ScenarioServer.StartAsync(Rows, build, minimumPort: 7500);
        using var socketA = OpenUdp();
        using var socketB = OpenUdp();
        var a = await server.ConnectAsync("peerA" + profile.ClientVersionMajor);
        var b = await server.ConnectAsync("peerB" + profile.ClientVersionMajor);
        await Login(a);
        await Login(b);
        foreach (var client in new[] { a, b })
        {
            var cursor = client.Cursor;
            await client.Send(ops.C2S("gcpReqGotoLobby"), []);
            await client.WaitFor(ops.S2C("gspGotoLobby"), cursor);
        }
        var created = a.Cursor;
        await a.Send(ops.C2S("gcpReqCreateSession"), Body("RoomCreateRequest", new()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "native-peers", ["session_state"] = 2, ["open_seats"] = 4,
                ["game_setting"] = new Dictionary<string, object?> { ["mode"] = 1 },
            },
            ["password"] = "",
        }));
        var room = await a.WaitFor(ops.S2C("gspGotoRoom"), created);
        var install = PacketCodec.Read(tables, build, "gspGotoRoom", room.Payload);
        var roomId = Convert.ToInt32(((IReadOnlyDictionary<string, object?>)install["session_setting"]!)["room_id"]);
        var seatA = InstalledSeat(room);
        var joined = b.Cursor;
        await b.Send(ops.C2S("gcpReqJoinSession"), Body("RoomJoinRequest", new() { ["room_id"] = (int)roomId, ["password"] = "" }));
        var seatB = InstalledSeat(await b.WaitFor(ops.S2C("gspGotoRoom"), joined));
        var earlyDiscovery = !bypass && tables.CapabilityOf(build, "udp_peer_discovery_in_room");
        uint identityA = 0, identityB = 0;
        if (earlyDiscovery)
        {
            if (delayedDiscovery)
            {
                await a.WaitFor(ops.S2C("gspTryHolePunching"), created);
                await b.WaitFor(ops.S2C("gspTryHolePunching"), joined);
            }
            else await PreparePeersAsync(created, joined);
            Assert.DoesNotContain(a.Since(created), frame => frame.Opcode == ops.S2C("gspUdpPeerPunchStart_"));
            Assert.DoesNotContain(b.Since(joined), frame => frame.Opcode == ops.S2C("gspUdpPeerPunchStart_"));
        }
        var ready = a.Cursor;
        await b.Send(ops.C2S("gcpRoomReqReadyStatus"), Body("RoomReadyRequest", new() { ["ready"] = true }));
        await a.WaitFor(frame => frame.Opcode == ops.S2C("gspRoomSetSlot") && Seat(frame).Name == b.Name && Ready(frame),
            "ready seat", ready);
        var startA = a.Cursor;
        var startB = b.Cursor;
        await a.Send(ops.C2S("gcpRoomReqEnterGame"), Body("RoomEnterGameRequest", new()
        { ["map_key"] = "rel_Village01_Byway", ["death_match_time_limit_ms"] = 60000 }));
        await a.WaitFor(ops.S2C("gspRoomEnterGame"), startA);
        await b.WaitFor(ops.S2C("gspRoomEnterGame"), startB);
        if (delayedDiscovery) await PreparePeersAsync(created, joined);
        var relayA = await Load(a, seatA);
        if (bypass)
            Assert.DoesNotContain(relayA.Since(0), frame => frame.Opcode == relayOps.S2C("rspUdpBypassInfo"));
        var relayB = await Load(b, seatB);
        await a.WaitFor(ops.S2C("gspGameStartGame"), startA);
        await b.WaitFor(ops.S2C("gspGameStartGame"), startB);
        if (bypass)
        {
            var tokens = new List<byte[]>();
            foreach (var (relay, socket) in new[] { (relayA, socketA), (relayB, socketB) })
            {
                var offer = await relay.WaitFor(relayOps.S2C("rspUdpBypassInfo"), 0);
                Assert.Equal(6, offer.Payload.Length);
                var token = offer.Payload[2..];
                tokens.Add(token);
                var endpoint = new IPEndPoint(IPAddress.Loopback, BinaryPrimitives.ReadUInt16LittleEndian(offer.Payload));
                await socket.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                    relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassAck"), token), SocketFlags.None, endpoint, server.Ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var buffer = new byte[1400];
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                Assert.Equal(endpoint, received.RemoteEndPoint);
                Assert.Equal(relayOps.ForConnection(OpcodeConnection.Udp).S2C("rspUdpBypassVerify_"),
                    ProtocolDatagramCodec.Read(tables, build, buffer.AsMemory(0, received.ReceivedBytes)).Opcode);
                Assert.DoesNotContain(relay.Since(0), frame => frame.Opcode == relayOps.S2C("rspUdpBypassReady_"));
                await socket.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                    relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassVerifyAck"), token), SocketFlags.None, endpoint, server.Ct);
                await relay.WaitFor(relayOps.S2C("rspUdpBypassReady_"), 0);
            }
            Assert.NotEqual(tokens[0], tokens[1]);
            var payload = Body("MovementReport", new() { ["unknown_4"] = 1000000 + seatA.Slot * 100000 });
            var port = BinaryPrimitives.ReadUInt16LittleEndian((await relayA.WaitFor(relayOps.S2C("rspUdpBypassInfo"), 0)).Payload);
            foreach (var actor in new[] { -1, 1000000 + seatB.Slot * 100000 })
                await socketA.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                    relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassWanderLean"),
                    tokens[0].Concat(Body("MovementReport", new() { ["unknown_4"] = actor })).ToArray()),
                    SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port), server.Ct);
            await socketA.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassWanderLean"), tokens[0].Concat(payload).ToArray()),
                SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port), server.Ct);
            await ReceiveMovementAsync(socketB, relayOps.S2C("gppWanderLean"), payload);
            var delta = Body("MovementSample", new()
            {
                ["unknown_4"] = 1000000 + seatA.Slot * 100000, ["unknown_8"] = 33, ["mask"] = 0x3F,
                ["unknown_18"] = 123, ["unknown_20"] = 456, ["unknown_22"] = 789,
                ["unknown_24"] = 12, ["unknown_25"] = 34, ["unknown_26"] = 56,
            });
            await socketA.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassWanderLeanOptimize"), tokens[0].Concat(delta).ToArray()),
                SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port), server.Ct);
            await ReceiveMovementAsync(socketB, relayOps.S2C("gppWanderLeanOptimize"), delta);

            await relayB.Send(relayOps.C2S("rcpReqUdpBypassDisable"), []);
            var reply = Body("MovementReport", new() { ["unknown_4"] = 1000000 + seatB.Slot * 100000 });
            // The same ordered relay stream processes disable before this return movement.
            await relayB.Send(relayOps.C2S("gppWanderLean"), reply);
            await ReceiveMovementAsync(socketA, relayOps.S2C("gppWanderLean"), reply);
            var fallbackCursor = relayB.Cursor;
            await socketA.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                relayOps.ForConnection(OpcodeConnection.Udp).C2S("rcpUdpBypassWanderLean"), tokens[0].Concat(payload).ToArray()),
                SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port), server.Ct);
            Assert.Equal(payload, (await relayB.WaitFor(relayOps.S2C("gppWanderLean"), fallbackCursor)).Payload);
            return;

            async Task ReceiveMovementAsync(Socket receiver, ushort opcode, byte[] expected)
            {
                using var movementTimeout = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
                movementTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var datagram = new byte[1400];
                while (true)
                {
                    var received = await receiver.ReceiveFromAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), movementTimeout.Token);
                    var frame = ProtocolDatagramCodec.Read(tables, build, datagram.AsMemory(0, received.ReceivedBytes));
                    if (frame.Opcode != opcode) continue;
                    Assert.Equal(expected, frame.Body.ToArray());
                    break;
                }
            }
        }
        if (!earlyDiscovery) await PreparePeersAsync(startA, startB);
        await a.WaitFor(ops.S2C("gspUdpPeerPunchStart_"), startA);
        await Punch(socketA, socketB, identityA, identityB);
        await Punch(socketB, socketA, identityB, identityA);
        await b.WaitFor(ops.S2C("gspUdpPeerPunchFinish_"), startB);
        var membership = Body("UdpPeerRelayMembership", new() { ["peer_identity"] = identityB });
        var unknownMembership = Body("UdpPeerRelayMembership", new() { ["peer_identity"] = 0u });
        var selfMembership = Body("UdpPeerRelayMembership", new() { ["peer_identity"] = identityA });
        await relayA.Send(relayOps.C2S("rcpReqUnRegisterPeer"), unknownMembership);
        await relayA.Send(relayOps.C2S("rcpReqUnRegisterPeer"), selfMembership);
        await relayA.Send(relayOps.C2S("rcpReqUnRegisterPeer"), membership);
        await relayA.Send(relayOps.C2S("rcpReqRegisterPeer"), unknownMembership);
        await relayA.Send(relayOps.C2S("rcpReqRegisterPeer"), selfMembership);
        // The ordered relay stream applies membership before the next movement record.
        var move = Body("MovementReport", new() { ["unknown_4"] = 1000000 + seatA.Slot * 100000 });
        // cn-279 sub_703430 and cn-324 sub_6F97B0 acknowledge after more than 32 records.
        for (var sample = 0; sample < 33; sample++)
        {
            await SendPeer(socketA, socketB, 32885, identityA, move);
            var received = await ReceivePeer(socketB, socketA, 32885, identityA);
            Assert.Equal(move, received);
        }
        await SendPeer(socketB, socketA, 32770, identityB, []);
        Assert.Empty(await ReceivePeer(socketA, socketB, 32770, identityB));
        var movements = new List<(ushort Code, byte[] Body)> { (relayOps.C2S("gppWanderLean"), move) };
        if (tables.CapabilityOf(build, "udp_peer_optimized_movement"))
            movements.Add((relayOps.C2S("gppWanderLeanOptimize"), Body("MovementSample", new()
            {
                ["unknown_4"] = 1000000 + seatA.Slot * 100000, ["unknown_8"] = 33,
                ["mask"] = 0x3F, ["unknown_18"] = 123, ["unknown_20"] = 456, ["unknown_22"] = 789,
                ["unknown_24"] = 12, ["unknown_25"] = 34, ["unknown_26"] = 56,
            })));
        foreach (var movement in movements)
        {
            await SendPeer(socketA, socketB, movement.Code, identityA, movement.Body);
            Assert.Equal(movement.Body, await ReceivePeer(socketB, socketA, movement.Code, identityA));
            var directCursor = relayB.Cursor;
            await relayA.Send(movement.Code, movement.Body);
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
            silence.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await relayB.WaitFor(movement.Code, directCursor).WaitAsync(silence.Token));
        }
        await relayA.Send(relayOps.C2S("rcpReqRegisterPeer"), membership);
        var missingActor = Body("MovementReport", new() { ["unknown_4"] = uint.MaxValue });
        var foreignActor = Body("MovementReport", new() { ["unknown_4"] = 1000000 + seatB.Slot * 100000 });
        foreach (var movement in movements)
        {
            var fallbackCursor = relayB.Cursor;
            await relayA.Send(relayOps.C2S("gppWanderLean"), missingActor);
            await relayA.Send(relayOps.C2S("gppWanderLean"), foreignActor);
            await relayA.Send(movement.Code, movement.Body);
            Assert.Equal(movement.Body, (await relayB.WaitFor(movement.Code, fallbackCursor)).Payload);
        }
        await a.Send(ops.C2S("gcpReportHolePunchingEnd"), Body("UdpPeerPunchReported", new()
        { ["occupied_seats"] = 0u, ["connected_peers"] = 1u }));
        var gameFallbackCursor = relayB.Cursor;
        await a.Send(ops.C2S("gppWanderLean"), missingActor);
        await a.Send(ops.C2S("gppWanderLean"), foreignActor);
        await a.Send(ops.C2S("gppWanderLean"), move);
        Assert.Equal(move, (await relayB.WaitFor(relayOps.S2C("gppWanderLean"), gameFallbackCursor)).Payload);

        async Task PreparePeersAsync(int cursorA, int cursorB)
        {
            var offerA = await a.WaitFor(ops.S2C("gspTryHolePunching"), cursorA);
            var offerB = await b.WaitFor(ops.S2C("gspTryHolePunching"), cursorB);
            identityA = BinaryPrimitives.ReadUInt32LittleEndian(offerA.Payload.AsSpan(2));
            identityB = BinaryPrimitives.ReadUInt32LittleEndian(offerB.Payload.AsSpan(2));
            Assert.NotEqual(identityA, identityB);
            foreach (var pair in new[] { (socketA, identityA), (socketB, identityB) })
            {
                var local = (IPEndPoint)pair.Item1.LocalEndPoint!;
                await pair.Item1.SendToAsync(ProtocolDatagramCodec.Write(tables, build,
                    ops.ForConnection(OpcodeConnection.Udp).C2S("upTrySendUdpToServer"), Body("UdpPeerDiscovery", new()
                    { ["probe_identity"] = pair.Item2, ["local_ipv4"] = 0x0100007Fu, ["local_port"] = local.Port })),
                    SocketFlags.None, new IPEndPoint(IPAddress.Loopback,
                        BinaryPrimitives.ReadUInt16LittleEndian(offerA.Payload)), server.Ct);
            }
            await a.WaitFor(ops.S2C("gspUdpPeerTableEnd_"), cursorA);
            await b.WaitFor(ops.S2C("gspUdpPeerTableEnd_"), cursorB);
            foreach (var client in new[] { a, b })
            {
                var rows = client.Since(client == a ? cursorA : cursorB).Where(frame => frame.Opcode == ops.S2C("gspTableSetHolePunching"))
                    .Select(frame => PacketCodec.Read(tables, build, "UdpPeerTableRow", frame.Payload)).ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Equal(new[] { identityA, identityB }.Order(), rows.Select(row => unchecked((uint)Convert.ToInt32(row["peer_identity"]))).Order());
            }
            await a.Send(ops.C2S("gcpReportTableSetHolePunchingEnd"), []);
            await b.Send(ops.C2S("gcpReportTableSetHolePunchingEnd"), []);
        }

        byte[] Body(string structure, Dictionary<string, object?> values) => PacketCodec.Write(tables, build, structure, values);
        async Task Punch(Socket sender, Socket target, uint senderId, uint targetId)
        {
            // cn-279 sub_701BD0/sub_7030A0: UDP6/7 carry identity and the attempted local address.
            var inner = Body("UdpPeerInnerPunch", new() { ["peer_identity"] = senderId, ["local_ipv4"] = 0x0100007Fu });
            await SendPeer(sender, target, 6, 0, inner);
            Assert.Equal(inner, await ReceivePeer(target, sender, 6, 0));
            var reply = Body("UdpPeerInnerPunch", new() { ["peer_identity"] = targetId, ["local_ipv4"] = 0x0100007Fu });
            await SendPeer(target, sender, 7, 0, reply);
            Assert.Equal(reply, await ReceivePeer(sender, target, 7, 0));
            // cn-279 sub_702EE0/sub_702E10: UDP8/9 exchange the respective sender identities.
            var outer = Body("UdpPeerOuterPunch", new() { ["peer_identity"] = senderId });
            await SendPeer(sender, target, 8, 0, outer);
            Assert.Equal(outer, await ReceivePeer(target, sender, 8, 0));
            outer = Body("UdpPeerOuterPunch", new() { ["peer_identity"] = targetId });
            await SendPeer(target, sender, 9, 0, outer);
            Assert.Equal(outer, await ReceivePeer(sender, target, 9, 0));
        }
        async Task SendPeer(Socket sender, Socket target, ushort opcode, uint identity, byte[] body)
        {
            var header = PacketCodec.Write(tables, build, "UdpRelayDatagramPrefix", new Dictionary<string, object?>
            { ["opcode"] = opcode, ["peer_identity"] = identity, ["unknown_2"] = build == "cn-279" ? 0u : identity });
            await sender.SendToAsync(header.Concat(body).ToArray(), SocketFlags.None, target.LocalEndPoint!, server.Ct);
        }
        async Task<byte[]> ReceivePeer(Socket target, Socket sender, ushort opcode, uint identity)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(server.Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var buffer = new byte[1400];
            var received = await target.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            Assert.Equal(sender.LocalEndPoint, received.RemoteEndPoint);
            var packet = buffer.AsMemory(0, received.ReceivedBytes);
            var header = PacketCodec.Read(tables, build, "UdpRelayDatagramPrefix",
                packet.Span[..(build == "cn-279" ? 8 : 6)]);
            Assert.Equal(identity, unchecked((uint)Convert.ToInt32(header[build == "cn-279" ? "peer_identity" : "unknown_2"])));
            var frame = ProtocolDatagramCodec.Read(tables, build, packet);
            Assert.Equal(opcode, frame.Opcode);
            return frame.Body.ToArray();
        }
        (int Slot, string Name) InstalledSeat(SimFrame frame)
        {
            var fields = PacketCodec.Read(tables, build, "gspGotoRoom", frame.Payload);
            var slot = Convert.ToInt32(fields["local_slot"]);
            var users = ((IEnumerable<object?>)fields["user_slots"]!).Cast<IReadOnlyDictionary<string, object?>>().ToArray();
            return (slot, (string)users[slot]["nickname"]!);
        }
        (int Slot, string Name) Seat(SimFrame frame)
        {
            var row = PacketCodec.Read(tables, build, "RoomSeatUpdate", frame.Payload);
            return (Convert.ToInt32(row["slot"]), (string)((IReadOnlyDictionary<string, object?>)row["user"]!)["nickname"]!);
        }
        bool Ready(SimFrame frame)
        {
            var row = PacketCodec.Read(tables, build, "RoomSeatUpdate", frame.Payload);
            return Convert.ToBoolean(((IReadOnlyDictionary<string, object?>)row["user"]!)["ready"]);
        }
        async Task Login(SimActor client)
        {
            var hello = new byte[96];
            BinaryPrimitives.WriteInt32LittleEndian(hello, profile.ClientVersionMajor);
            await client.Send(ops.C2S("gcpFirstConnect"), hello);
            await client.WaitFor(ops.S2C("gspRepGamePatch"), 0);
            // cn-279 sub_420100 and cn-324 sub_583DD0 stop after the adapter list.
            var name = System.Text.Encoding.Unicode.GetBytes(client.Name);
            var loginValues = (IReadOnlyDictionary<string, object?>)tables.ValuesOf(build)["login"]!;
            var trailer = bool.Parse((string)loginValues["request_trailer"]!) ? 8 : 0;
            var token = bool.Parse((string)loginValues["request_token"]!) ? 4 : 0;
            var login = new byte[16 + name.Length + trailer + token];
            BinaryPrimitives.WriteInt32LittleEndian(login, client.Name.Length);
            name.CopyTo(login, 4);
            await client.Send(ops.C2S("gcpReqLogin"), login);
            await client.WaitFor(ops.S2C("gspGotoRoom"), 0);
        }
        async Task<SimActor> Load(SimActor client, (int Slot, string Name) seat)
        {
            var cursor = client.Cursor;
            await client.Send(ops.C2S("gcpGameReportMapLoadingComplete"), Body("RoundMapLoadingReport", new()));
            var offer = await client.WaitFor(ops.S2C("gspConnectToRelayServer"), cursor);
            var key = PacketCodec.Read(tables, build, "RelayConnectionOffer", offer.Payload)["key"];
            var relay = await server.ConnectRelayAsync(client.Name);
            await relay.Send(relayOps.C2S("rcpReqJoinRelaySession"), Body("RelayJoinRequest", new() { ["key"] = key }));
            await relay.WaitFor(relayOps.S2C("rspRelayJoinAccepted_"), 0);
            if (tables.CapabilityOf(build, "relay_validation_report"))
            {
                await relay.Send(relayOps.C2S("rcpReqReportHackCheckData"), Body("RelayValidationReport", new()
                { ["entity_id"] = 1000000 + seat.Slot * 100000, ["nickname"] = seat.Name }));
                await relay.WaitFor(relayOps.S2C("rspRelaySceneAccepted_"), 0);
            }
            await client.Send(ops.C2S("gcpReportRelayConnected"), Body("RelayConnectionReport", new() { ["unknown_4"] = true }));
            return relay;
        }
        List<PluginRow> Rows(int gamePort, int relayPort)
        {
            var rows = new List<PluginRow>();
            foreach (var plugin in new[] { "core.protocol-registry", "core.session-registry", "core.packet-router", "core.message-router",
                "core.client-profiles", "core.opcode-tables", "core.wire-dialects", "persistence.memory" }) Add(plugin, plugin);
            Add("dialect", "dialect", TestWireDialects.RowConfig(build));
            Add("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(root, "profiles", build, "opcodes.yaml"),
                ["patch_request_name"] = "gcpFirstConnect", ["bootstrap_status"] = 2 });
            Add("dialect.messages", "dialect.messages", new() { ["protocol"] = build + "/game", ["login"] = true,
                ["inventory"] = true, ["equipment"] = true, ["equipment_view"] = true, ["packets_directory"] = packets });
            Add("dialect.rooms", "dialect.rooms", new() { ["protocol"] = build + "/game", ["packets_directory"] = packets,
                ["select_match_relay"] = true, ["udp_enabled"] = bypass, ["udp_protocol"] = bypass ? build + "/udp" : "" });
            Add("lobby", "lobby", new() { ["protocol"] = build + "/game", ["auto_create_players"] = true,
                ["authentication"] = "open", ["inventory_source"] = "mysql", ["money_source"] = "mysql" });
            Add("equipment", "equipment", new() { ["protocol"] = build + "/game", ["client_profile"] = build,
                ["version_major"] = profile.ClientVersionMajor });
            Add("inventoryview", "inventoryview", new() { ["protocol"] = build + "/game", ["client_profile"] = build,
                ["version_major"] = profile.ClientVersionMajor, ["enabled"] = true, ["publish_equipped_view_push"] = true });
            Add("game-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{gamePort}", ["protocol"] = build + "/game" });
            Add("relay-listener", "net.tcp", new() { ["listen"] = $"127.0.0.1:{relayPort}", ["protocol"] = build + "/relay" });
            Add("udp-listener", "net.udp", new() { ["listen"] = $"127.0.0.1:{relayPort}", ["protocol"] = build + "/udp" });
            Add("dialect.match", "dialect.match", new() { ["protocol"] = build + "/relay", ["packets_directory"] = packets });
            Add("dialect.match-offers", "dialect.match-offers", new() { ["protocol"] = build + "/game", ["packets_directory"] = packets });
            Add("dialect.match-replies", "dialect.match-replies", new() { ["protocol"] = build + "/relay", ["packets_directory"] = packets,
                ["udp_enabled"] = bypass, ["udp_protocol"] = bypass ? build + "/udp" : "" });
            Add("match-connection", "match-connection", new() { ["protocol"] = build + "/relay", ["capability_authentication"] = true,
                ["require_capability_scene_report"] = tables.CapabilityOf(build, "relay_validation_report") });
            if (bypass)
            {
                Add("match-udp-directory", "match-udp-directory", new() { ["protocol"] = build + "/relay" });
                Add("match-udp-relay-setup", "match-udp-relay-setup", new()
                { ["game_protocol"] = build + "/game", ["relay_protocol"] = build + "/relay" });
            }
            else
            {
                Add("dialect.udp-peers", "dialect.udp-peers", new() { ["protocol"] = build + "/game", ["packets_directory"] = packets });
                Add("match-udp-peers", "match-udp-peers", new() { ["protocol"] = build + "/game", ["udp_protocol"] = build + "/udp",
                    ["punch_window_ms"] = 100 });
            }
            Add("ingame", "ingame", new() { ["game_protocol"] = build + "/game", ["relay_protocol"] = build + "/relay",
                ["relay_advertise_ipv4"] = "127.0.0.1", ["relay_advertise_port"] = relayPort, ["native_match_relay"] = true,
                ["start_matches"] = true, ["start_timer_ms"] = 50, ["assign_spawn_teams_on_start"] = true,
                ["send_room_list"] = true, ["send_plaza_snapshot"] = true, ["republish_room_ui_on_join"] = true });
            return rows;
            void Add(string id, string plugin, Dictionary<string, object?>? config = null) => rows.Add(new()
            {
                Id = id, Plugin = plugin, Source = "native-udp-peer-scenario", Config = config, Family = "legacy",
                ClientBuild = new(profile.Region, profile.ClientVersionMajor, profile.ResourceVersion), Lineage = tables.LineageOf(build).ToArray(),
            });
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
        throw new InvalidOperationException("No isolated UDP peer scenario port available.");
    }
}
