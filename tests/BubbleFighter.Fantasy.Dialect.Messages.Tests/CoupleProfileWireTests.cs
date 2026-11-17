using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native coupled flag, date and display ring independently of partner presence.</summary>
public sealed class CoupleProfileWireTests
{
    // OLE DATE 2.0 is 1900-01-01; constructing local time keeps this byte pin independent of the test host's zone.
    private static readonly DateTimeOffset Created = new(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local));

    /// <summary>The native optional record announces coupling even when the partner is offline.</summary>
    [Theory]
    [InlineData(false, "00000000")]
    [InlineData(true, "01000000")]
    public void Kr1256ProfileUsesDateDisplayRingAndSeparateOnlineFlag(bool online, string flag)
    {
        var body = CoupleProfileWire.Write(TestBuilds.Packets, "kr-1256", new(Created, 100604, online));
        Assert.Equal("3A" + "0000000000000040" + "FC880100" + "00000000" + flag, Convert.ToHexString(body));
        Assert.Equal(new byte[] { 0 }, CoupleProfileWire.Write(TestBuilds.Packets, "kr-1256", null));
    }

    /// <summary>Each untagged fighter stores coupling as a word before its date, ring and online flag.</summary>
    [Theory]
    [InlineData("kr-812", 28)]
    [InlineData("cn-408", 28)]
    [InlineData("id-400", 28)]
    [InlineData("cn-717", 28)]
    [InlineData("cn-706", 28)]
    [InlineData("cn-406", 28)]
    [InlineData("cn-404", 28)]
    [InlineData("cn-705", 28)]
    [InlineData("id-1809", 28)]
    [InlineData("cn-774", 20)]
    [InlineData("tw-454", 20)]
    public void InlineProfilesUseTheirOwnCoupleFields(string build, int offset)
    {
        var paired = UserStatWire.Write(TestBuilds.Packets, build, "", 1, 0, 0,
            couple: new(Created, 100604, true));
        byte[] FighterBytes(byte[] stat) => PacketCodec.Write(TestBuilds.Packets, build, "FighterInfo",
            Assert.IsType<Dictionary<string, object?>>(PacketCodec.Read(TestBuilds.Packets, build, "UserStat", stat)["fighter"]));
        Assert.Equal("01000000" + "0000000000000040" + "FC880100" + "01000000" + "00000000",
            Convert.ToHexString(FighterBytes(paired).AsSpan(offset, 24)));
        var single = UserStatWire.Write(TestBuilds.Packets, build, "", 1, 0, 0);
        Assert.Equal(new byte[24], FighterBytes(single).AsSpan(offset, 24).ToArray());
    }

    /// <summary>A missing ring model in this build keeps the native coupled flag and the relationship date.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    [InlineData("cn-717")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-705")]
    [InlineData("id-1809")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UnknownDisplayRingDoesNotClearTheRelationship(string build)
    {
        var body = UserStatWire.Write(TestBuilds.Packets, build, "", 1, 0, 0, couple: new(Created, 0, false));
        var fighter = Assert.IsType<Dictionary<string, object?>>(PacketCodec.Read(TestBuilds.Packets, build, "UserStat", body)["fighter"]);
        var coupled = build == "kr-1256" ? Assert.IsType<Dictionary<string, object?>>(fighter["unknown_48"]) : fighter;
        Assert.Equal(0, Assert.IsType<int>(coupled["couple_ring_item"]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(2d), Assert.IsType<long>(coupled["couple_since"]));
        if (build != "kr-1256") Assert.Equal(1, Assert.IsType<int>(fighter["couple_present"]));
    }

    /// <summary>A wallet refresh reads current session state, preserving coupling and later ring or breakup changes.</summary>
    [Fact]
    public async Task WalletRefreshReadsTheLatestCoupleProfileEveryTime()
    {
        var state = new Profile { Current = new(Created, 100604, true) };
        var session = new Session();
        using var registration = session.Features.Register<ICoupleProfile>(state);
        using var scope = new InventoryBaselineScope(1, TimeProvider.System);
        var binding = new ShopBinding("kr-1256/game", TestBuilds.Opcodes("kr-1256"), scope, TestBuilds.Packets, "kr-1256");
        var update = new ShopBalanceUpdated(new(42, "player", "Player", 10), new(42, 50, 20));

        await binding.SendAsync(session, update, default);
        Assert.Equal(100604, Ring(session.Bodies[^1]));
        state.Current = state.Current! with { DisplayRingItemId = 100984, PartnerOnline = false };
        await binding.SendAsync(session, update, default);
        Assert.Equal(100984, Ring(session.Bodies[^1]));
        Assert.False(Assert.IsType<bool>(Coupled(session.Bodies[^1])["couple_partner_online"]));
        state.Current = null;
        await binding.SendAsync(session, update, default);
        Assert.Equal((byte)0, Assert.IsType<byte>(Fighter(session.Bodies[^1])["unknown_48"]));
    }

    private static Dictionary<string, object?> Fighter(byte[] body) => Assert.IsType<Dictionary<string, object?>>(
        PacketCodec.Read(TestBuilds.Packets, "kr-1256", "UserStat", body)["fighter"]);

    private static Dictionary<string, object?> Coupled(byte[] body) =>
        Assert.IsType<Dictionary<string, object?>>(Fighter(body)["unknown_48"]);

    private static int Ring(byte[] body) => Assert.IsType<int>(Coupled(body)["couple_ring_item"]);

    private sealed class Profile : ICoupleProfile
    {
        public CoupleProfileState? Current { get; set; }
    }

    private sealed class Session : ISessionWithFeatures
    {
        private readonly MatchMessagesMountTests.Session _inner = new(new("kr-1256", "kr", 1256, 0, 0, "kr-1256"));
        internal List<byte[]> Bodies => _inner.Bodies;
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => _inner.Id;
        public BubbleFighter.Fantasy.Abstractions.Net.IConnection Connection => _inner.Connection;
        public ClientProfile Profile => _inner.Profile;
        public ConcurrentDictionary<string, object> Items => _inner.Items;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            _inner.SendAsync(opcode, payload, ct);
    }
}
