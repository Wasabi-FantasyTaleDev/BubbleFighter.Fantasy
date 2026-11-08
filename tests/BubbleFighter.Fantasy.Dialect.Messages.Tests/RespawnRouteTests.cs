using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the mounted legacy come-back route end to end: the build's own gcpRespawn bytes reach one typed request,
/// and the mounted grant sender writes the build's own gspRespawn bytes on its own opcode.
/// </summary>
public sealed class RespawnRouteTests
{
    /// <summary>The shared grant keeps the original sender's session-table lookup rather than caching the mount's opcode.</summary>
    [Fact]
    public async Task SpawnConfirmationUsesTheRecipientsRegisteredOpcodeTable()
    {
        const string build = "kr-812";
        var mountTable = TestBuilds.Opcodes(build);
        var sessionTable = new OpcodeTable(mountTable.Profile with { Id = "shifted" },
            mountTable.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 3000)) }));
        using var fixture = new MatchMessagesMountTests.Fixture(mountTable);
        var context = fixture.Context(build, build + "/game");
        var registry = new RecipientTables(sessionTable);
        try
        {
            var binding = new RoundRespawnBinding(build + "/game", mountTable, TestBuilds.Packets, build, registry);
            binding.Mount(context);
            var session = new MatchMessagesMountTests.Session(sessionTable.Profile);
            await binding.SendAsync(session, new RoundPlayerRespawned(9, 1, 1_100_000, 3), default);
            Assert.Equal("shifted", registry.RequestedProfile);
            Assert.Equal(sessionTable.S2C("gspRespawn"), Assert.Single(session.Sent));
            Assert.Equal(Convert.FromHexString("E0C81000030000000100000000000000"), Assert.Single(session.Bodies));
        }
        finally { await context.UnwindAsync(); }
    }

    private sealed class RecipientTables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        internal string? RequestedProfile { get; private set; }
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId)
        {
            RequestedProfile = profileId;
            return profileId == table.Profile.Id ? table : throw new InvalidOperationException("Unknown test profile.");
        }
        public bool TryGet(string profileId, out IOpcodeTable? value)
        {
            value = profileId == table.Profile.Id ? table : null;
            return value is not null;
        }
    }

    // kr-812 sub_503530 reads actor, index, the revive flag and a mode word; cn-324 sub_45EA40 and cn-406 sub_4206B0 end
    // after the flag; cn-279 sub_47E650 ends after the index (respawn.yaml).
    [Theory]
    [InlineData("kr-812", "E0C810000300000001000000", "E0C81000030000000100000000000000")]
    [InlineData("cn-324", "E0C810000300000001000000", "E0C810000300000001000000")]
    [InlineData("cn-406", "E0C810000300000001000000", "E0C810000300000001000000")]
    [InlineData("cn-279", "E0C8100003000000", "E0C8100003000000")]
    public async Task ALegacyRespawnReadsItsOwnBodyAndGrantsWithItsOwnConfirmation(string build, string request, string grant)
    {
        var protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        var (schema, play) = TestBuilds.Of(build);
        try
        {
            new PlayRoomCodec(protocol, build, schema, play, fixture.Services.Get<IOpcodeTableRegistry>(),
                NullLogger.Instance, TestBuilds.Packets).Mount(context);
            var session = new MatchMessagesMountTests.Session(new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build));
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpRespawn"), Convert.FromHexString(request)), default);
            var accepted = Assert.IsType<RoundSpawnAccepted>(Assert.Single(fixture.Messages.Received));
            Assert.Equal((1_100_000, 3, (int?)1), (accepted.Actor, accepted.SpawnIndex, accepted.ActorSeat));
            Assert.Equal(RoundRespawnRules.RoomGrant, accepted.Rules);
            Assert.True(accepted.Request.IsEmpty);

            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundPlayerRespawned>>());
            await sender.SendAsync(session, new RoundPlayerRespawned(9, 1, accepted.Actor, accepted.SpawnIndex), default);
            Assert.Equal(table.S2C("gspRespawn"), Assert.Single(session.Sent));
            Assert.Equal(Convert.FromHexString(grant), Assert.Single(session.Bodies));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPlayerRespawned>>());
    }

    /// <summary>
    /// Every supported build states its respawn rules through its base: the legacy builds the room-wide grant that
    /// revives at once, the modern builds the requester-only echo a match confirmation completes after cleanup.
    /// </summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-408", false)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public void EverySupportedBuildInheritsItsFamilysRespawnRules(string build, bool modern) => AssertRules(build, modern);

    /// <summary>A missing block, an unknown key, a missing key or a value outside its names fails at load.</summary>
    [Theory]
    [InlineData("", "must state the respawn block")]
    [InlineData("    respawn: { body: echo, audience: room, completion: grant, admission: owner, extra: 1 }\n", "unknown key 'extra'")]
    [InlineData("    respawn: { body: echo, audience: room, completion: grant }\n", "admission is missing")]
    [InlineData("    respawn: { body: echo, audience: everyone, completion: grant, admission: owner }\n", "audience must be one of room, requester")]
    public void InvalidRespawnDataFailsAtLoad(string block, string diagnostic)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-respawn-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-812:\n" + (block.Length == 0 ? "    display_level_max: 1\n" : block));
            var error = Assert.Throws<ConfigException>(() => RoundRespawnBinding.RulesOf(PacketTables.Load(directory), "kr-812"));
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertRules(string build, bool modern)
    {
        var (body, rules) = RoundRespawnBinding.RulesOf(TestBuilds.Packets, build);
        Assert.Equal(modern ? RespawnBody.Echo : RespawnBody.SpawnIndex, body);
        Assert.Equal(modern
            ? new RoundRespawnRules(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath)
            : RoundRespawnRules.RoomGrant, rules);
    }
}
