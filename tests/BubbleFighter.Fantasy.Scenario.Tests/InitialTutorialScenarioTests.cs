using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Scenario.Tests;

/// <summary>Exercises the initial tutorial from the encrypted login through solo loading and menu return.</summary>
public sealed class InitialTutorialScenarioTests
{
    /// <summary>The packed rule byte remains one byte and complete requests reject every truncated or extended body.</summary>
    [Fact]
    public void Kr1256InitialTutorialRequestPinsTheNativePackedEnvelope()
    {
        var body = Request();
        Assert.Equal(177, body.Length);
        var fields = PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialPlayRequest", body);
        Assert.Equal((byte)16, fields["unknown_52"]);
        Assert.Equal(241000, fields["time_attack_time_limit_ms"]);
        Assert.Equal("rel_Village07_Easy02", fields["map_key"]);
        Assert.Equal("directory-name", fields["map_name"]);
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialPlayRequest", truncated));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialPlayRequest", [.. body, 0]));
        var exit = PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialEndStage", new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(1, exit["unknown_0"]);
        Assert.Equal(false, exit["unknown_4"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialEndStage", new byte[7]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "TutorialEndStage", new byte[9]));
    }

    /// <summary>The initial tutorial opens without a room-create click or match connection and releases its hidden room on exit.</summary>
    /// <param name="nativeExit">Uses the native tutorial-exit confirmation instead of the menu request.</param>
    /// <param name="failed">Uses the native failure sequence instead of the completion sequence.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Kr1256InitialTutorialStartsOverGameAndReturnsToMenu(bool nativeExit, bool failed)
    {
        await using var server = await ParityRun.StartAsync(rounds: true, initialTutorial: true);
        var player = await server.ConnectAsync("initialTutorial");
        await player.Login();
        var from = player.Cursor;
        await player.Send(107, Request());
        var install = await player.WaitFor(117, from);
        var room = PacketCodec.Read(ParityRun.Tables, "kr-1256", "gspGotoRoom", install.Payload);
        var slots = Assert.IsType<object[]>(room["user_slots"]);
        var local = Assert.IsType<Dictionary<string, object?>>(Assert.Single(slots));
        Assert.Equal((byte)2, local["team"]);
        var update = await player.WaitFor(503, from);
        var updated = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomSeatUpdate", update.Payload);
        Assert.Equal((byte)2, Assert.IsType<Dictionary<string, object?>>(updated["user"])["team"]);
        var setting = Assert.IsType<Dictionary<string, object?>>(room["session_setting"]);
        var game = Assert.IsType<Dictionary<string, object?>>(setting["game_setting"]);
        Assert.Equal(2, setting["kind"]);
        Assert.Equal((ushort)103, game["mode"]);
        Assert.Equal(1, game["unknown_140"]);
        Assert.Equal(false, game["unknown_322"]);
        Assert.Equal(false, game["unknown_323"]);
        Assert.Equal(true, game["unknown_324"]);
        Assert.Equal(false, game["unknown_325"]);
        Assert.Equal(false, game["unknown_326"]);
        Assert.Equal(true, game["unknown_340"]);
        Assert.Equal(true, game["unknown_341"]);
        Assert.Equal(-1, game["unknown_404"]);
        foreach (var key in new[] { "unknown_44", "unknown_68", "unknown_92", "unknown_116" })
            Assert.Equal("rel_prologue_01", game[key]);
        var enter = await player.WaitFor(510, from);
        var stage = PacketCodec.Read(ParityRun.Tables, "kr-1256", "RoomEnterGame", enter.Payload);
        Assert.Equal("rel_prologue_01", stage["stage_key"]);
        await player.Send(607, ParityRun.Body("RoundObjectAddRequest", new() { ["object_id"] = 1000000 }));
        await player.Send(578, new byte[16]);
        var ready = await player.WaitFor(585, from);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, ready.Payload);
        var started = await player.WaitFor(608, from, seconds: 12);
        Assert.Equal(99999000, PacketCodec.ReadScalars(ParityRun.Tables, "kr-1256", "RoundStartGame", started.Payload)["unknown_4"]);
        Assert.DoesNotContain(player.Since(from), frame => frame.Opcode == 571);
        var wave = player.Cursor;
        await player.Send(903, new byte[] { 1, 0, 0, 0 });
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, (await player.WaitFor(904, wave)).Payload);
        var table = SpawnTable();
        wave = player.Cursor;
        await player.Send(908, table);
        Assert.Empty((await player.WaitFor(908, wave)).Payload);
        Assert.Equal(table, (await player.WaitFor(909, wave)).Payload);
        Assert.Equal(new ushort[] { 908, 909 }, player.Since(wave).Where(frame => frame.Opcode is 908 or 909).Select(frame => frame.Opcode));
        wave = player.Cursor;
        await player.Send(32857, new byte[] { 3, 0, 0, 0 });
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, (await player.WaitFor(32857, wave)).Payload);
        var member = ParityRun.Body("TutorialSpawnMember", new()
        {
            ["unknown_0"] = 7, ["unknown_4"] = 3, ["unknown_8"] = "npc-preset",
            ["unknown_32"] = new[] { 0x3F800000, 0, 0, 0 },
        });
        wave = player.Cursor;
        await player.Send(32862, member);
        Assert.Equal(member, (await player.WaitFor(32862, wave)).Payload);
        foreach (var (opcode, structure, values) in new (ushort, string, Dictionary<string, object?>)[]
        {
            (32892, "TutorialSpawnActor", new() { ["unknown_0"] = new[] { 7, 3, 1, 2, 5, 0, 0, 0, 0 } }),
            (32866, "TutorialActorReplay", new() { ["unknown_0"] = 7, ["unknown_4"] = 3, ["unknown_8"] = "npc-replay", ["unknown_32"] = 1 }),
            (32873, "TutorialWaveMobGroup", new() { ["unknown_0"] = (byte)1, ["unknown_1"] = (byte)2, ["unknown_2"] = 7 }),
            (32876, "TutorialSpawnPointReplay", new() { ["unknown_0"] = 7, ["unknown_4"] = true }),
        })
        {
            wave = player.Cursor;
            var body = ParityRun.Body(structure, values);
            await player.Send(opcode, body);
            Assert.Equal(body, (await player.WaitFor(opcode, wave)).Payload);
        }
        wave = player.Cursor;
        await player.Send(904, new byte[] { 1, 0, 0, 0 });
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, (await player.WaitFor(905, wave)).Payload);
        wave = player.Cursor;
        await player.Send(failed ? (ushort)906 : (ushort)905, []);
        Assert.Empty((await player.WaitFor(failed ? (ushort)907 : (ushort)906, wave)).Payload);
        var exit = player.Cursor;
        await player.Send(nativeExit ? (ushort)624 : (ushort)105,
            nativeExit ? new byte[] { failed ? (byte)0 : (byte)1, 0, 0, 0, 0, 0, 0, 0 } : []);
        await player.WaitFor(117, exit);
        // A new ordinary room proves the hidden solo membership was removed.
        var create = player.Cursor;
        await player.Send(106, []);
        await player.WaitFor(117, create);
        create = player.Cursor;
        await player.SendReference(402);
        await player.WaitFor(503, create);
    }

    /// <summary>Variable NPC tables preserve their fields and reject truncation, trailing bytes and impossible list counts.</summary>
    [Fact]
    public void Kr1256NpcSpawnTablePinsVariableRecords()
    {
        var table = SpawnTable();
        Assert.Equal(72, table.Length);
        var fields = PacketCodec.Read(ParityRun.Tables, "kr-1256", "HordeWaveTable", table);
        Assert.Equal(table, PacketCodec.Write(ParityRun.Tables, "kr-1256", "HordeWaveTable", fields));
        for (var length = 0; length < table.Length; length++)
        {
            var truncated = table[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(ParityRun.Tables, "kr-1256", "HordeWaveTable", truncated));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(ParityRun.Tables, "kr-1256", "HordeWaveTable", [.. table, 0]));
        var impossible = table.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(impossible.AsSpan(10), int.MaxValue);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(ParityRun.Tables, "kr-1256", "HordeWaveTable", impossible));
        Assert.Equal(14, ParityRun.Body("HordeWaveTable", new()).Length);
        Assert.Equal(36, ParityRun.Body("TutorialSpawnActor", new()).Length);
        Assert.Equal(28, ParityRun.Body("TutorialSpawnMember", new()).Length);
        Assert.Equal(16, ParityRun.Body("TutorialActorReplay", new()).Length);
        Assert.Equal(6, ParityRun.Body("TutorialWaveMobGroup", new()).Length);
        Assert.Equal(8, ParityRun.Body("TutorialSpawnPointReplay", new()).Length);
    }

    /// <summary>An unavailable generation fails at activation instead of silently selecting a training room.</summary>
    /// <param name="kind">A recognized generation whose native route has not been implemented.</param>
    [Theory]
    [InlineData("classic")]
    [InlineData("ai_battle")]
    [InlineData("not_a_tutorial")]
    public async Task UnavailableTutorialGenerationFailsConfiguration(string kind)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var server = await ParityRun.StartAsync(initialTutorial: true, tutorialKind: kind);
        });
        Assert.Contains(kind == "not_a_tutorial" ? "initial_tutorial_kind" : "no implemented route", error.ToString());
    }

    /// <summary>NPC requests cannot be acknowledged from the menu or from an ordinary waiting room.</summary>
    /// <param name="ordinaryRoom">Admits the player to an ordinary room before sending the tutorial-only request.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TutorialNpcRequestsRequireTheirOwnSoloRoom(bool ordinaryRoom)
    {
        await using var server = await ParityRun.StartAsync(initialTutorial: true);
        var player = await server.ConnectAsync("unadmittedNpcHost");
        await player.Login();
        if (ordinaryRoom)
        {
            await player.EnterPlaza();
            var create = player.Cursor;
            await player.SendReference(402);
            await player.WaitFor(503, create);
        }
        var from = player.Cursor;
        await player.Send(903, new byte[] { 1, 0, 0, 0 });
        await Assert.ThrowsAsync<IOException>(() => player.WaitFor(904, from));
        Assert.DoesNotContain(player.Since(from), frame => frame.Opcode == 904);
    }

    private static byte[] SpawnTable() => ParityRun.Body("HordeWaveTable", new()
    {
        ["wave"] = 1, ["unknown_4"] = (ushort)2, ["unknown_6"] = 3,
        ["mobs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["unknown_4"] = 7, ["unknown_8"] = "npc-preset", ["unknown_32"] = "npc-script",
                ["unknown_56"] = true, ["unknown_57"] = (byte)1, ["unknown_58"] = (byte)2,
            },
        },
    });

    private static byte[] Request()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        foreach (var word in new[] { 3000, 6000, 4000, 11000, 45000, 142000, 20, 240000, 241000, 240000, 240000, 15000, 15000 })
            writer.WriteInt32(word);
        writer.WriteByte(16);
        writer.WriteWideStringPrefixed("rel_Village07_Easy02");
        writer.WriteWideStringPrefixed("directory-name");
        writer.WriteWideStringPrefixed("rel_Village07_Easy02");
        writer.WriteInt32(1);
        return buffer.WrittenSpan.ToArray();
    }
}
