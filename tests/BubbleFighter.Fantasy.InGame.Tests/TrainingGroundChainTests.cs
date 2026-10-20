using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// End-to-end pins for the single-player training ground: the C2S 108 create,
/// the room install it answers with, and the deferred start chain a position
/// report releases. Each frame is pinned by opcode, so any drift in the order,
/// in the seat-info mask or in the room state shows up here first.
///
/// </summary>
/// <remarks>
/// The chain the client code proves, in order: S2C 117 (kind 2, room state, mode 40,
/// the training map in all four selection slots, one seat), one S2C 511 per
/// seat, S2C 118, the S2C 503 create reply, S2C 510 (the stage move, carrying
/// the room state), then on the first position report 585, 584, the four
/// 602-605 seat-info records with a mask covering the room's logical seat
/// count, 571, 607 and 608. A second position report must not repeat the chain.
/// </remarks>
public sealed class TrainingGroundChainTests
{
    [Fact]
    public async Task TrainingCreationWaitsForAuthenticationInsteadOfStartingOnPositionReports()
    {
        var options = new InGameOptions
        {
            StartMatches = true,
            StartTrainingImmediately = true,
            RepublishRoomUiOnJoin = true,
            RepublishRoomUiOnPosition = true,
            RelayCharacterPositions = true,
        };
        var registry = new RoomRegistry(options);
        var rooms = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var loop = new InGameFlows(options, registry, NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        var session = new RoomPushTests.RecordingSession(90);
        session.SetLobbyPlayer(7, "trainee");

        await loop.Setup.HandleTrainingRoomAsync(session, TrainingCreate(), CancellationToken.None);

        Assert.Equal(
            new ushort[]
            {
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
                InGameOpcodes.S2CCreateRoomReply,
                InGameOpcodes.S2CWaitUserList,
                InGameOpcodes.S2CCreateRoomReply,
                521,
                InGameOpcodes.S2CEnterGameStage,
            },
            session.Opcodes);
        // The install carries the training map in all four selection slots and
        // the room state, which is what the kind-2 branch needs before it
        // compares the mode against 40.
        var install = new PacketReader(session.Body(InGameOpcodes.S2CGotoRoom));
        var setting = SessionSettingBlob.Read(ref install);
        Assert.Equal(SessionSettingBlob.MainMenuKind, setting.Kind);
        // The install carries the waiting state the start click requires (2); the training stage enter below
        // still carries the in-round 4.
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, setting.Unknown4);
        Assert.Equal((ushort)RoomRegistry.TrainingModeId, setting.Map.Unknown4);
        Assert.Equal(RoomRegistry.TrainingMapName, setting.Map.Unknown44);
        Assert.Equal(RoomRegistry.TrainingMapName, setting.Map.Unknown68);
        Assert.Equal(RoomRegistry.TrainingMapName, setting.Map.Unknown92);
        Assert.Equal(RoomRegistry.TrainingMapName, setting.Map.Unknown116);

        // The stage enter repeats the map keys and the room state: the
        // dispatcher copies its order-4 i32 into session+12, which five UI
        // gates test as equal to four.
        var stage = new PacketReader(session.Body(InGameOpcodes.S2CEnterGameStage));
        Assert.Equal(RoomRegistry.TrainingMapName, stage.ReadWideStringPrefixed());
        Assert.Equal(RoomRegistry.TrainingMapName, stage.ReadWideStringPrefixed());
        Assert.Equal(RoomRegistry.TrainingMapName, stage.ReadWideStringPrefixed());
        Assert.Equal(3, stage.ReadInt32());

        // The first position report releases the rest of the chain: the router
        // entry for C2S 511 is RoomLoop's, which relays and then flushes. The
        // same handler also re-publishes the room UI afterwards, so the frames
        // after the create are the chain plus the two room-UI bodies.
        await rooms.HandleCharacterPositionAsync(session, Position(1), CancellationToken.None);

        Assert.DoesNotContain(InGameOpcodes.S2CStartFlags, session.Opcodes);
        Assert.DoesNotContain(InGameOpcodes.S2CConnectRelay, session.Opcodes);
        Assert.DoesNotContain(InGameOpcodes.S2CRoundInitialize, session.Opcodes);
        Assert.True(registry.RequireGame(session).Room.StartPending);
    }

    private static InboundPacket TrainingCreate()
    {
        var body = new byte[53];
        for (var i = 0; i < 13; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4), i == 6 ? 20 : 0);
        return new InboundPacket(InGameOpcodes.C2STrainingRoom, body);
    }

    private static InboundPacket Position(int counter)
    {
        var body = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0), counter);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), 3);
        body[16] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(18), 4);
        return new InboundPacket(InGameOpcodes.C2SCharacterPositionRoom, body);
    }
}
