using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class CatalogOpcodeFallbackTests
{
    [Fact]
    public void ExistingLogicResolvesOnlyMissingNamesAndLogsResearchProvenance()
    {
        WithCatalog("""
            - {value: 55, direction: c2s, name: gcpNeeded, grade: shape, name_source: matched, method: shape, note: test evidence}
            - {value: 56, direction: c2s, name: gcpReviewed, grade: label}
            - {value: 59, direction: c2s, connection: relay, name: rcpNeeded, grade: label}
            """, (path, logger) =>
        {
            var runtime = Table([new OpcodeEntry(56, PacketDirection.ClientToServer, "gcpReviewed", Evidence.Verified, null, null)]);
            var fallback = CatalogOpcodeFallback.Load(new ProtocolOptions { OpcodeCatalogPath = path }, runtime, logger)!;
            var table = new OpcodeTable(runtime.Profile, runtime.All, fallback.Resolve);
            Assert.Equal((ushort)56, table.C2S("gcpReviewed"));
            Assert.Empty(logger.Lines);
            Assert.Equal((ushort)55, table.C2S("gcpNeeded"));
            Assert.Contains(logger.Lines, line => line.Contains("candidate=gcpNeeded?") && line.Contains("grade=shape") && line.Contains("existing codec only"));
            Assert.Null(table.Find(PacketDirection.ServerToClient, "gcpNeeded"));
            Assert.Null(table.Find(PacketDirection.ClientToServer, "rcpNeeded"));
            Assert.Equal((ushort)59, table.ForConnection(OpcodeConnection.Relay).C2S("rcpNeeded"));
            Assert.Single(table.All);
            Assert.False(table.TryGet(PacketDirection.ClientToServer, 55, out _));
        });
    }

    [Theory]
    [InlineData("- {value: 55, direction: c2s, name: gcpNeeded, grade: label}\n- {value: 57, direction: c2s, name: gcpNeeded, grade: label}")]
    [InlineData("- {value: 55, direction: c2s, name: gcpNeeded, grade: label}\n- {value: 55, direction: c2s, name: gcpOther, grade: label}")]
    public void ConflictingCandidatesFailInsteadOfChoosingAValue(string rows)
    {
        WithCatalog(rows, (path, logger) =>
        {
            var runtime = Table([]);
            var fallback = CatalogOpcodeFallback.Load(new ProtocolOptions { OpcodeCatalogPath = path }, runtime, logger)!;
            Assert.Throws<ConfigException>(() => fallback.Resolve(OpcodeConnection.Game, PacketDirection.ClientToServer, "gcpNeeded"));
            Assert.Empty(logger.Lines);
        });
    }

    [Fact]
    public void RuntimeNumericClaimsAreNotOverwrittenByCatalogCandidates()
    {
        WithCatalog("- {value: 55, direction: c2s, name: gcpNeeded, grade: label}", (path, logger) =>
        {
            var runtime = Table([new OpcodeEntry(55, PacketDirection.ClientToServer, "gcpOther", Evidence.Verified, null, null)]);
            var fallback = CatalogOpcodeFallback.Load(new ProtocolOptions { OpcodeCatalogPath = path }, runtime, logger)!;
            Assert.Throws<ConfigException>(() => fallback.Resolve(OpcodeConnection.Game, PacketDirection.ClientToServer, "gcpNeeded"));
        });
    }

    private static OpcodeTable Table(OpcodeEntry[] rows) => new(new ClientProfile("kr-1256", "kr", 1256, 0, 0, "Test"), rows);

    private static void WithCatalog(string rows, Action<string, Recorder> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-fallback-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, "profile: kr-1256\nopcodes:\n" + string.Join('\n', rows.Split('\n').Select(line => "  " + line)) + "\n");
            test(path, new Recorder());
        }
        finally { File.Delete(path); }
    }

    private sealed class Recorder : ILogger
    {
        internal List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
