using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises room-entry packets before ready toggles and the first scene move.</summary>
public sealed class RoomEntryRegressionTests
{
    // The seat character comes from the roster service when it has one, else the Dao item.
    [Theory]
    [InlineData(403, 300009, 300009)]
    [InlineData(403, 0, 300001)]
    [InlineData(517, 300009, 300009)]
    [InlineData(517, 0, 300001)]
    public async Task JoiningPreservesTheLocalCharacterThroughRefreshReadyAndStart(
        int opcode, int resolved, int expected)
    {
        var (registry, room, host, guest, options) = Create();
        var view = new EquippedView();
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1",
            NullLogger.Instance, new CharacterResolver(resolved), equippedView: view);

        await JoinAsync(loop, guest, room.Id, opcode);
        var seat = room.FindByGame(guest.Id)!;
        Assert.Equal(expected, seat.CharacterId);
        Assert.True(guest.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out var guestPlayer));
        Assert.Equal(BubbleFighter.Fantasy.Contracts.Sessions.SessionPhase.Room, guestPlayer.Phase);
        AssertPartyCharacter(guest, expected);
        Assert.Contains((guest.Id, 22, expected), view.Sent);
        Assert.Equal(new byte[] { 1, 2 }, room.Seats.Select(s => s.Team));

        await JoinAsync(loop, guest, room.Id, 517);
        AssertPartyCharacter(guest, expected);
        var combat = new InGameFlows(options, registry, NullLogger.Instance,
            signatureSender: new RecordingSignatureSender());
        foreach (var ready in new[] { 1, 0, 1 })
        {
            await combat.Setup.HandleRoomStatusAsync(guest, new InboundPacket(InGameOpcodes.C2SUnknown505,
                WireLengthSupport.Written(w => { w.WriteInt32(ready); w.WriteInt32(0); w.WriteInt32(0); })),
                CancellationToken.None);
            var update = guest.Body(InGameOpcodes.S2CCreateRoomReply);
            Assert.Equal((byte)2, update[17]); // 0x1143710: slot header, three dwords, slot byte, team byte.
            Assert.Equal(ready, BinaryPrimitives.ReadInt32LittleEndian(update.AsSpan(40)));
            Assert.Equal(expected, seat.CharacterId);
        }

        await combat.Start.StartMatchAsync(room, CancellationToken.None, host.Id);
        Assert.Contains(InGameOpcodes.S2CEnterGameStage, guest.Opcodes);
        Assert.Contains(InGameOpcodes.S2CEnterGameStage, host.Opcodes);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        AssertPartyCharacter(guest, expected);
        Assert.Equal(expected, seat.CharacterId);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(517)]
    public async Task UnresolvedJoinIsNotVisibleToRoomPublishers(int opcode)
    {
        var (registry, room, _, guest, options) = Create();
        var pending = new PendingCharacterResolver();
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1",
            NullLogger.Instance, pending);
        var join = JoinAsync(loop, guest, room.Id, opcode).AsTask();
        try
        {
            Assert.True(pending.Requested);
            Assert.False(join.IsCompleted);
            Assert.Null(registry.TryGet(guest.Id));
            Assert.Single(room.Seats);
            await loop.PushRoomAsync(room, CancellationToken.None);
            Assert.Empty(guest.Opcodes);
        }
        finally
        {
            pending.Result.TrySetResult(300017);
            await join;
        }
        Assert.Equal(300017, room.FindByGame(guest.Id)!.CharacterId);
        AssertPartyCharacter(guest, 300017);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(517)]
    public async Task FailedCharacterResolutionLeavesNoJoinedSeat(int opcode)
    {
        var (registry, room, _, guest, options) = Create();
        var pending = new PendingCharacterResolver();
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1",
            NullLogger.Instance, pending);
        var join = JoinAsync(loop, guest, room.Id, opcode).AsTask();
        pending.Result.TrySetException(new InvalidOperationException("Character store unavailable."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => join);
        Assert.Null(registry.TryGet(guest.Id));
        Assert.Single(room.Seats);
        Assert.Empty(guest.Opcodes);
    }

    [Fact]
    public async Task CreateAndRepeatedCreateKeepTheAssignedTeamInEveryReply()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = Player(101, 11, "host");
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var setting = RoomWireSupport.OrdinarySettingWithMap();
        setting = setting with { Map = setting.Map with { Unknown4 = 1 } };
        var request = new InboundPacket(InGameOpcodes.C2SUnknown402,
            WireLengthSupport.Written(w => { setting.Write(w); w.WriteInt32(0); }));
        for (var i = 0; i < 2; i++)
        {
            await loop.HandleUnknown402Async(host, request, CancellationToken.None);
            var (_, seat) = registry.RequireGame(host);
            Assert.Equal((byte)1, seat.Team);
            Assert.False(seat.Ready);
            Assert.Equal((byte)1, host.Body(InGameOpcodes.S2CCreateRoomReply)[17]);
        }
    }

    [Fact]
    public void JoiningBalancesOnlyUnassignedSeatsAndKeepsExplicitChoices()
    {
        var (registry, room, host, guest, _) = Create();
        var hostSeat = room.FindByGame(host.Id)!;
        Assert.Equal((byte)1, hostSeat.Team);
        hostSeat.Team = 2;
        var (_, joined) = registry.Join(guest, room.Id, 22, "guest");
        Assert.Equal((byte)1, joined.Team);
        Assert.Equal((byte)2, hostSeat.Team);
        joined.Team = 36;
        registry.Join(Player(103, 33, "third"), room.Id, 33, "third");
        Assert.Equal((byte)36, joined.Team);
        Assert.Equal((byte)2, hostSeat.Team);
    }

    private static void AssertPartyCharacter(RoomPushTests.RecordingSession session, int characterId)
    {
        var reader = new PacketReader(session.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, reader.ReadInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(22, reader.ReadInt32());
        Assert.Equal("guest", reader.ReadWideStringPrefixed());
        reader.ReadBytes(16);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(22, reader.ReadInt32());
        Assert.Equal(characterId, reader.ReadInt32());
    }

    private static ValueTask JoinAsync(RoomLoop loop, RoomPushTests.RecordingSession guest, int roomId, int opcode)
    {
        var request = new InboundPacket((ushort)opcode, WireLengthSupport.Written(w =>
        {
            w.WriteInt32(roomId);
            if (opcode == 403) w.WriteWideStringPrefixed(string.Empty);
        }));
        return opcode == 403 ? loop.HandleUnknown403Async(guest, request, CancellationToken.None)
            : loop.HandleJoinRoomAsync(guest, request, CancellationToken.None);
    }

    private static (RoomRegistry, PlayRoom, RoomPushTests.RecordingSession, RoomPushTests.RecordingSession, InGameOptions)
        Create()
    {
        var options = new InGameOptions { StartMatches = true };
        var registry = new RoomRegistry(options);
        var host = Player(101, 11, "host");
        var guest = Player(102, 22, "guest");
        var setting = RoomWireSupport.OrdinarySettingWithMap();
        var (room, seat) = registry.CreateFromSetting(host,
            setting with { Map = setting.Map with { Unknown4 = 1 } }, 11, "host");
        seat.CharacterId = 300001;
        return (registry, room, host, guest, options);
    }

    private static RoomPushTests.RecordingSession Player(long id, int userNum, string name)
    {
        var session = new RoomPushTests.RecordingSession(id);
        session.SetLobbyPlayer(userNum, name);
        return session;
    }

    private sealed class CharacterResolver(int character) : ISeatCharacterResolver
    {
        public ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct) => ValueTask.FromResult(character);
    }

    private sealed class PendingCharacterResolver : ISeatCharacterResolver
    {
        public TaskCompletionSource<int> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Requested { get; private set; }
        public ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct)
        {
            Requested = true;
            return new ValueTask<int>(Result.Task.WaitAsync(ct));
        }
    }

    private sealed class EquippedView : IEquippedViewPush
    {
        public List<(long Session, int User, int Character)> Sent { get; } = [];
        public List<(long Session, int User, int Character)> Remote { get; } = [];
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds,
            CancellationToken ct)
        {
            Assert.Contains(characterId, equippedIds);
            Sent.Add((session.Id, userNum, characterId));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId,
            IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            Remote.Add((recipient.Id, userNum, characterId));
            return ValueTask.CompletedTask;
        }
    }
}
