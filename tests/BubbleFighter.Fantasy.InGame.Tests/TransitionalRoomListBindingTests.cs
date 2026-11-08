using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins native lobby list headers and mounts the shared typed sender with separate-equipment builds.</summary>
public sealed class TransitionalRoomListBindingTests
{
    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public async Task NativeListUsesPageBooleanAndCompleteSessionRows(string build)
    {
        var root = Path.GetDirectoryName(SharedDialectRows.PacketsDirectory())!;
        var tables = PacketTables.Load(Path.Combine(root, "packets"));
        var opcodes = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var game = opcodes.ForConnection(OpcodeConnection.Game);
        Assert.Equal((ushort)101, game.S2C("gspRepListSession"));
        Assert.Null(opcodes.ForConnection(OpcodeConnection.Relay).Find(PacketDirection.ServerToClient, "gspRepListSession"));
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
        var router = Context("router");
        var messages = Context("messages");
        var binding = Context("binding");
        try
        {
            await new PacketRouterPlugin().ActivateAsync(router, default);
            await new MessageRouterPlugin().ActivateAsync(messages, default);
            new RoomListCodec(build + "/game", game.C2S("gcpReqListSession"), game.S2C("gspRepListSession"), tables, build, NullLogger.Instance).Mount(binding);
            var sender = Assert.Single(services.GetAll<IMessageSender<RoomListSnapshot>>());
            var schema = RoomTables.Load(tables).Of(build).Schema;
            foreach (var count in new[] { 0, 2 })
            {
                RoomListEntry[] entries = [
                    new(7, "waiting", "host", 1, 2, 8, true),
                    new(13, "playing", "guest", 3, 3, 6, false) { MatchInProgress = true },
                ];
                var snapshot = new RoomListSnapshot(5, entries.Take(count).ToArray());
                var expected = new ArrayBufferWriter<byte>();
                var writer = new PacketWriter(expected);
                writer.WriteInt32(5);
                writer.WriteBool32(false);
                writer.WriteInt32(count);
                foreach (var room in snapshot.Rooms)
                    RoomCodec.WriteSessionSetting(writer, schema, new RoomValues(2, [])
                    {
                        Play = true, RoomId = room.RoomId, Title = room.Name, MasterNickname = room.MasterNickname,
                        SessionState = room.MatchInProgress ? 4 : 2, Mode = room.ModeId,
                        Occupants = room.Occupants, OpenSeats = room.Capacity, HasPassword = room.HasPassword,
                    });
                var session = new RecordingSession(7);
                await sender.SendAsync(session, snapshot, default);
                var actual = session.Body(game.S2C("gspRepListSession"));
                Assert.Equal(expected.WrittenSpan.ToArray(), actual);
                var decoded = PacketCodec.Read(tables, build, "RoomListReply", actual, requireCanonicalBooleans: true);
                Assert.Equal(5, decoded["page"]);
                Assert.Equal(false, decoded["more"]);
                Assert.Equal(count, Assert.IsType<object?[]>(decoded["rooms"]).Length);
                Assert.Equal(actual, PacketCodec.Write(tables, build, "RoomListReply", decoded));
                for (var length = 0; length < actual.Length; length++)
                    Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "RoomListReply", actual.AsSpan(0, length)));
                Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "RoomListReply", [.. actual, 0]));
            }
        }
        finally
        {
            await binding.UnwindAsync();
            await messages.UnwindAsync();
            await router.UnwindAsync();
        }
        Assert.Empty(services.GetAll<IMessageSender<RoomListSnapshot>>());
    }
}
