using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Diagnostics;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Checks lossless payload snapshots and explicit capture memory overflow without affecting gameplay.</summary>
public sealed class PacketCaptureTests
{
    [Fact]
    public async Task BorrowedPayloadIsCopiedAndBothDirectionsAreWrittenAsJsonLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-capture-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "capture.jsonl");
        try
        {
            var options = new PacketLogOptions { CaptureFile = path, FileTimeZone = "UTC" };
            await using (var capture = new PacketCapture(options, failure => throw failure))
            {
                var session = new CaptureSession();
                var bytes = new byte[] { 0, 255, 128, 1 };
                capture.Record(session, "kr-812/game", "c2s", 701, "gcpGameReportMapLoadingComplete", bytes, true);
                Array.Fill(bytes, (byte)42);
                capture.Record(session, "kr-812/game", "s2c", 704, "gspGameStartGame", ReadOnlyMemory<byte>.Empty, null);
            }
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var inbound = JsonDocument.Parse(lines[0]);
            Assert.Equal("00FF8001", inbound.RootElement.GetProperty("payload_hex").GetString());
            Assert.Equal(4, inbound.RootElement.GetProperty("length").GetInt32());
            Assert.Equal("c2s", inbound.RootElement.GetProperty("direction").GetString());
            Assert.True(inbound.RootElement.GetProperty("handled").GetBoolean());
            using var outbound = JsonDocument.Parse(lines[1]);
            Assert.Equal("s2c", outbound.RootElement.GetProperty("direction").GetString());
            Assert.Equal("", outbound.RootElement.GetProperty("payload_hex").GetString());
            Assert.Equal(JsonValueKind.Null, outbound.RootElement.GetProperty("handled").ValueKind);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OversizePacketProducesOverflowRecordAndDoesNotPreventTheNextPacket()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-capture-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "capture.jsonl");
        try
        {
            var options = new PacketLogOptions { CaptureFile = path, FileTimeZone = "UTC", CaptureMaxQueuedBytes = 4 };
            await using (var capture = new PacketCapture(options, failure => throw failure))
            {
                var session = new CaptureSession();
                capture.Record(session, "kr-812/game", "c2s", 1, "oversize", new byte[5], false);
                capture.Record(session, "kr-812/game", "s2c", 2, "accepted", new byte[] { 3, 4 }, null);
            }
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var overflow = JsonDocument.Parse(lines[0]);
            Assert.Equal("capture_overflow", overflow.RootElement.GetProperty("type").GetString());
            Assert.Equal(1, overflow.RootElement.GetProperty("dropped").GetInt32());
            using var packet = JsonDocument.Parse(lines[1]);
            Assert.Equal("0304", packet.RootElement.GetProperty("payload_hex").GetString());
            Assert.Equal(2, packet.RootElement.GetProperty("sequence").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class CaptureSession : ISession
    {
        public long Id => 7;
        public IConnection Connection { get; } = new WireSessionConcurrencyTests.OutboundCaptureConnection();
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
