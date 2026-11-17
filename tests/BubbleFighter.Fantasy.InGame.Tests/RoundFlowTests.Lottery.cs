using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The native pirate clear request commits each seat once, publishes its award before selection and returns once.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NativePirateLotteryRunsThroughSelectionGrantRevealAndRoomReturn(bool cleared, bool scoreboardSupported)
    {
        const string build = "kr-1256";
        const string protocol = "test/game";
        const int displayId = 1000920;
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get(build).Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName,
            "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "pirate-lottery", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var players = new MemoryPlayerTable(2);
        var inventory = new MemoryInventoryRepository(players);
        var wallets = new MemoryPlayerWalletRepository(players);
        var purchases = new MemoryShopPurchaseStore(players);
        var push = new LotteryInventoryPush();
        var workbook = new LotteryWorkbook();
        var displayed = new ConcurrentQueue<(long Player, int Seat, int Display)>();
        var boardCode = table.S2C("Unknown614");
        var selectedCode = table.S2C("gspRepAwardSelectSuccess");
        var openCode = table.S2C("gspGameAwardDialog");
        var revealCode = table.S2C("gspHordeModeRewardList");
        var returnCode = table.S2C("gspGameReturnAfterAward_");
        var endAllReplyCode = table.S2C("gspRepEndAllWave");
        Rig? rig = null;
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            new RoomCommandsBinding(protocol, table, packets, build, NullLogger.Instance, roundsBound: true).Mount(context);
            new RoundResultBinding(protocol, table, packets, build, scoreboardSupported ? [101] : [1], NullLogger.Instance).Mount(context);
            new LotteryBinding(protocol, table, packets, build).Mount(context, services.Get<IMessageRouter>());
            new NpcBinding(protocol, table, packets, build, NullLogger.Instance).Mount(context);
            context.Effect(services.Register<IPlayerWalletRepository>(wallets));
            context.Effect(services.Register<IShopPurchaseStore>(purchases));
            context.Effect(services.Register<IInventoryRepository>(inventory));
            context.Effect(services.Register<IInventoryPushService>(push));
            context.Effect(services.Register<IClientDataStore>(workbook));
            foreach (var user in new long[] { 11, 22 })
                await wallets.UpsertAsync(new PlayerWallet(user, 0, 0), default);
            Rig CreateRig() => new(resultContext: context,
                roundReturnSender: Assert.Single(services.GetAll<IMessageSender<RoomEntered>>()), configure: options =>
                {
                    options.LotteryModes = [101];
                    options.LotteryWorkbookClientPath = "Config/ShopItem2.xml";
                    options.LotteryMaximumFileBytes = 4096;
                    options.LotteryItems = [new() { ItemId = 100207, DisplayId = displayId, Quantity = 5, Stackable = true }];
                    options.LotterySelectTimeMs = 1000;
                    options.LotteryRevealTimeMs = 1000;
                    options.LotteryOpenDelayMs = 1;
                    options.ResultScoreboardDelayMs = 0;
                    options.ResultRewardDelayMs = 0;
                    options.ResultCloseGraceMs = 1;
                });
            if (!scoreboardSupported)
            {
                var error = Assert.Throws<ConfigException>(CreateRig);
                Assert.Contains("scoreboard", error.Message, StringComparison.Ordinal);
                Assert.Equal(0, workbook.Reads);
                Assert.Empty(push.Counts);
                return;
            }
            rig = CreateRig();
            var flow = rig.Round;
            var npc = Assert.Single(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
            context.Effect(services.Get<IMessageRouter>().For(context).On<RoundNpcReported>(protocol,
                (session, report, ct) => flow.HandleNpcAsync(session, report, npc, ct)));
            context.Effect(services.Get<IMessageRouter>().For(context).On<RoundLotteryRequested>(protocol,
                (session, request, ct) => flow.HandleLotteryRequestedAsync(session, request, ct)));
            foreach (var session in new[] { rig.Host, rig.Guest })
                session.OnSend = (opcode, payload) =>
                {
                    if (opcode == selectedCode)
                    {
                        var seat = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
                        Assert.InRange(seat, 0, 1);
                        // The shared rig's names are "host" and "guest"; their masked rows occupy 250 and 252 bytes.
                        var row = PacketCodec.Read(packets, build, "ScoreRow",
                            session.Body(boardCode).AsSpan(seat == 0 ? 38 : 288, seat == 0 ? 250 : 252));
                        displayed.Enqueue((session.Id, seat, (int)row["award_display_id"]!));
                    }
                    return ValueTask.CompletedTask;
                };
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("pirate", "", 101, "map"), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            Assert.Equal(101, room.ModeId);
            if (cleared)
            {
                Assert.Equal((ushort)905, table.C2S("gcpReqEndAllWave"));
                Assert.Equal((ushort)906, endAllReplyCode);
                await EndAllWaves(rig.Guest, []);
                await Assert.ThrowsAsync<PacketFormatException>(() => EndAllWaves(rig.Host, [0]).AsTask());
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Empty(rig.Host.Opcodes);
                Assert.Empty(rig.Guest.Opcodes);
                await EndAllWaves(rig.Host, []);
            }
            else
                await rig.Round.HandleEndRequestedAsync(rig.Host,
                    new RoundEndRequested { Reason = RoundEndReason.CooperativeFailed }, default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (cleared)
            {
                Assert.Equal(RoomPhase.Result, room.Phase);
                Assert.Empty(Assert.Single(rig.Host.Bodies(endAllReplyCode)));
                Assert.Empty(rig.Guest.Bodies(endAllReplyCode));
                Assert.Equal(new[] { endAllReplyCode, table.S2C("Unknown612"), boardCode, table.S2C("Unknown615"), openCode },
                    rig.Host.Opcodes);
                Assert.Equal(new[] { table.S2C("Unknown612"), boardCode, table.S2C("Unknown615"), openCode },
                    rig.Guest.Opcodes);
                var completion = room.CountdownTask;
                await Select(rig.Host, 0, 0);
                Assert.Single(await purchases.ListRecentAsync(11, 10, default));
                Assert.Equal(5, Assert.Single(await inventory.ListSlotsAsync(11, InventoryBag.CharacterBag, 10, default)).Quantity);
                Assert.Equal(new[] { boardCode, selectedCode }, rig.Host.Opcodes.TakeLast(2));
                await Select(rig.Host, 1, 0);
                await Select(rig.Guest, 0, 1);
                Assert.Single(await purchases.ListRecentAsync(11, 10, default));
                Assert.Empty(await purchases.ListRecentAsync(22, 10, default));
                Assert.Equal(table.S2C("gspRepAwardSelectFail"), rig.Host.Opcodes.Last());
                Assert.Equal(table.S2C("gspRepAwardSelectFail"), rig.Guest.Opcodes.Last());
                await completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(4, displayed.Count);
                Assert.All(displayed, item => Assert.Equal(displayId, item.Display));
                foreach (var session in new[] { rig.Host, rig.Guest })
                {
                    Assert.Equal(new[] { 0, 1 }, displayed.Where(item => item.Player == session.Id).Select(item => item.Seat));
                    Assert.Single(await purchases.ListRecentAsync(session.Id, 10, default));
                    Assert.Equal(5, Assert.Single(await inventory.ListSlotsAsync(session.Id,
                        InventoryBag.CharacterBag, 10, default)).Quantity);
                    Assert.Equal(1, push.Counts[session.Id]);
                    Assert.Single(session.Bodies(openCode));
                    Assert.Equal(2, session.Bodies(selectedCode).Length);
                    var reveal = Assert.Single(session.Bodies(revealCode));
                    Assert.Equal(44, reveal.Length);
                    Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(reveal));
                    Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(reveal.AsSpan(4)));
                    Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(reveal.AsSpan(8)));
                    Assert.Equal(displayId, BinaryPrimitives.ReadInt32LittleEndian(reveal.AsSpan(12)));
                }
            }
            else
            {
                Assert.Empty(displayed);
                Assert.Empty(push.Counts);
                foreach (var session in new[] { rig.Host, rig.Guest })
                {
                    Assert.Empty(session.Bodies(openCode));
                    Assert.Empty(session.Bodies(revealCode));
                    Assert.Empty(session.Bodies(selectedCode));
                    Assert.Empty(await purchases.ListRecentAsync(session.Id, 10, default));
                    Assert.Empty(await inventory.ListSlotsAsync(session.Id, InventoryBag.CharacterBag, 10, default));
                }
            }
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            foreach (var session in new[] { rig.Host, rig.Guest })
            {
                Assert.Single(session.Bodies(returnCode));
                Assert.Empty(session.Body(returnCode));
                Assert.Equal(new[] { table.S2C("gspUdpStop"), returnCode }, session.Opcodes.TakeLast(2));
                Assert.Single(rig.Entered.To(session), entry => entry.Reason == RoomEntryReason.RoundReturned);
            }
        }
        finally
        {
            if (rig is not null) await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }

        ValueTask EndAllWaves(ISession session, byte[] body) =>
            services.Get<IPacketRouter>().DispatchAsync(protocol, session,
                new(table.C2S("gcpReqEndAllWave"), body), default);

        ValueTask Select(ISession session, int card, int seat)
        {
            var body = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(body, card);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), seat);
            return services.Get<IPacketRouter>().DispatchAsync(protocol, session,
                new(table.C2S("gcpReqSelectAward"), body), default);
        }
    }

    private sealed class NativeRoundReturnSink(Sink<RoomEntered> recorded, IMessageSender<RoomEntered>? native)
        : IMessageSender<RoomEntered>
    {
        public string Protocol => recorded.Protocol;
        public async ValueTask SendAsync(ISession session, RoomEntered message, CancellationToken ct)
        {
            if (message.Reason == RoomEntryReason.RoundReturned && native is not null)
                await native.SendAsync(session, message, ct);
            await recorded.SendAsync(session, message, ct);
        }
    }

    private sealed class LotteryInventoryPush : IInventoryPushService
    {
        internal ConcurrentDictionary<long, int> Counts { get; } = new();
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("Lottery grants publish their committed rows.");
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) =>
            throw new InvalidOperationException("Lottery grants publish their committed rows together.");
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct)
        {
            Assert.Single(grants);
            Counts.AddOrUpdate(userNum, 1, (_, count) => count + 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LotteryWorkbook : IClientDataStore
    {
        internal int Reads { get; private set; }
        private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("""
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
              <Worksheet ss:Name="Stock"><Table>
                <Row><Cell><Data>stockId</Data></Cell><Cell><Data>itemId</Data></Cell><Cell><Data>duration</Data></Cell><Cell><Data>type</Data></Cell></Row>
                <Row><Cell><Data>1000920</Data></Cell><Cell><Data>100207</Data></Cell><Cell><Data>5</Data></Cell><Cell><Data>1</Data></Cell></Row>
              </Table></Worksheet>
            </Workbook>
            """);
        public bool Exists(string path) => true;
        public byte[] Read(string path)
        {
            Reads++;
            return Bytes;
        }
    }
}
