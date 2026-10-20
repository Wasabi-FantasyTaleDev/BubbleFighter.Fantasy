using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the server-driven room-to-match start chain behind <see
/// cref="InGameOptions.StartMatches"/>.
/// </summary>
public sealed class MatchStartChainTests
{
    [Fact]
    public async Task MissingSignatureAdapterDoesNotAdvanceRoomState()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Start.StartMatchAsync(handle.Room, CancellationToken.None).AsTask());
        Assert.Equal(RoomPhase.Waiting, handle.Room.Phase);
        Assert.Empty(host.Opcodes);
        Assert.Empty(guest.Opcodes);
    }

    [Fact]
    public async Task SceneMoveWaitsForSignatureSendToComplete()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        var sender = new GatedSignatureSender();
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: sender);
        var start = loop.Start.StartMatchAsync(handle.Room, CancellationToken.None).AsTask();
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(start.IsCompleted);
        Assert.DoesNotContain((ushort)510, host.Opcodes);
        Assert.DoesNotContain((ushort)510, guest.Opcodes);
        sender.Release.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var session in new[] { host, guest })
            Assert.Equal(new ushort[] { 521, 510 }, session.Opcodes.Where(o => o is 521 or 510));
    }

    private sealed class GatedSignatureSender : IMessageSender<BubbleFighter.Fantasy.Contracts.Match.MatchSessionSignature>
    {
        public string Protocol => "kr-1256/game";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SendAsync(ISession session, BubbleFighter.Fantasy.Contracts.Match.MatchSessionSignature message, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await session.SendAsync(521, ReadOnlyMemory<byte>.Empty, ct);
        }
    }

    [Fact]
    public async Task EverySeatMustLoadAndAuthenticateBeforeTheCountdownCanStart()
    {
        var options = new InGameOptions { StartMatches = true, StartTimerMs = 150 };
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await loop.Start.HandleStageMoveConsumedAsync(host, new(579, new byte[60]), CancellationToken.None);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, host);
        Assert.DoesNotContain((ushort)585, host.Opcodes);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, guest);
        Assert.Contains((ushort)585, host.Opcodes);
        Assert.DoesNotContain((ushort)608, host.Opcodes);
        Assert.False(handle.Room.CountdownTask.IsCompleted);
        await handle.Room.CountdownTask;
        Assert.Single(host.Opcodes, opcode => opcode == 608);
        Assert.Single(guest.Opcodes, opcode => opcode == 608);
        await loop.Start.HandleRelayEndpointAsync(host, new(578, new byte[16]), CancellationToken.None);
        Assert.Single(host.Opcodes, opcode => opcode == 571);
        Assert.DoesNotContain((ushort)584, host.Opcodes);
        Assert.DoesNotContain((ushort)602, host.Opcodes);
    }

    [Fact]
    public async Task FailedOrUninitializedRelayNeverReleasesPendingStart()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, _) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await loop.Start.HandleRelayConnectResultAsync(host, new(571, BitConverter.GetBytes(0)), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Start.HandleRelayConnectResultAsync(host,
            new(571, BitConverter.GetBytes(1)), CancellationToken.None).AsTask());
        Assert.True(handle.Room.StartPending);
        Assert.DoesNotContain((ushort)608, host.Opcodes);
    }

    [Fact]
    public async Task RetiredTransportReportsPreserveTheRoomAndCannotRevokeTheNextRound()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, host);
        handle.Room.MarkResult();
        foreach (var phase in new[] { RoomPhase.Result, RoomPhase.Waiting })
        {
            if (phase == RoomPhase.Waiting) handle.Room.ResetToWaiting();
            var before = host.Opcodes.ToArray();
            foreach (var result in new[] { 0, 1 })
                await loop.Start.HandleRelayConnectResultAsync(host, new(571, BitConverter.GetBytes(result)), CancellationToken.None);
            Assert.Equal(before, host.Opcodes);
            Assert.Equal(phase, handle.Room.Phase);
            Assert.Equal(2, handle.Room.Seats.Count);
        }
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await loop.Start.HandleRelayConnectResultAsync(host, new(571, BitConverter.GetBytes(0)), CancellationToken.None);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, host);
        var seat = handle.Registry.RequireGame(host).Seat;
        var replacement = seat.Relay;
        await loop.Start.HandleRelayConnectResultAsync(host, new(571, BitConverter.GetBytes(0)), CancellationToken.None);
        Assert.Same(replacement, seat.Relay);
        Assert.True(seat.RelayInitialized);
        Assert.True(seat.RelayConfirmed);
        Assert.DoesNotContain((ushort)585, host.Opcodes);
        Assert.DoesNotContain((ushort)585, guest.Opcodes);
        handle.Registry.Detach(host.Id);
        await loop.Start.HandleRelayConnectResultAsync(host, new(571, BitConverter.GetBytes(0)), CancellationToken.None);
    }

    [Fact]
    public async Task LostTransportDuringCountdownClosesOnlyThatMember()
    {
        var options = new InGameOptions { StartMatches = true, StartTimerMs = 100 };
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, host);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, guest);
        handle.Registry.Detach(handle.Room.Seats[1].Relay!.Id);
        await handle.Room.CountdownTask;
        Assert.Contains((ushort)608, host.Opcodes);
        Assert.DoesNotContain((ushort)608, guest.Opcodes);
        Assert.True(guest.Connection.IsClosed);
        Assert.False(host.Connection.IsClosed);
        Assert.Equal(RoomPhase.Playing, handle.Room.Phase);
    }

    [Fact]
    public async Task LeavingDuringCountdownLeavesTheRoundToTheOthers()
    {
        var options = new InGameOptions { StartMatches = true, StartTimerMs = 100 };
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, host);
        await CompleteHandshakeAsync(loop, handle.Registry, handle.Room, guest);
        await loop.Setup.PostDetach(guest.Connection.Id);
        await handle.Room.CountdownTask;
        Assert.Contains((ushort)608, host.Opcodes);
        Assert.False(host.Connection.IsClosed);
        // Nothing is announced during the round.
        Assert.DoesNotContain(InGameOpcodes.S2CSeatRecord, host.Opcodes);
        Assert.Single(handle.Room.Seats);
    }

    private static async Task CompleteHandshakeAsync(InGameFlows loop, RoomRegistry registry, PlayRoom room, RecordingSession game)
    {
        var seat = registry.RequireGame(game).Seat;
        await loop.Start.HandleSceneObjectReadyAsync(game, new(607, BitConverter.GetBytes(1000000 + 100000 * seat.Slot)), CancellationToken.None);
        await loop.Start.HandleRelayEndpointAsync(game, new(578, new byte[16]), CancellationToken.None);
        var relay = new RecordingSession(1000L * room.StartGeneration + game.Id);
        var ticket = WireLengthSupport.Written(seat.Key.Write);
        var binding = await registry.BindAsync(relay, new(ticket, seat.UserNum, seat.Nickname), CancellationToken.None);
        registry.MarkInitialized(relay, binding);
        await loop.Start.HandleRelayConnectResultAsync(game, new(571, BitConverter.GetBytes(1)), CancellationToken.None);
    }

    [Fact]
    public async Task StartMovesEverySeatedPlayerIntoTheMatchPhase()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        var hostPlayer = host.SetLobbyPlayer(1, "host");
        var guestPlayer = guest.SetLobbyPlayer(2, "guest");
        hostPlayer.MoveTo(SessionPhase.Room);
        guestPlayer.MoveTo(SessionPhase.Room);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        handle.Room.FindByGame(guest.Id)!.Ready = true;

        await loop.Start.StartMatchAsync(handle.Room, CancellationToken.None, host.Id);

        Assert.Equal(SessionPhase.Match, hostPlayer.Phase);
        Assert.Equal(SessionPhase.Match, guestPlayer.Phase);
    }

    [Fact]
    public async Task StartClickPublishesTeamsBeforeSceneCreationAndKeepsModeMetadata()
    {
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        host.SetLobbyPlayer(1, "host", pastLoginBurst: false);
        var map = handle.Room.Setting.Map with { Unknown4 = 1, Unknown116 = "old-title" };
        handle.Room.UpdateMap(map);
        var request = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed("rel_Village01_Byway");
            w.WriteWideStringPrefixed("new-title");
            w.WriteWideStringPrefixed("Random_Hot");
            w.WriteInt32(0);
            for (var i = 0; i < 13; i++) w.WriteInt32(0);
            w.WriteByte(0);
            w.WriteInt32(0);
        });
        var signatureSender = new RecordingSignatureSender();
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: signatureSender);

        handle.Room.FindByGame(guest.Id)!.Ready = true;
        await loop.Setup.HandleCreateRoomAsync(host, new InboundPacket(503, request), CancellationToken.None);
        Assert.Equal(new[] { (host.Id, (ushort)1), (guest.Id, (ushort)1) }, signatureSender.Sent);

        Assert.Equal((ushort)1, handle.Room.Setting.Map.Unknown4);
        Assert.Equal(map with
        {
            Unknown44 = "Random_Hot", Unknown68 = "rel_Village01_Byway",
            Unknown92 = "rel_Village01_Byway", Unknown116 = "new-title",
        }, handle.Room.Setting.Map);
        Assert.Equal(new byte[] { 1, 2 }, handle.Room.Seats.Select(s => s.Team));
        foreach (var session in new[] { host, guest })
        {
            Assert.Equal(new ushort[] { 503, 503, 521, 510 }, session.Opcodes);
            Assert.Equal(2, session.Body(503)[17]);
            var stage = new PacketReader(session.Body(510));
            for (var i = 0; i < 3; i++) stage.ReadWideStringPrefixed();
            Assert.Equal(3, stage.ReadInt32());
            Assert.DoesNotContain((ushort)608, session.Opcodes);
        }
    }

    [Fact]
    public async Task PirateStartNamesTheMissionAndItsStage()
    {
        // The pirate map report carries the stage in its key slot and the mission in its scene
        // slot; the room keeps the mission, and S2C 510 names the mission in slots one and three,
        // the MapMeta Directory stage in slot two, and the mode in the fourth field.
        var options = new InGameOptions { StartMatches = true };
        var (handle, host, guest) = TwoSeatRoom(options);
        host.SetLobbyPlayer(1, "host", pastLoginBurst: false);
        handle.Room.UpdateMap(handle.Room.Setting.Map with { Unknown4 = 101 });
        var signatureSender = new RecordingSignatureSender();
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: signatureSender,
            maps: TestMapCatalog.Pirate());

        var selection = WireLengthSupport.Written(w =>
        {
            w.WriteWideStringPrefixed("빌리지 강변 쉼터");
            w.WriteWideStringPrefixed("rel_Village07_Easy02");
            w.WriteWideStringPrefixed("npc_Village07_Easy01");
            for (var i = 0; i < 13; i++) w.WriteInt32(0);
        });
        await loop.Setup.HandleMapListReportAsync(host, new InboundPacket(513, selection), CancellationToken.None);
        Assert.Equal("npc_Village07_Easy01", handle.Room.Setting.Map.Unknown68);

        var start = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(0);
            w.WriteWideStringPrefixed("npc_Village07_Easy01");
            w.WriteWideStringPrefixed("variant-title");
            w.WriteWideStringPrefixed("npc_Village07_Easy01");
            w.WriteInt32(0);
            for (var i = 0; i < 13; i++) w.WriteInt32(0);
            w.WriteByte(0);
            w.WriteInt32(0);
        });
        handle.Room.FindByGame(guest.Id)!.Ready = true;
        await loop.Setup.HandleCreateRoomAsync(host, new InboundPacket(503, start), CancellationToken.None);

        foreach (var session in new[] { host, guest })
        {
            var stage = new PacketReader(session.Body(510));
            Assert.Equal("npc_Village07_Easy01", stage.ReadWideStringPrefixed());
            Assert.Equal("rel_Village07_Easy02", stage.ReadWideStringPrefixed());
            Assert.Equal("npc_Village07_Easy01", stage.ReadWideStringPrefixed());
            Assert.Equal(101, stage.ReadInt32());
        }
        Assert.Equal("rel_Village07_Easy02", handle.Room.RoundStageKey);
    }

    [Fact]
    public void StartMatchesDefaultsToOff()
    {
        Assert.False(new InGameOptions().StartMatches);
        Assert.Empty(new InGameOptions().Validate());
    }

    [Fact]
    public void StartMatchesWithoutTheC2S17GateFailsValidation()
    {
        var options = new InGameOptions { StartMatches = true, StartFlag0 = 0 };
        var failures = options.Validate().ToList();
        var failure = Assert.Single(failures);
        Assert.Contains(nameof(InGameOptions.StartMatches), failure.MemberNames);
        Assert.Contains(nameof(InGameOptions.StartFlag0), failure.MemberNames);
    }




    [Fact]
    public async Task StartMatchRefusesAnEmptyRoom()
    {
        var options = new InGameOptions { StartMatches = true };
        var empty = new PlayRoom(99, 2, RoomWireSupport.WaitingSetting(), queueCapacity: 1);
        var loop = new InGameFlows(options, new RoomRegistry(options), NullLogger.Instance, signatureSender: new RecordingSignatureSender());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loop.Start.StartMatchAsync(empty, CancellationToken.None).AsTask());
    }

    private static (RoomHandle Room, RecordingSession Host, RecordingSession Guest) TwoSeatRoom(InGameOptions options, bool relayOnGame = false)
    {
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        if (relayOnGame)
            foreach (var seat in room.Seats) { seat.Relay = seat.Game; seat.RelayInitialized = true; }
        return (new RoomHandle(registry, room), host, guest);
    }





    /// <summary>
    /// A timed round progresses through its room-owned result task even without
    /// inbound traffic. S2C 615 advances to rewards; only the later 576/620 pair
    /// returns the room to waiting.
    ///
    /// </summary>
    [Fact]
    public async Task IdleRoundOpensAndClosesTheResultWithoutAnyClientTraffic()
    {
        var options = new InGameOptions
        {
            StartMatches = true,
            StartTimerMs = 0,
            RoundUnknown0 = 1_000,
            ResultScoreboardDelayMs = 10,
            ResultRewardDelayMs = 10,
            ResultCloseGraceMs = 50,
        };
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.OrdinarySettingWithMap(), 1, "host");
        var loop = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());

        await loop.Start.StartMatchAsync(room, CancellationToken.None);
        await CompleteHandshakeAsync(loop, registry, room, host);
        await room.CountdownTask;

        // The armed clock only closes a round it sees as Playing, which is the
        // state C2S 17 sets; no other inbound frame is sent for the rest of the test.
        room.MarkPlaying();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !host.Opcodes.Contains(InGameOpcodes.S2CResultClose))
            await Task.Delay(20);

        await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.Opcodes, o => o == InGameOpcodes.S2CResultOpen);
        Assert.Single(host.Opcodes, o => o == InGameOpcodes.S2CResultClose);

        // The result close is a triple in this order: 615 (one u8), then the
        // bodyless 576 (dispatcher case 9D4790 clears the pending relay state),
        // then the bodyless 620 (case 0x26C drops the phase and repeats the
        // stage-4 move the next S2C 117 performs anyway).
        var opcodes = host.Opcodes.ToList();
        var close = opcodes.IndexOf(InGameOpcodes.S2CResultClose);
        Assert.Equal(
            new[]
            {
                InGameOpcodes.S2CResultClose,
                InGameOpcodes.S2CRelayStateClear,
                InGameOpcodes.S2CRoundStageReset,
            },
            opcodes.Skip(close).Take(3).ToArray());
        Assert.Empty(host.Body(InGameOpcodes.S2CRelayStateClear));
        Assert.Empty(host.Body(InGameOpcodes.S2CRoundStageReset));

        Assert.Equal(RoomPhase.Waiting, room.Phase);
    }


    [Fact]
    public async Task HostLeavingMidRoundIsAnnouncedAfterTheRoomReturnsToWaiting()
    {
        var options = new InGameOptions
        {
            StartMatches = true,
            StartTimerMs = 0,
            RoundUnknown0 = 300,
            ResultScoreboardDelayMs = 10,
            ResultRewardDelayMs = 10,
            ResultCloseGraceMs = 10,
        };
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.OrdinarySettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        var loop = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await loop.Start.StartMatchAsync(room, CancellationToken.None);
        await CompleteHandshakeAsync(loop, registry, room, host);
        await CompleteHandshakeAsync(loop, registry, room, guest);
        await room.CountdownTask;
        Assert.Equal(RoomPhase.Playing, room.Phase);

        await loop.Setup.PostDetach(host.Connection.Id);
        Assert.DoesNotContain(InGameOpcodes.S2CSeatRecord, guest.Opcodes);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !guest.Opcodes.Contains(InGameOpcodes.S2CMasterChanged))
            await Task.Delay(20);

        // After S2C 620 the survivor sees the vacated host seat, then the master change to its own seat.
        var opcodes = guest.Opcodes.ToList();
        var reset = opcodes.IndexOf(InGameOpcodes.S2CRoundStageReset);
        Assert.True(reset >= 0, string.Join(",", opcodes));
        Assert.Equal(
            new[] { InGameOpcodes.S2CSeatRecord, InGameOpcodes.S2CMasterChanged, InGameOpcodes.S2CCreateRoomReply },
            opcodes.Skip(reset + 1).ToArray());
        Assert.Equal(Convert.ToHexString(GameToClient.SeatVacated(0)), Convert.ToHexString(guest.Body(InGameOpcodes.S2CSeatRecord)));
        Assert.Equal(Convert.ToHexString(GameToClient.MasterChanged(1)), Convert.ToHexString(guest.Body(InGameOpcodes.S2CMasterChanged)));
        Assert.Equal(RoomPhase.Waiting, room.Phase);
    }

    /// <summary>
    /// C2S 103's thirty counters are the only per-seat record of a finished round,
    /// so they have to survive the packet and clear with the round. Slot 0 is the
    /// token the client echoes from the second field of S2C 608, which is what
    /// ties a record to one specific round rather than to the seat's last round.
    ///
    /// </summary>
    [Fact]
    public async Task ResultStatsAreKeptOnTheSeatUntilTheRoomResets()
    {
        var options = new InGameOptions();
        var (handle, host, _) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        var body = WireLengthSupport.Written(w =>
        {
            for (var i = 1; i <= 30; i++)
                w.WriteInt32(i);
        });

        await loop.Result.HandleResultStatsAsync(
            host, new InboundPacket(InGameOpcodes.C2SGameResultStats, body), CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, 30), handle.Room.Seats[0].RoundStats);
        // No result was open, so the ack stored the data and closed nothing.
        Assert.DoesNotContain(InGameOpcodes.S2CResultClose, host.Opcodes);

        handle.Room.ResetToWaiting();
        Assert.Null(handle.Room.Seats[0].RoundStats);
    }

    [Fact]
    public async Task MovementSamplesReachOnlyTheOtherSeatsAndMergeIntoState()
    {
        // The C2S 32923 body is mask-gated ({u32 actor, u16 tick, u8 mask} + per-bit fields), the server merges
        // it into the actor's per-player movement state, and the merged full S2C 32885 frame goes to every
        // OTHER seat only - the client drops frames carrying its own id before applying anything.
        var options = new InGameOptions();
        var (handle, host, guest) = TwoSeatRoom(options, relayOnGame: true);
        var movement = new MemoryPlayerMovementState(64);
                var relay = new TestRelay(handle.Registry, broadcastMovementSamples: true);
        var sampleBody = new byte[]
        {
            0x40, 0x42, 0x0F, 0x00,             // actor 1000000
            0x39, 0x05,                          // tick 1337
            0x3F,                                // mask: x, y, z, yaw, pitch, state
            0x00, 0x00, 0x20, 0x41,             // x = 10.0f
            0x00, 0x00, 0x40, 0x41,             // y = 12.0f
            0x00, 0x00, 0x60, 0x41,             // z = 14.0f
            0x0A, 0x14, 0x02,                    // yaw, pitch, state
        };

        await relay.Flows.HandleMovementSampleAsync(
            host, new InboundPacket(InGameOpcodes.C2SWanderReport, sampleBody), CancellationToken.None);

        // The guest receives the merged full frame; the host never sees its own.
        Assert.Equal(21, guest.Body(InGameOpcodes.S2CMovementReport).Length);
        Assert.DoesNotContain(InGameOpcodes.S2CMovementReport, host.Opcodes);
        var r = new PacketReader(guest.Body(InGameOpcodes.S2CMovementReport));
        Assert.Equal(1000000, r.ReadInt32());
        Assert.Equal(10.0f, r.ReadSingle());
        Assert.Equal(12.0f, r.ReadSingle());
        Assert.Equal(14.0f, r.ReadSingle());
        Assert.Equal((byte)0x0A, r.ReadByte());
        Assert.Equal((byte)0x14, r.ReadByte());
        Assert.Equal((byte)0x02, r.ReadByte());
        Assert.Equal((ushort)1337, r.ReadUInt16());

        // A second partial sample (y and pitch only) merges with the kept x/z.
        var partial = new byte[]
        {
            0x40, 0x42, 0x0F, 0x00,
            0x3A, 0x05,
            0x12,                                // mask: y and pitch
            0x00, 0x00, 0xC0, 0x41,             // y = 24.0f
            0x05,
        };
        await relay.Flows.HandleMovementSampleAsync(
            host, new InboundPacket(InGameOpcodes.C2SWanderReport, partial), CancellationToken.None);
        // Body(opcode) returns the LAST body for that opcode: the merged frame.
        var merged = new PacketReader(guest.Body(InGameOpcodes.S2CMovementReport));
        Assert.Equal(1000000, merged.ReadInt32());
        Assert.Equal(10.0f, merged.ReadSingle());
        Assert.Equal(24.0f, merged.ReadSingle());
        Assert.Equal(14.0f, merged.ReadSingle());
        // The partial sample's mask 0x12 = y and pitch: x/z keep the first
        // sample's values, yaw keeps 0x0A, pitch takes 0x05, state keeps 0x02.
        Assert.Equal((byte)0x0A, merged.ReadByte());
        Assert.Equal((byte)0x05, merged.ReadByte());
        Assert.Equal((byte)0x02, merged.ReadByte());
        // The current room seat owns the merged record.
        var stored = relay.MovementOf(handle.Room, handle.Room.Seats[0]);
        Assert.Equal(new PlayerMovementState(10.0f, 24.0f, 14.0f, 0x0A, 0x05, 0x02), stored);
    }

    [Fact]
    public async Task MovementFullReportJoinsTheBroadcastAndNeverReachesTheSender()
    {
        // S2C 32885 is an others-only stream. With broadcast_movement_samples on, the 1 Hz C2S 32885 FULL
        // report joins the same pipeline as the C2S 32923 samples: the actor's movement state is overwritten
        // from the report and the full 21-byte frame reaches every other seat, preserving all three float bit
        // patterns.
        var options = new InGameOptions();
        var (handle, host, guest) = TwoSeatRoom(options, relayOnGame: true);
        var movement = new MemoryPlayerMovementState(64);
                var relay = new TestRelay(handle.Registry, broadcastMovementSamples: true);
        const float x = 165.654f;
        const float y = 1748.83f;
        var body = WireLengthSupport.Written(new MovementReport(
            1000000, x, y, 1900.25f, 0x0A, 0x14, 0x02, 1337).Write);

        await relay.Flows.HandleMovementReportAsync(
            host, new InboundPacket(InGameOpcodes.C2SMovementReport, body), CancellationToken.None);

        Assert.Equal(21, guest.Body(InGameOpcodes.S2CMovementReport).Length);
        Assert.DoesNotContain(InGameOpcodes.S2CMovementReport, host.Opcodes);
        var frame = new PacketReader(guest.Body(InGameOpcodes.S2CMovementReport));
        Assert.Equal(1000000, frame.ReadInt32());
        Assert.Equal(x, frame.ReadSingle());
        Assert.Equal(y, frame.ReadSingle());
        Assert.Equal(1900.25f, frame.ReadSingle());
        Assert.Equal((byte)0x0A, frame.ReadByte());
        Assert.Equal((byte)0x14, frame.ReadByte());
        Assert.Equal((byte)0x02, frame.ReadByte());
        // The trailing u16 clock rides unchanged.
        Assert.Equal((ushort)1337, frame.ReadUInt16());

        // The current room seat keeps all fields from the complete report.
        Assert.Equal(
            new PlayerMovementState(x, y, 1900.25f, 0x0A, 0x14, 0x02),
            relay.MovementOf(handle.Room, handle.Room.Seats[0]));

        // The full unsigned sample clock survives its maximum value unchanged.
        var wrapped = WireLengthSupport.Written(new MovementReport(
            1000000, x, y, 1900.25f, 0x0A, 0x14, 0x02, ushort.MaxValue).Write);
        await relay.Flows.HandleMovementReportAsync(host, new(InGameOpcodes.C2SMovementReport, wrapped), CancellationToken.None);
        Assert.Equal(ushort.MaxValue, MovementReport.Parse(guest.Body(InGameOpcodes.S2CMovementReport)).Tick);
    }

    [Fact]
    public async Task MovementReportEchoKeepsPrecedenceOverTheBroadcast()
    {
        // With both flags on the legacy 1:1 self-echo wins and the others-only
        // pipeline stays out: the sender gets its own report back exactly once
        // and no peer receives anything, so no seat is ever served twice.
        var options = new InGameOptions();
        var (handle, host, guest) = TwoSeatRoom(options, relayOnGame: true);
                var relay = new TestRelay(handle.Registry, broadcastMovementSamples: true, echoMovementReports: true);
        var body = WireLengthSupport.Written(new MovementReport(
            1000000, 165.654f, 1748.83f, 1900.25f, 0x0A, 0x14, 0x02, 1337).Write);

        await relay.Flows.HandleMovementReportAsync(
            host, new InboundPacket(InGameOpcodes.C2SMovementReport, body), CancellationToken.None);

        Assert.Equal(new[] { InGameOpcodes.S2CMovementReport }, host.Opcodes);
        // The echo is the byte-identical Write replay of the report.
        Assert.Equal(
            Convert.ToHexString(body),
            Convert.ToHexString(host.Body(InGameOpcodes.S2CMovementReport)));
        Assert.Empty(guest.Opcodes);
    }

    private sealed record RoomHandle(RoomRegistry Registry, PlayRoom Room);

    private sealed class RecordingSession(long id) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        private readonly List<(ushort Opcode, byte[] Body)> _sent = [];

        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();

        public IEnumerable<ushort> Opcodes
        {
            get
            {
                lock (_sent)
                    return _sent.Select(s => s.Opcode).ToArray();
            }
        }

        public byte[] Body(ushort opcode)
        {
            lock (_sent)
                return _sent.Last(s => s.Opcode == opcode).Body;
        }

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnection(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason)
        {
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}
