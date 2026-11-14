using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Tests for <see cref="OpcodeTableLoader"/> and <see cref="OpcodeTable"/>.</summary>
public sealed class OpcodeTableTests
{
    private static string TablePath => Path.Combine(AppContext.BaseDirectory, "opcodes.yaml");

    [Fact]
    public void ShippedTableLoadsSeedRows()
    {
        var table = OpcodeTableLoader.Load(TablePath, TestWireDialects.Get("kr-1256").Profile);
        Assert.True(table.TryGet(PacketDirection.ClientToServer, 1, out var c2s));
        Assert.Equal("gcpReqGamePatch", c2s!.Name);
        Assert.Equal(Evidence.Verified, c2s.Evidence);
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 2, out var s2c));
        Assert.Equal("gspRepGamePatch", s2c!.Name);
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 163, out _));
        // C2S and S2C numeric spaces are separate.
        Assert.Equal("gspDisconnect", table.Describe(PacketDirection.ServerToClient, 1).Split('(')[0]);
        Assert.Equal("gcpReqGamePatch", table.Describe(PacketDirection.ClientToServer, 1).Split('(')[0]);
    }

    [Fact]
    public void DescribeFormatsKnownAndUnknown()
    {
        var table = OpcodeTableLoader.Load(TablePath, TestWireDialects.Get("kr-1256").Profile);
        Assert.Equal("gspRepGamePatch(2)", table.Describe(PacketDirection.ServerToClient, 2));
        Assert.Equal("Unknown(999)", table.Describe(PacketDirection.ServerToClient, 999));
    }

    [Fact]
    public void NamesResolvePerDirectionAndMissingNamesFailLoud()
    {
        var table = OpcodeTableLoader.Load(TablePath, TestWireDialects.Get("kr-1256").Profile);
        Assert.Equal((ushort)3, table.C2S("gcpReqLogin"));
        Assert.Equal((ushort)117, table.S2C("gspGotoRoom"));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gspGotoRoom"));
        var error = Assert.Throws<ConfigException>(() => table.S2C("gspNoSuchPacket"));
        Assert.Contains("kr-1256", error.Message);
    }

    [Fact]
    public void ProtocolNamesSelectTheirProfilesTable()
    {
        var registry = new Registry(OpcodeTableLoader.Load(TablePath, TestWireDialects.Get("kr-1256").Profile));
        Assert.Equal("kr-1256", registry.ForProtocol("kr-1256/game").Profile.Id);
        Assert.Throws<ConfigException>(() => registry.ForProtocol("cn-774/game"));
        Assert.Throws<ConfigException>(() => registry.ForProtocol("game"));
    }

    private sealed class Registry(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];

        public IDisposable Register(IOpcodeTable t) => throw new NotSupportedException();

        public IOpcodeTable Get(string profileId) => TryGet(profileId, out var t) ? t! : throw new InvalidOperationException(profileId);

        public bool TryGet(string profileId, out IOpcodeTable? t)
        {
            t = profileId == table.Profile.Id ? table : null;
            return t is not null;
        }
    }

    [Fact]
    public void DuplicateDirectionValueFails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dup-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            profile: kr-1256
            opcodes:
              - {value: 1, direction: c2s, name: first, evidence: verified}
              - {value: 1, direction: c2s, name: second, evidence: verified}
            """);
        try
        {
            Assert.Throws<ConfigException>(() => OpcodeTableLoader.Load(path, TestWireDialects.Get("kr-1256").Profile));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SameValueInBothDirectionsIsAllowed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"both-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            profile: kr-1256
            opcodes:
              - {value: 1, direction: c2s, name: in, evidence: verified}
              - {value: 1, direction: s2c, name: out, evidence: verified}
            """);
        try
        {
            var table = OpcodeTableLoader.Load(path, TestWireDialects.Get("kr-1256").Profile);
            Assert.Equal(2, table.All.Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CapturedEvidenceAndNameSourceLoad()
    {
        var table = LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 5, direction: s2c, name: gspThing, evidence: captured, name_source: label@kr-1256}
              - {value: 6, direction: c2s, name: gcpThing, evidence: verified, name_source: mirrored}
              - {value: 7, direction: c2s, name: Unknown7, evidence: inferred, name_source: none}
              - {value: 8, direction: c2s, name: Unknown8, evidence: inferred}
            """);
        Assert.True(table.TryGet(PacketDirection.ServerToClient, 5, out var captured));
        Assert.Equal(Evidence.Captured, captured!.Evidence);
        Assert.Equal("label@kr-1256", captured.NameSource);
        Assert.True(table.TryGet(PacketDirection.ClientToServer, 8, out var unclassified));
        Assert.Null(unclassified!.NameSource);
    }

    [Theory]
    [InlineData("{value: 1, direction: c2s, name: gcpA, evidence: verified, name_source: guessed}")]
    [InlineData("{value: 1, direction: c2s, name: gcpA, evidence: verified, name_source: label@}")]
    [InlineData("{value: 1, direction: c2s, name: gcpA, evidence: verified, name_source: none}")]
    [InlineData("{value: 1, direction: c2s, name: Unknown1, evidence: verified, name_source: mirrored}")]
    [InlineData("{value: 1, direction: c2s, name: gcpA, evidence: observed}")]
    [InlineData("{value: 1, direction: c2s, name: gcpReqA_, evidence: verified}")]
    [InlineData("{value: 1, direction: c2s, name: gcpReqA_, evidence: verified, name_source: label@kr-812}")]
    [InlineData("{value: 1, direction: c2s, name: gcpReqA, evidence: verified, name_source: coined}")]
    [InlineData("{value: 1, direction: c2s, name: gcpA, evidence: verified, connection: match}")]
    public void InvalidNameSourceOrEvidenceFails(string row)
    {
        Assert.Throws<ConfigException>(() => LoadInline($"profile: kr-1256\nopcodes:\n  - {row}\n"));
    }

    [Fact]
    public void CoinedNamesEndInUnderscore()
    {
        var table = LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 9, direction: c2s, name: gcpReqThing_, evidence: inferred, name_source: coined}
            """);
        Assert.Equal((ushort)9, table.C2S("gcpReqThing_"));
    }

    [Fact]
    public void GameAndRelayRowsShareValuesWithoutCrossTalk()
    {
        var table = LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 6, direction: c2s, name: gcpRepPing, evidence: verified}
              - {value: 6, direction: c2s, name: gcpReportRelayConnected, evidence: verified, connection: relay}
              - {value: 7, direction: c2s, name: gcpShared, evidence: verified, connection: both}
            """);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.Equal("gcpRepPing(6)", table.Describe(PacketDirection.ClientToServer, 6));
        Assert.Equal("gcpReportRelayConnected(6)", relay.Describe(PacketDirection.ClientToServer, 6));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpReportRelayConnected"));
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "gcpRepPing"));
        Assert.Equal((ushort)7, table.C2S("gcpShared"));
        Assert.Equal((ushort)7, relay.C2S("gcpShared"));
        Assert.Same(table, relay.ForConnection(OpcodeConnection.Game));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.ForConnection(OpcodeConnection.Both));

        var registry = new Registry(table);
        Assert.Equal(OpcodeConnection.Relay, registry.ForProtocol("kr-1256/relay").Connection);
        Assert.Equal(OpcodeConnection.Game, registry.ForProtocol("kr-1256/game").Connection);
        Assert.Throws<ConfigException>(() => registry.ForProtocol("kr-1256/match"));
        Assert.False(registry.TryForProtocol("kr-1256/match", out _));
    }

    /// <summary>Resolves native match and report names without changing another connection or direction's rows.</summary>
    [Theory]
    [InlineData("kr-1256", "gspClanPassport(21)", "gspRepJoinSession(103)")]
    [InlineData("cn-738", "Unknown(21)", "Unknown(103)")]
    [InlineData("cn-742", "Unknown(21)", "Unknown(103)")]
    [InlineData("cn-774", "gspDisconnectOpenWeb(21)", "gspRepJoinSession(103)")]
    [InlineData("tw-454", "gspDisconnectOpenWeb(21)", "gspRepJoinSession(103)")]
    public void NativeMatchNamesStayInTheirConnectionAndDirection(string build, string game21, string game103)
    {
        var table = OpcodeTableLoader.Load(ProfileTablePath(build), TestWireDialects.Get(build).Profile);
        var registry = new Registry(table);
        var game = registry.ForProtocol($"{build}/game");
        var relay = registry.ForProtocol($"{build}/relay");
        var udp = registry.ForProtocol($"{build}/udp");
        (string Protocol, PacketDirection Direction, ushort Value, string Name)[] names =
        [
            ($"{build}/relay", PacketDirection.ClientToServer, 17, "rcpGameStart"),
            ($"{build}/relay", PacketDirection.ClientToServer, 24, "rcpCriticalBuffData"),
            ($"{build}/relay", PacketDirection.ClientToServer, 29, "rcpReqHackDataRandomCheck"),
            ($"{build}/relay", PacketDirection.ClientToServer, 20006, "rcpReqLagDisPing"),
            ($"{build}/relay", PacketDirection.ServerToClient, 21, "rspRepLagDisPing"),
            ($"{build}/game", PacketDirection.ClientToServer, 103, "gcpReportUserTracking"),
            ($"{build}/relay", PacketDirection.ClientToServer, 32889, "gppCharactersPlayData"),
        ];
        foreach (var (protocol, direction, value, name) in names)
        {
            var expected = registry.ForProtocol(protocol);
            Assert.Equal((ushort?)value, expected.Find(direction, name));
            var opposite = direction == PacketDirection.ClientToServer
                ? PacketDirection.ServerToClient : PacketDirection.ClientToServer;
            Assert.Null(expected.Find(opposite, name));
            foreach (var other in new[] { game, relay, udp }.Where(view => view.Connection != expected.Connection))
                Assert.Null(other.Find(direction, name));
        }
        Assert.Equal(game21, game.Describe(PacketDirection.ServerToClient, 21));
        Assert.Equal("rspRepLagDisPing(21)", relay.Describe(PacketDirection.ServerToClient, 21));
        Assert.Equal(game103, game.Describe(PacketDirection.ServerToClient, 103));
        Assert.Null(relay.Find(PacketDirection.ServerToClient, "gppCharactersPlayData"));
    }

    /// <summary>Resolves the native pre-countdown only in game S2C while retaining the separate countdown.</summary>
    [Theory]
    [InlineData("kr-812", 737, 703)]
    [InlineData("kr-1256", 606, 607)]
    [InlineData("cn-738", 739, 703)]
    [InlineData("cn-742", 739, 703)]
    [InlineData("cn-774", 637, 601)]
    [InlineData("tw-454", 637, 601)]
    public void NativePreCountdownNamesStayInGameServerToClient(string build, ushort preCountdown, ushort countdown)
    {
        const string name = "gspGamePreCountDown";
        var registry = new Registry(OpcodeTableLoader.Load(ProfileTablePath(build), TestWireDialects.Get(build).Profile));
        var game = registry.ForProtocol($"{build}/game");
        Assert.Equal(preCountdown, game.S2C(name));
        Assert.Equal(countdown, game.S2C("gspGameCountDown"));
        Assert.True(game.TryGet(PacketDirection.ServerToClient, preCountdown, out var entry));
        Assert.Equal(name, entry!.Name);
        Assert.Equal(OpcodeConnection.Game, entry.Connection);
        Assert.Equal(Evidence.Verified, entry.Evidence);
        Assert.Equal("label@kr-812", entry.NameSource);
        Assert.Null(game.Find(PacketDirection.ClientToServer, name));
        foreach (var connection in new[] { "relay", "udp" })
        {
            var other = registry.ForProtocol($"{build}/{connection}");
            Assert.Null(other.Find(PacketDirection.ServerToClient, name));
            Assert.Null(other.Find(PacketDirection.ClientToServer, name));
        }
    }

    private static string ProfileTablePath(string build)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "profiles", build, "opcodes.yaml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"The opcode table for {build} was not found above the test output.");
    }

    [Theory]
    [InlineData("connection: relay", "connection: relay")]
    [InlineData("connection: both", "connection: game")]
    [InlineData("connection: both", "connection: relay")]
    [InlineData("", "connection: both")]
    public void OverlappingScopesFail(string first, string second)
    {
        Assert.Throws<ConfigException>(() => LoadInline($$"""
            profile: kr-1256
            opcodes:
              - {value: 6, direction: c2s, name: gcpA, evidence: verified, {{(first.Length == 0 ? "connection: game" : first)}}}
              - {value: 6, direction: c2s, name: gcpB, evidence: verified, {{second}}}
            """));
    }

    [Fact]
    public void UdpHasIndependentOpcodesAndDoesNotInheritBothTcpRows()
    {
        var table = LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 2, direction: c2s, name: gcpStartGPK, evidence: verified, connection: game}
              - {value: 2, direction: c2s, name: rcpScene, evidence: verified, connection: relay}
              - {value: 2, direction: c2s, name: upTrySendUdpToServer, evidence: verified, connection: udp}
              - {value: 7, direction: c2s, name: gcpShared, evidence: verified, connection: both}
            """);
        var udp = table.ForConnection(OpcodeConnection.Udp);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.Equal("gcpStartGPK(2)", table.Describe(PacketDirection.ClientToServer, 2));
        Assert.Equal("rcpScene(2)", relay.Describe(PacketDirection.ClientToServer, 2));
        Assert.Equal("upTrySendUdpToServer(2)", udp.Describe(PacketDirection.ClientToServer, 2));
        Assert.False(udp.TryGet(PacketDirection.ClientToServer, 7, out _));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "upTrySendUdpToServer"));
        Assert.Null(relay.Find(PacketDirection.ClientToServer, "upTrySendUdpToServer"));
        Assert.Same(udp, relay.ForConnection(OpcodeConnection.Udp));
        Assert.Same(relay, udp.ForConnection(OpcodeConnection.Relay));
        Assert.Same(table, udp.ForConnection(OpcodeConnection.Game));
        var registry = new Registry(table);
        Assert.Same(udp, registry.ForProtocol("kr-1256/udp"));
        Assert.True(registry.TryForProtocol("kr-1256/udp", out var resolved));
        Assert.Same(udp, resolved);
    }

    [Fact]
    public void DuplicateNameInOneScopeFails()
    {
        Assert.Throws<ConfigException>(() => LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 6, direction: c2s, name: gcpA, evidence: verified}
              - {value: 7, direction: c2s, name: gcpA, evidence: verified}
            """));
        var table = LoadInline("""
            profile: kr-1256
            opcodes:
              - {value: 6, direction: c2s, name: gcpA, evidence: verified}
              - {value: 7, direction: c2s, name: gcpA, evidence: verified, connection: relay}
            """);
        Assert.Equal((ushort)7, table.ForConnection(OpcodeConnection.Relay).C2S("gcpA"));
    }

    private static OpcodeTable LoadInline(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"inline-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return OpcodeTableLoader.Load(path, TestWireDialects.Get("kr-1256").Profile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileFails()
    {
        Assert.Throws<ConfigException>(() => OpcodeTableLoader.Load("no-such-file.yaml", TestWireDialects.Get("kr-1256").Profile));
    }

    [Fact]
    public void HandshakeAndPreHandshakeOpcodesResolveFromTheTable()
    {
        var table = OpcodeTableLoader.Load(TablePath, TestWireDialects.Get("kr-1256").Profile);
        Assert.Equal(WirePins.PatchRequestOpcode, table.C2S("gcpReqGamePatch"));
        Assert.Equal(WirePins.PatchReplyOpcode, table.S2C("gspRepGamePatch"));
        Assert.Equal(WirePins.PreHandshakeOpcode, table.C2S("Unknown521"));
        Assert.Equal(WirePins.MoveChannelOpcode, table.S2C("gspNotifyFirstMoveChannel"));
    }
}
