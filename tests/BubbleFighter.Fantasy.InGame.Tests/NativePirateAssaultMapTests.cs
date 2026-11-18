using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises mode 106 from a two-seat waiting room, with each region's shipped map keys and wave scripts.</summary>
public sealed class NativePirateAssaultMapTests
{
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("kr-1256", 159, true)]
    [InlineData("cn-774", 157, false)]
    [InlineData("tw-454", 156, true)]
    public async Task PirateAssaultRetainsTheSelectedMissionForBothPlayers(string build, int firstId, bool pharaoh)
    {
        const string port = "rel_NPCpirateclassic02_portVillage", tomb = "rel_NPCpirateclassic01_Pharaoh";
        var store = new NativeStore(build);
        var maps = MapCatalog.Load(store);
        var meta = MapMetaTable.Parse(store.Read(MapMetaTable.Path));
        var missions = Enumerable.Range(1, 4).Select(d => (Key: port + $"0{d}", Stage: port, Id: firstId + d - 1, Difficulty: d)).ToList();
        if (pharaoh) missions.Add((tomb, tomb, 15, 5));
        Assert.Equal(missions.Select(m => m.Key).Order(StringComparer.Ordinal), maps.ModePool(106));
        var waves = Encoding.ASCII.GetString(store.Read($"NpcMode/Scripts/Maps/{port}/{port}_WaveTest.lua"));
        var indices = Regex.Match(waves, @"(?ms)^WaveIndices\s*=\s*\{(?<rows>.*?)^\s*\}").Groups["rows"].Value;
        Assert.Equal(new[] { 5, 5, 6, 7 }, Regex.Matches(indices, @"(?m)^\s*\{([0-9, ]+)\}")
            .Select(row => row.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length));
        foreach (var (key, stage, id, difficulty) in missions)
        {
            Assert.Equal(id.ToString(CultureInfo.InvariantCulture), meta.Get(key, "MapIdx"));
            Assert.Equal(difficulty.ToString(CultureInfo.InvariantCulture), meta.Get(key, "Difficulty"));
            Assert.False(meta.IsSet(key, "isHordeModeMap"));
            var setup = Encoding.ASCII.GetString(store.Read($"NpcMode/Scripts/Maps/{stage}/{stage}.lua"));
            Assert.Contains($"{stage}_WaveTest.lua", setup, StringComparison.Ordinal);
            var rig = new RoomFlowTests.Rig(options: new InGameOptions { ServerMapDraw = true });
            try
            {
                var host = RoomFlowTests.Rig.Player(11, "host");
                var guest = RoomFlowTests.Rig.Player(22, "member");
                rig.Flow.Maps = maps;
                await rig.Flow.HandleCreateAsync(host, new("assault", "", 106, key), default);
                var room = rig.Registry.TryGet(host.Id)!;
                await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
                await rig.Flow.HandleReadyAsync(guest, new(true), default);
                await rig.Flow.HandleStartAsync(host, new("npc_Village07_Easy01"), default);
                Assert.Equal(RoomPhase.Waiting, room.Phase);
                await rig.Flow.HandleStartAsync(host, new(stage) { MapSelection = key }, default);
                Assert.Equal(RoomPhase.Starting, room.Phase);
                foreach (var player in new[] { host, guest })
                {
                    var enter = Assert.Single(rig.Starting.To(player));
                    Assert.Equal((int)GameModeId.PirateAssaultSeason3, (int)enter.Room.ModeId);
                    Assert.Equal(key, enter.MapKey);
                    Assert.Equal(stage, enter.StageKey);
                }
            }
            finally { await rig.Registry.StopAllAsync(); }
        }
    }

    private sealed class NativeStore(string build) : IClientDataStore
    {
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] Read(string path) => LocalClientData.Bytes(build, path);
    }
}
