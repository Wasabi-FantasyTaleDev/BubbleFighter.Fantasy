using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Scenario.Tests.Harness;
using BubbleFighter.Fantasy.Tests;
using System.Collections.Concurrent;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>
/// Full TCP loopback scenarios verifying <c>user_host</c> login defaults, room creation, prize configuration,
/// launch countdown escrow, crash/disconnect offline refund flushing on next login, and negative build gating.
/// </summary>
public sealed class UserHostScenarioTests
{
    private const int HostTicketItemId = 101_164;

    private const ushort Kr1256S2CUserHostStart = 990;
    private const ushort Kr1256S2CUserHostSetting = 991;
    private const ushort Kr1256S2CUserHostDefaultSetting = 992;
    private const ushort Kr1256S2CSceneInstall = 117;
    private const ushort Kr1256S2CUserStat = 105;
    private const ushort Kr1256S2CEnterGame = 510;

    private const ushort Kr1256C2SRoomCreate = 402;
    private const ushort Kr1256C2SUserHostStart = 2031;
    private const ushort Kr1256C2SUserHostSetting = 2032;

    internal sealed class ProbeOptions
    {
        public string Token { get; set; } = "";
    }

    internal sealed record CapturedServices(
        IPlayerRepository Players,
        IPlayerWalletRepository Wallets,
        IInventoryRepository Inventory,
        IUserHostPrizeStore Prizes);

    [Plugin("test.user-host-probe", Requires = [typeof(IPlayerRepository), typeof(IPlayerWalletRepository), typeof(IInventoryRepository), typeof(IUserHostPrizeStore)])]
    public sealed class ProbePlugin : IPlugin
    {
        internal static readonly ConcurrentDictionary<string, CapturedServices> ByToken = new(StringComparer.Ordinal);

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var token = context.Config.Bind<ProbeOptions>().Token;
            ByToken[token] = new(
                context.Services.Get<IPlayerRepository>(),
                context.Services.Get<IPlayerWalletRepository>(),
                context.Services.Get<IInventoryRepository>(),
                context.Services.Get<IUserHostPrizeStore>());
            context.Effect(new RemoveToken(token));
            return ValueTask.CompletedTask;
        }

