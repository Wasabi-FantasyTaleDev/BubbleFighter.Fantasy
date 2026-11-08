using System.Buffers.Binary;
using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class MatchActionRelayTests
{
    /// <summary>Rod acknowledgements reach both peers and only the live owner's exact pair can link over its current transport.</summary>
    [Fact]
    public async Task MountedFinalZoneTrapRoundTripRejectsForeignAndRepeatedLinks()
    {
        using var h = await Harness.CreateAsync(finalZone: true);
        var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
        var ledger = h.Room.FinalZone!.Items!;
        var bag = Assert.IsType<int>(ledger.TryCreateSupply([new(560, 5921, 2, new([], []))]));
        var pickup = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pickup, bag);
        BinaryPrimitives.WriteInt32LittleEndian(pickup.AsSpan(4), 560);
        await h.DispatchAsync(table.C2S("gcpArea32BackpackPickup_"), pickup, h.HostGame, "kr-1256/game");
        var actor = RoomRegistry.SceneEntity(h.HostSeat.Slot);
        var place = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(place, actor);
        BinaryPrimitives.WriteInt32LittleEndian(place.AsSpan(4), 560);
        BinaryPrimitives.WriteInt32LittleEndian(place.AsSpan(8), 1);
        await h.DispatchAsync(table.C2S("gcpArea32TrapPlace_"), place, h.GuestGame, "kr-1256/game");
        var spawnOpcode = table.S2C("gspArea32TrapSpawn_");
        Assert.DoesNotContain(h.HostGame.SentSnapshot, packet => packet.Opcode == spawnOpcode);
        await h.DispatchAsync(table.C2S("gcpArea32TrapPlace_"), place, h.HostGame, "kr-1256/game");
        await h.DispatchAsync(table.C2S("gcpArea32TrapPlace_"), place, h.HostGame, "kr-1256/game");
        var first = Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == spawnOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(place.AsSpan(8), 3);
        await h.DispatchAsync(table.C2S("gcpArea32TrapPlace_"), place, h.HostGame, "kr-1256/game");
        var second = h.HostGame.SentSnapshot.Last(packet => packet.Opcode == spawnOpcode);
        Assert.Equal(2, h.GuestGame.SentSnapshot.Count(packet => packet.Opcode == spawnOpcode));
        var link = new byte[18];
        BinaryPrimitives.WriteInt32LittleEndian(link, actor);
        first.Body.AsSpan(4, 4).CopyTo(link.AsSpan(4));
        second.Body.AsSpan(4, 4).CopyTo(link.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(link.AsSpan(12), 5921);
        var peerOpcode = table.ForConnection(OpcodeConnection.Relay).C2S("gppArea32TrapLink_");
        await h.DispatchAsync(peerOpcode, link, h.Guest);
        Assert.DoesNotContain(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gppArea32TrapLink_"));
        await h.DispatchAsync(peerOpcode, link, h.Host);
        await h.DispatchAsync(table.C2S("gppArea32TrapLink_"), link, h.HostGame, "kr-1256/game");
        Assert.Equal(link, Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gppArea32TrapLink_")).Body);
        Assert.Equal(link, Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == table.S2C("gppArea32TrapLink_")).Body);
        Assert.DoesNotContain(ledger.Held(actor), item => item.ItemSpecId == 560);
    }

    /// <summary>A scheduled drone becomes one box, and only the current master may request a distinct golden supply.</summary>
    [Fact]
    public async Task MountedFinalZoneArbitratesSeededSuppliesAndCancelsItsDroneClock()
    {
        using var h = await Harness.CreateAsync(finalZone: true);
        var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
        var droneOpcode = table.S2C("gspArea32DroneSpawn_");
        for (var attempt = 0; attempt < 500 && !h.HostGame.SentSnapshot.Any(packet => packet.Opcode == droneOpcode); attempt++)
            await Task.Delay(10);
        var drone = Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == droneOpcode);
        var id = BinaryPrimitives.ReadInt32LittleEndian(drone.Body);
        var report = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(report, id);
        await h.DispatchAsync(table.C2S("gcpArea32DroneDestroyed_"), report, h.HostGame, "kr-1256/game");
        await h.DispatchAsync(table.C2S("gcpArea32DroneDestroyed_"), report, h.GuestGame, "kr-1256/game");
        var box = Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32DroneBoxSpawn_"));
        Assert.Equal(box.Body, Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == box.Opcode).Body);
        var boxId = BinaryPrimitives.ReadInt32LittleEndian(box.Body.AsSpan(20));
        var pickup = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pickup, boxId);
        BinaryPrimitives.WriteInt32LittleEndian(pickup.AsSpan(4), 9001);
        await h.DispatchAsync(table.C2S("gcpArea32BackpackPickup_"), pickup, h.GuestGame, "kr-1256/game");
        await h.DispatchAsync(table.C2S("gcpArea32BackpackPickup_"), pickup, h.HostGame, "kr-1256/game");
        Assert.Equal(7, Assert.Single(h.Room.FinalZone!.Items!.Held(RoomRegistry.SceneEntity(h.GuestSeat.Slot)),
            entry => entry.ItemSpecId == 9001).Quantity);
        Assert.DoesNotContain(h.Room.FinalZone.Items.Held(RoomRegistry.SceneEntity(h.HostSeat.Slot)), entry => entry.ItemSpecId == 9001);
        var golden = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(golden, 1);
        await h.DispatchAsync(table.C2S("gcpArea32GoldenSupply_"), golden, h.GuestGame, "kr-1256/game");
        Assert.DoesNotContain(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32GoldenSupplySpawn_"));
        await h.DispatchAsync(table.C2S("gcpArea32GoldenSupply_"), golden, h.HostGame, "kr-1256/game");
        await h.DispatchAsync(table.C2S("gcpArea32GoldenSupply_"), golden, h.HostGame, "kr-1256/game");
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32GoldenSupplySpawn_"));
        Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32GoldenSupplySpawn_"));
        h.Room.RoundCancelled = true;
        await h.Room.FinalZoneDroneClock!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The mounted dialect loads resources, grants opening inventory, echoes an exclusive pickup and produces the shared result.</summary>
    [Fact]
    public async Task MountedFinalZoneLoadsItemsAndEndsOnTheLastParticipant()
    {
        using var h = await Harness.CreateAsync(finalZone: true);
        var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        Assert.Equal(RoomPhase.Playing, h.Room.Phase);
        var spawned = Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32ItemsSpawn_"));
        var fields = PacketCodec.Read(packets, "kr-1256", "Area32ItemsSpawn", spawned.Body);
        var item = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single((object?[])fields["items"]!));
        var id = (int)item["object_id"]!;
        var actor = RoomRegistry.SceneEntity(h.HostSeat.Slot);
        Assert.Equal(2, h.Room.FinalZone!.Items!.Held(actor).Count);
        var pickup = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pickup, id);
        BinaryPrimitives.WriteInt32LittleEndian(pickup.AsSpan(4), actor);
        await h.DispatchAsync(table.C2S("gcpArea32ItemPickup_"), pickup, h.HostGame, "kr-1256/game");
        await h.DispatchAsync(table.C2S("gcpArea32ItemPickup_"), pickup, h.HostGame, "kr-1256/game");
        var award = table.S2C("gspArea32ItemPicked_");
        Assert.Equal(pickup, Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == award).Body);
        Assert.Equal(pickup, Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == award).Body);
        var death = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(death, RoomRegistry.SceneEntity(h.GuestSeat.Slot));
        BinaryPrimitives.WriteInt32LittleEndian(death.AsSpan(4), actor);
        BinaryPrimitives.WriteInt32LittleEndian(death.AsSpan(24), 1);
        await h.DispatchAsync(table.ForConnection(BubbleFighter.Fantasy.Abstractions.Protocol.OpcodeConnection.Relay)
            .C2S("Unknown38282"), death, h.Guest);
        Assert.Equal(RoomPhase.Result, h.Room.Phase);
        Assert.Equal(new[] { h.HostSeat.Slot }, h.Room.DecidedWinnerSlots);
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == table.S2C("gspArea32PlayerEliminated_"));
        var scoreboard = table.S2C("Unknown614");
        for (var attempt = 0; attempt < 500 && (!h.HostGame.SentSnapshot.Any(packet => packet.Opcode == scoreboard)
            || !h.GuestGame.SentSnapshot.Any(packet => packet.Opcode == scoreboard)); attempt++)
            await Task.Delay(10);
        Assert.Single(h.HostGame.SentSnapshot, packet => packet.Opcode == scoreboard);
        Assert.Single(h.GuestGame.SentSnapshot, packet => packet.Opcode == scoreboard);
    }

    private sealed class FinalZoneStore : IClientDataStore
    {
        private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
        {
            ["ServerShared/MapMeta.xml"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"><Worksheet><Table>
                  <Row><Cell><Data>[Field]</Data></Cell><Cell><Data>Type</Data></Cell><Cell><Data>fixture</Data></Cell></Row>
                  <Row><Cell><Data>Directory</Data></Cell><Cell><Data>string</Data></Cell><Cell><Data>fixture</Data></Cell></Row>
                </Table></Worksheet></Workbook>
                """,
            ["Config/GameSpec/GenItemSetting.yaml"] = """
                GenItemMeta:
                  GunAmmo:
                    catAmmo: { isVariant: true, isReloadNecessary: true, isSplittable: true }
                  ActiveItem:
                    catMedipack: { isVariant: true, isReloadNecessary: false, isSplittable: false }
                  PassiveItem:
                    catDogtag: { isVariant: true, isReloadNecessary: false, isSplittable: false }
                  Etc:
                    catBoobyTrap: { isVariant: false, isReloadNecessary: false, isSplittable: false }
                GenItems:
                  TestAmmo: { genItemId: 9001, genItemCategory: catAmmo, weaponId: -1 }
                  TestMedipack: { genItemId: 580, genItemCategory: catMedipack, weaponId: 5920 }
                  TestDogtag: { genItemId: 590, genItemCategory: catDogtag, weaponId: -1 }
                  TestRod: { genItemId: 560, genItemCategory: catBoobyTrap, weaponId: 5921 }
                """,
            ["Maps/fixture/GenClusterTable.csv"] = "ClusterName\tSpawnGroupName\nRegion\tCommon\n",
            ["Maps/fixture/GenSpawnTable.csv"] = "SpawnGroupName\tPermyriad\tMainCandidates\tBonusCandidates\tBonusSpawnCount\nCommon\t10000\tTestAmmo[12]\t\t0\n",
            ["Maps/fixture/GenSpotList.csv"] = "ClusterName\tSpotSn\tSpotPoint\tSpotValid\nRegion\t1\t1, 2, 3\tTRUE\n",
            ["Maps/fixture/GenDroneRewardTable.csv"] = "SpawnGroupName\tPermyriad\tMainCandidates\tBonusCandidates\tBonusSpawnCount\nReward\t100\tTestAmmo[7]\t\t0\n",
            ["Config/GenDroneSpec.csv"] = "Filter\tProperty\tValue\nReward\tWeaponLotteryCount\t0\nReward\tSupplyLotteryCount\t1\nReward\tWeaponSpawnGroupName\tReward\nReward\tSupplySpawnGroupName\tReward\n",
            ["Config/SurvivalMode.xml"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"><Worksheet><Table>
                  <Row><Cell><Data>[Field]</Data></Cell><Cell><Data>Type</Data></Cell><Cell><Data>fixture</Data></Cell></Row>
                  <Row><Cell><Data>TsunamiUse</Data></Cell><Cell><Data>bool</Data></Cell><Cell><Data>0</Data></Cell></Row>
                  <Row><Cell><Data>DroneUse</Data></Cell><Cell><Data>bool</Data></Cell><Cell><Data>1</Data></Cell></Row>
                  <Row><Cell><Data>DroneTriggerMinSecond</Data></Cell><Cell><Data>int</Data></Cell><Cell><Data>1</Data></Cell></Row>
                  <Row><Cell><Data>DroneTriggerMaxSecond</Data></Cell><Cell><Data>int</Data></Cell><Cell><Data>1</Data></Cell></Row>
                  <Row><Cell><Data>DroneAppearMinSecond</Data></Cell><Cell><Data>int</Data></Cell><Cell><Data>100</Data></Cell></Row>
                  <Row><Cell><Data>DroneAppearMaxSecond</Data></Cell><Cell><Data>int</Data></Cell><Cell><Data>100</Data></Cell></Row>
                </Table></Worksheet></Workbook>
                """,
        };
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] Read(string path)
        {
            if (!Files.TryGetValue(path, out var text)) throw new FileNotFoundException(path);
            var encoding = path.EndsWith(".csv", StringComparison.Ordinal) ? Encoding.Unicode : Encoding.UTF8;
            return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        }
    }
}
