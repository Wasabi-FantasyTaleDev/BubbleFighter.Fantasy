using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the persisted preset bar the default-off <c>persist_preset_pages</c> switch publishes: five
/// server-owned pages in each family's own form. kr-1256 carries every page as its three-word record
/// (<c>kr-1256 sub_115C000</c> reads twelve bytes; <c>kr-1256 sub_81B820</c> keys pages by list position),
/// so pages 3-4 receive records and <c>kr-1256 sub_5FB4F0</c> enables them; kr-812 carries each page's
/// first word in the parallel id vector (<c>kr-812 sub_812190</c>, materialized by <c>kr-812 sub_987BA0</c>
/// at player+6032..6068), the projection <c>kr-812 sub_97A8D0</c>'s <c>/1000 == 204</c> gate reads.
/// The derived bar's pins elsewhere in this suite are the default-off parity: those bytes do not move.
/// </summary>
public sealed class PersistedPresetTableTests
{
    // Page order: two bound primaries, a third page, a bound expansion page, a weaponless fifth page.
    private static readonly WeaponPresetPage[] Pages =
    [
        new(202001, 205001, 0),
        new(203001, 205001, 0),
        new(204001, 0, 0),
        new(202003, 205001, 7),
        new(0, 0, 0),
    ];

    [Fact]
    public void Kr1256UpdateCarriesAllFivePagesAsThreeWordRecords()
    {
        // Active slot 3, identity 282, five 12-byte records, empty byte tail: 73 bytes total.
        var body = PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 3, 282, Pages);
        Assert.Equal(73, body.Length);
        Assert.Equal(
            "03" + "1A010000" + "05000000"
            + "11150300" + "C9200300" + "00000000"
            + "F9180300" + "C9200300" + "00000000"
            + "E11C0300" + "00000000" + "00000000"
            + "13150300" + "C9200300" + "07000000"
            + "00000000" + "00000000" + "00000000"
            + "00000000",
            Convert.ToHexString(body));
    }

    [Fact]
    public void Kr812UpdateProjectsEachPageOntoItsSingleWord()
    {
        // Active slot 3, identity 282: five ids from word 0, flags nonzero-means-bound, zero tail.
        var body = PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 3, 282, Pages);
        Assert.Equal(47, body.Length);
        Assert.Equal(
            "03" + "1A010000" + "05000000"
            + "11150300" + "F9180300" + "E11C0300" + "13150300" + "00000000"
            + "05000000" + "0101010100"
            + "05000000" + "0000000000",
            Convert.ToHexString(body));
    }

    [Fact]
    public void TheDerivedBarKeepsThreeRecordsSoPagesThreeAndFourStayRecordless()
    {
        // The enable rule is record existence: kr-1256 sub_5FB4F0 offers indices 0-2 without a record
        // and disables 3-4 without one, so the shipped three-record derived bar leaves pages 3-4 dead
        // while the persisted five-record bar switches them on.
        var derived = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "WeaponPresetUpdate",
            PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 0, 160, [202001, 201001, 205001, 207001, 0],
                202001, 205001, 3));
        var persisted = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "WeaponPresetUpdate",
            PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 0, 160, Pages));
        Assert.Equal(3, Assert.IsType<object?[]>(Assert.IsType<Dictionary<string, object?>>(derived["presets"])["entries"]).Length);
        var entries = Assert.IsType<object?[]>(Assert.IsType<Dictionary<string, object?>>(persisted["presets"])["entries"]);
        Assert.Equal(5, entries.Length);
        // The fifth page rides without a weapon: its record exists (the bar may offer it) yet its word is
        // zero, the unbound convention the switch handler refuses.
        Assert.Equal(0, Assert.IsType<Dictionary<string, object?>>(entries[4])["unknown_0"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void APersistedBarCarriesExactlyFivePagesOrThrows(int count)
    {
        // The oversized row pads with a sixth page rather than slicing past the table's end.
        var pages = count <= Pages.Length ? Pages[..count]
            : [.. Pages, .. Enumerable.Repeat(new WeaponPresetPage(202001, 0, 0), count - Pages.Length)];
        Assert.Throws<ArgumentException>(() => PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 0, 1, pages));
        Assert.Throws<ArgumentException>(() => PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 0, 1, pages));
        Assert.Throws<ArgumentNullException>(() => PresetWire.WriteUpdate(
            TestBuilds.Packets, "kr-1256", 0, 1, (WeaponPresetPage[])null!));
    }

    [Fact]
    public async Task PersistedPagesInTheMessageReplaceTheDerivedRecordsByteForByte()
    {
        // Default off (no pages in the message): the binding's bar is exactly the derived call the shipped
        // bytes come from. Pages present: it is exactly the persisted call. The binding adds nothing.
        int[] worn = [300001, 202001, 201001, 205001, 207001];
        var view = new EquipmentViewBinding("kr-1256/game", 806, 837, 836, TestBuilds.Packets, "kr-1256");
        var session = new RecordingSession(21);
        await view.SendAsync(session, new LocalEquipmentViewUpdated(42, 300001, worn, 0, 202001, 205001), default);
        Assert.Equal(PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 0, 42, WeaponPresetSlots.FromWorn(worn),
            202001, 205001, 3), session.Bar);

        session = new RecordingSession(22);
        await view.SendAsync(session, new LocalEquipmentViewUpdated(42, 300001, worn, 0, 202001, 205001, Pages), default);
        Assert.Equal(PresetWire.WriteUpdate(TestBuilds.Packets, "kr-1256", 0, 42, Pages), session.Bar);

        var legacy = new EquipmentViewBinding("kr-812/game", 805, 833, 832, TestBuilds.Packets, "kr-812");
        session = new RecordingSession(23);
        await legacy.SendAsync(session, new LocalEquipmentViewUpdated(42, 300001, worn, 0, 202001, 205001, Pages), default);
        Assert.Equal(PresetWire.WriteUpdate(TestBuilds.Packets, "kr-812", 0, 42, Pages), session.Bar);
    }

    private sealed class RecordingSession(long connectionId) : ISession
    {
        public long Id => 1;
        public IConnection Connection { get; } = new StubConnection(connectionId);
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

        /// <summary>Gets the preset bar body of the publication (kr-1256's second part, kr-812's second).</summary>
        internal byte[] Bar => Sent.First(frame => frame.Opcode is 837 or 833).Payload;

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
