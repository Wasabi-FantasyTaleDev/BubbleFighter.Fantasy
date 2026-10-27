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