        private sealed class RemoveToken(string token) : IDisposable
        {
            public void Dispose() => ByToken.TryRemove(token, out _);
        }
    }

    private static List<PluginRow> Kr1256UserHostRows(int gamePort, int relayPort, string? probeToken = null, int countdownMs = 60, bool enabled = true, bool? dialectEnabled = null)
    {
        var rows = ScenarioServer.Rows(gamePort, relayPort);
        ReplaceConfig(rows, "dialect.rooms", config => config["user_host"] = dialectEnabled ?? enabled);
        ReplaceConfig(rows, "lobby", config =>
        {
            config["user_host_enabled"] = enabled;
            config["user_host_min_prize"] = 1_000;
            config["user_host_max_prize"] = 50_000;
        });
        ReplaceConfig(rows, "ingame", config =>
        {
            config["user_host_enabled"] = enabled;
            config["user_host_min_prize"] = 1_000;
            config["user_host_max_prize"] = 50_000;
            config["user_host_fee_percent"] = 10;
            config["user_host_countdown_ms"] = countdownMs;
            config["cancel_round_on_disconnect"] = true;
            config["user_host_allowed_modes"] = new[] { 3 };
        });
        if (probeToken is not null)
        {
            rows.Add(new PluginRow
            {
                Id = "test.user-host-probe",
                Plugin = "test.user-host-probe",
                Source = "test",
                Config = new Dictionary<string, object?> { ["token"] = probeToken },
            });
        }
        return rows;
    }

    private static void ReplaceConfig(List<PluginRow> rows, string id, Action<Dictionary<string, object?>> update)
    {
        var idx = rows.FindIndex(r => r.Id == id);
        var original = rows[idx];
        var next = new Dictionary<string, object?>(original.Config ?? new Dictionary<string, object?>());
        update(next);
        rows[idx] = new PluginRow
        {
            Id = original.Id,
            Plugin = original.Plugin,
            Source = original.Source,
            ClientBuild = original.ClientBuild,
            Family = original.Family,
            Lineage = original.Lineage,
            ParentId = original.ParentId,
            IsolatedServices = original.IsolatedServices,
            Config = next,
        };
    }

    [Fact]
    public async Task Kr1256PrizeRoomEndToEndWithOfflineHostRefundFlushOverTcp()
    {
        var probeToken = Guid.NewGuid().ToString("N");
        await using var server = await ScenarioServer.StartAsync((g, r) => Kr1256UserHostRows(g, r, probeToken: probeToken, countdownMs: 60));
        var captured = ProbePlugin.ByToken[probeToken];
        var players = captured.Players;
        var wallets = captured.Wallets;
        var inventory = captured.Inventory;
        var prizes = captured.Prizes;

        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

        // Seed host with Lucci + ticket, plus two competitors before login.
        var hostRecord = await players.CreateAsync("uhhost", "uhhost", server.Ct);
        await wallets.GetOrCreateAsync(new(hostRecord.UserNum, 20_000, 0), server.Ct);
        await inventory.ReplaceSlotsAsync(hostRecord.UserNum, InventoryBag.CharacterBag,
            [new InventorySlotRecord(hostRecord.UserNum, InventoryBag.CharacterBag, 1L, HostTicketItemId, 2, 0)], server.Ct);

        var host = await server.ConnectAsync("uhhost");
        await host.Login();

        // 1) Login emits gspUserHostDefaultSetting (min=1000, max=50000).
        var defaultsFrame = host.Since(0).Single(f => f.Opcode == Kr1256S2CUserHostDefaultSetting);
        var defaultFields = PacketCodec.Read(tables, "kr-1256", "UserHostDefaultSetting", defaultsFrame.Payload);
        Assert.Equal(1_000, (int)defaultFields["min_prize"]!);
        Assert.Equal(50_000, (int)defaultFields["max_prize"]!);

        var r1 = await server.ConnectAsync("uhred1");
        var b1 = await server.ConnectAsync("uhblue1");
        await r1.Login();
        await b1.Login();

        // 2) Host creates prize room by patching is_user_host=true into C2S 402 GameSetting block.
        var createPayload = BuildPrizeCreatePayload(tables, "kr-1256", "PrizeCup", mode: 3);
        var hostCursor = host.Cursor;
        await host.Send(Kr1256C2SRoomCreate, createPayload);
        var install = await host.WaitFor(Kr1256S2CSceneInstall, hostCursor);
        var r = new PacketReader(install.Payload);
        var sessionMeta = SimRoomList.ReadSessionSetting(ref r);
        var roomId = sessionMeta.Id;
        Assert.True(roomId > 0);

        // Wait until the created room is published in the room list, then guests join and ready;
        // verify balanced start check (-3 before ready).
        var listed = false;
        for (var attempt = 0; attempt < 20 && !listed; attempt++)
        {
            if ((await r1.ListRooms()).Any(row => row.Id == roomId))
                listed = true;
            else
                await Task.Delay(25, server.Ct);
        }
        Assert.True(listed, $"Room {roomId} never became listed for joiners.");
        await JoinWithDiag(r1, roomId);
        await JoinWithDiag(b1, roomId);

        hostCursor = host.Cursor;
        await host.Send(Kr1256C2SUserHostStart, []);
        var premature = await host.WaitFor(Kr1256S2CUserHostStart, hostCursor);
        Assert.Equal(UserHostStartStatus.InvalidTeamsOrReady, BinaryPrimitives.ReadInt32LittleEndian(premature.Payload));

        // Host updates prize to 6,000 -> guests and host receive gspUserHostSetting(action=1, prize=6000).
        var guestCursor = r1.Cursor;
        var setPrizeBody = PacketCodec.Write(tables, "kr-1256", "UserHostSettingRequest", new Dictionary<string, object?>
        {
            ["action"] = (int)UserHostSettingAction.ConfirmPrize,
            ["prize"] = 6_000,
        });
        await host.Send(Kr1256C2SUserHostSetting, setPrizeBody);
        var guestSetting = await r1.WaitFor(Kr1256S2CUserHostSetting, guestCursor);
        var settingFields = PacketCodec.Read(tables, "kr-1256", "UserHostSettingUpdate", guestSetting.Payload);
        Assert.Equal((int)UserHostSettingAction.ConfirmPrize, (int)settingFields["action"]!);
        Assert.Equal(6_000, (int)settingFields["prize"]!);

        // Ready competitors, then host sends gcpUserHostStart -> S2C gspUserHostStart(0) -> timer commits escrow & enters match.
        await r1.SetReady(true);
        await b1.SetReady(true);
        await Task.Delay(120, server.Ct);

        hostCursor = host.Cursor;
        await host.Send(Kr1256C2SUserHostStart, []);
        var startedReply = await host.WaitFor(Kr1256S2CUserHostStart, hostCursor);
        Assert.Equal(UserHostStartStatus.Started, BinaryPrimitives.ReadInt32LittleEndian(startedReply.Payload));
        await host.WaitFor(Kr1256S2CEnterGame, hostCursor);

        // Verify ticket consumed (2 -> 1) and prize deducted (20,000 -> 14,000) in persistence + gspUserStat.
        Assert.Equal(14_000, (await wallets.FindByUserNumAsync(hostRecord.UserNum, server.Ct))!.Lucci);
        Assert.Equal(1, (await inventory.ListSlotsAsync(hostRecord.UserNum, InventoryBag.CharacterBag, 64, server.Ct))
            .Where(s => s.ItemId == HostTicketItemId).Sum(s => s.Quantity));
        Assert.Contains(host.Since(hostCursor), f => f.Opcode == Kr1256S2CUserStat);

        // 3) Simulate host disconnecting mid-match -> cancels & queues offline refund claim.
        await server.DisconnectAsync("uhhost");
        for (var i = 0; i < 60 && (await wallets.FindByUserNumAsync(hostRecord.UserNum, server.Ct))!.Lucci == 14_000; i++)
            await Task.Delay(25, server.Ct);

        // Note: host's claim sits pending while offline, flushed on next TCP login!
        var hostReconnect = await server.ConnectAsync("uhhost");
        await hostReconnect.Login();
        Assert.Equal(20_000, (await wallets.FindByUserNumAsync(hostRecord.UserNum, server.Ct))!.Lucci);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledUserHostProfileNeverAdmitsPrizeRoomsOverTcp(bool dialectUserHostEnabled)
    {
        await using var server = await ScenarioServer.StartAsync((g, r) => Kr1256UserHostRows(g, r, enabled: false, dialectEnabled: dialectUserHostEnabled));
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var host = await server.ConnectAsync("disabledhost");
        await host.Login();

        // No gspUserHostDefaultSetting published on login.
        Assert.DoesNotContain(host.Since(0), f => f.Opcode == Kr1256S2CUserHostDefaultSetting);

        // When dialect strips is_user_host, an ordinary non-prize room opens with is_user_host=false;
        // when dialect forwards IsUserHost=true while InGame has user_host_enabled=false, room creation is rejected.
        var cursor = host.Cursor;
        var createPayload = BuildPrizeCreatePayload(tables, "kr-1256", "DeniedCup", mode: 3);
        await host.Send(Kr1256C2SRoomCreate, createPayload);
        if (dialectUserHostEnabled)
        {
            await Task.Delay(200, server.Ct);
            Assert.DoesNotContain(host.Since(cursor), f => f.Opcode == Kr1256S2CSceneInstall);
            return;
        }

        var install = await host.WaitFor(Kr1256S2CSceneInstall, cursor);
        var reader = new PacketReader(install.Payload);
        var sessionFields = PacketCodec.ReadPrefix(ref reader, tables, "kr-1256", "SessionSetting");
        var gameFields = Assert.IsType<Dictionary<string, object?>>(sessionFields["game_setting"]);
        Assert.Equal(false, gameFields["is_user_host"]);

        var settingCursor = host.Cursor;
        await host.Send(Kr1256C2SUserHostSetting, PacketCodec.Write(tables, "kr-1256", "UserHostSettingRequest", new Dictionary<string, object?>
        {
            ["action"] = (int)UserHostSettingAction.ConfirmPrize,
            ["prize"] = 5_000,
        }));
        await Task.Delay(150, server.Ct);
        Assert.DoesNotContain(host.Since(settingCursor), f => f.Opcode == Kr1256S2CUserHostSetting);
    }

    private static async Task JoinWithDiag(SimActor actor, ushort roomId)
    {
        var cursor = actor.Cursor;
        try
        {
            await actor.JoinRoom(roomId);
        }
        catch (TimeoutException ex)
        {
            var seen = string.Join(" ", actor.Since(cursor).Select(f =>
                $"S{f.Opcode}({f.Payload.Length}:{Convert.ToHexString(f.Payload.AsSpan(0, Math.Min(8, f.Payload.Length)))})"));
            throw new Xunit.Sdk.XunitException($"{ex.Message}; seen since join cursor (roomId={roomId}): {(seen.Length == 0 ? "nothing" : seen)}");
        }
    }

    private static byte[] BuildPrizeCreatePayload(PacketTables tables, string build, string roomName, int mode) =>
        PacketCodec.Write(tables, build, "RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = roomName,
                ["game_setting"] = new Dictionary<string, object?>
                {
                    ["mode"] = mode,
                    ["is_user_host"] = true,
                },
            },
            ["password"] = "",
            ["unknown_n2"] = (byte)0xBA,
        });
}
