using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Validates the racing cannon data loader and the bounded outstanding-command ledger built on it.</summary>
public sealed class ExtremeRacingCannonScheduleTests
{
    internal const string TwoCannons = "[Count]\r\nCANNONCOUNT=2\r\n\r\n[Cannon0]\r\nWEAPONCOUNT = 1\r\nINTERVAL=3000\r\nSTAGE=2\r\n\r\n"
        + "[Cannon0_Weapon0]\r\nWEAPONID=50004\r\nCOUNT=1\r\nSCALE = 1.0\r\n\r\n"
        + "[Cannon1]\r\nWEAPONCOUNT = 2\r\nINTERVAL=800\r\nSTAGE=3\r\n\r\n"
        + "[Cannon1_Weapon0]\r\nWEAPONID=50007\r\nCOUNT=1\r\nSCALE=1.0\r\n[Cannon1_Weapon1]\r\nWEAPONID=50002\r\nCOUNT=3\r\nSCALE=0.5\r\n";

    /// <summary>Every cannon, weapon, interval, stage and scale is read in cannon number order.</summary>
    [Fact]
    public void ParsesEveryCannonAndWeapon()
    {
        var schedule = Parse(TwoCannons);
        Assert.Equal(2, schedule.Cannons.Count);
        Assert.Equal(3000, schedule.Cannons[0].IntervalMs);
        Assert.Equal(2, schedule.Cannons[0].Stage);
        Assert.Equal(new ExtremeRacingCannonWeapon(50004, 1, 1.0f), Assert.Single(schedule.Cannons[0].Weapons));
        Assert.Equal(new[] { 50007, 50002 }, schedule.Cannons[1].Weapons.Select(weapon => weapon.WeaponId));
        Assert.Equal(0.5f, schedule.Cannons[1].Weapons[1].Scale);
        Assert.True(schedule.Lists(1, 50002));
        Assert.False(schedule.Lists(0, 50002));
        Assert.False(schedule.Lists(2, 50004));
        Assert.False(schedule.Lists(-1, 50004));
    }

    /// <summary>A byte order mark, legacy comment bytes and case differences do not change what is read.</summary>
    [Fact]
    public void ToleratesBomCommentsAndSectionCase()
    {
        var text = TwoCannons.Replace("[Count]", "[COUNT] ;" + "\u00B0\u00A1", StringComparison.Ordinal);
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.Latin1.GetBytes(text)).ToArray();
        Assert.Equal(2, ExtremeRacingCannonSchedule.Parse(bytes, 4096, 2, 3, 100).Cannons.Count);
    }

    /// <summary>The cannon count must equal the initialized pair count in both directions.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]
    public void CountMustMatchTheInitializedCannons(int initialized)
    {
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons, initialized));
    }

    /// <summary>Malformed and out-of-range data fails and never becomes a default.</summary>
    [Theory]
    [InlineData("CANNONCOUNT=2", "CANNONCOUNT=two")]
    [InlineData("CANNONCOUNT=2", "CANNONCOUNT=-2")]
    [InlineData("CANNONCOUNT=2", "")]
    [InlineData("WEAPONID=50004", "WEAPONID=49999")]
    [InlineData("WEAPONID=50004", "WEAPONID=50009")]
    [InlineData("WEAPONID=50004", "WEAPONID=0x50004")]
    [InlineData("INTERVAL=3000", "INTERVAL=0")]
    [InlineData("INTERVAL=3000", "INTERVAL=fast")]
    [InlineData("STAGE=2", "STAGE=0")]
    [InlineData("STAGE=2", "STAGE=4")]
    [InlineData("WEAPONCOUNT = 1", "WEAPONCOUNT = 0")]
    [InlineData("WEAPONCOUNT = 1", "WEAPONCOUNT = 2")]
    [InlineData("SCALE = 1.0", "SCALE = 0")]
    [InlineData("SCALE = 1.0", "SCALE = NaN")]
    [InlineData("SCALE = 1.0", "SCALE = Infinity")]
    [InlineData("SCALE = 1.0", "SCALE = big")]
    [InlineData("COUNT=1\r\nSCALE = 1.0", "SCALE = 1.0")]
    [InlineData("[Cannon1]", "[Cannon1")]
    [InlineData("[Cannon1_Weapon1]", "[Cannon1_Weapon3]")]
    [InlineData("INTERVAL=800", "INTERVAL=800\r\nINTERVAL=900")]
    public void MalformedDataFails(string original, string replacement)
    {
        Assert.Contains(original, TwoCannons, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons.Replace(original, replacement, StringComparison.Ordinal)));
    }

    /// <summary>A repeated section and a cannon section beyond the declared count are refused.</summary>
    [Fact]
    public void DuplicateAndUndeclaredSectionsFail()
    {
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons + "[Cannon0]\r\nWEAPONCOUNT=1\r\n"));
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons + "[Cannon5]\r\nWEAPONCOUNT=1\r\n"));
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons + "[Cannon0_Weapon1]\r\nWEAPONID=50002\r\n"));
        Assert.Throws<InvalidDataException>(() => Parse("CANNONCOUNT=2\r\n"));
        Assert.Throws<InvalidDataException>(() => Parse(TwoCannons.Replace("[Count]", "[Other]", StringComparison.Ordinal)));
    }

    /// <summary>The file size and the object bounds apply before and while parsing.</summary>
    [Fact]
    public void EveryBoundIsEnforced()
    {
        var bytes = Encoding.ASCII.GetBytes(TwoCannons);
        Assert.Throws<InvalidDataException>(() => ExtremeRacingCannonSchedule.Parse(bytes, bytes.Length - 1, 2, 3, 100));
        Assert.Throws<InvalidDataException>(() => ExtremeRacingCannonSchedule.Parse(bytes, 4096, 2, 3, 1));
        Assert.Throws<InvalidDataException>(() => ExtremeRacingCannonSchedule.Parse(bytes, 4096, 2, 2, 100));
    }

    /// <summary>The loader reads one bounded file under the checked map directory and rejects traversal before any read.</summary>
    [Fact]
    public void LoadReadsOnlyTheMapsCannonFile()
    {
        var store = new Store(Encoding.ASCII.GetBytes(TwoCannons));
        Assert.Equal(2, ExtremeRacingCannonSchedule.Load(store, "race/nested", 2, 3, 4096, 100).Cannons.Count);
        Assert.Equal([("Maps/race/nested/cannon.ini", 4096)], store.Reads);
        foreach (var path in new[] { "../race", "race/../other", "race\\other", "C:/race", "/race", "" })
            Assert.Throws<InvalidDataException>(() => ExtremeRacingCannonSchedule.Load(store, path, 2, 3, 4096, 100));
        Assert.Single(store.Reads);
    }

    /// <summary>A command is accepted once per cannon pair, only for a listed weapon, and answered once by an exact match.</summary>
    [Fact]
    public void LedgerHoldsOneCommandPerPairAndConsumesItOnce()
    {
        var ledger = new ExtremeRacingCannonLedger(Parse(TwoCannons), 100);
        Assert.Equal(2, ledger.Capacity);
        Assert.True(ledger.IsInitialized(100));
        Assert.True(ledger.IsInitialized(103));
        Assert.False(ledger.IsInitialized(99));
        Assert.False(ledger.IsInitialized(104));
        Assert.False(ledger.IsInitialized(uint.MaxValue));
        Assert.False(ledger.TryIssue(104, 50004));
        Assert.False(ledger.TryIssue(100, 50007));
        Assert.True(ledger.TryIssue(100, 50004));
        Assert.False(ledger.TryIssue(101, 50004));
        Assert.True(ledger.TryIssue(103, 50002));
        Assert.Equal(ledger.Capacity, ledger.Outstanding);
        Assert.False(ledger.TryConsume(101, 50004));
        Assert.False(ledger.TryConsume(100, 50007));
        Assert.False(ledger.TryConsume(102, 50007));
        Assert.True(ledger.TryConsume(100, 50004));
        Assert.False(ledger.TryConsume(100, 50004));
        Assert.Equal(1, ledger.Outstanding);
        ledger.Revoke();
        Assert.False(ledger.TryConsume(103, 50002));
        Assert.Equal(0, ledger.Outstanding);
    }

    /// <summary>The real kr-1256 rel_Racing02_Extreme data loads and agrees with its option.ini cannon pair count.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void RealRacing02DataLoads()
    {
        var store = new RealStore("kr-1256");
        var schedule = ExtremeRacingCannonSchedule.Load(store, "rel_Racing02_Extreme", 24, 5, 8_388_608, 1024);
        Assert.Equal(24, schedule.Cannons.Count);
        Assert.All(schedule.Cannons, cannon =>
        {
            Assert.InRange(cannon.IntervalMs, 1, 10_000);
            Assert.Equal(4, cannon.Stage);
            var weapon = Assert.Single(cannon.Weapons);
            Assert.InRange(weapon.WeaponId, ExtremeRacingCannonSchedule.FirstWeapon, ExtremeRacingCannonSchedule.LastWeapon);
            Assert.Equal(1.0f, weapon.Scale);
        });
        var option = Encoding.Latin1.GetString(LocalClientData.Bytes("kr-1256", "Maps/rel_Racing02_Extreme/option.ini"));
        Assert.Contains("PairCount=24", option, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ExtremeRacingCannonSchedule.Load(store, "rel_Racing02_Extreme", 9, 5, 8_388_608, 1024));
    }

    /// <summary>The real Racing01 file declares no cannons while its option.ini initializes nine: the map schedules none.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void RealRacing01SchedulesNoCannons()
    {
        var store = new RealStore("kr-1256");
        var schedule = ExtremeRacingCannonSchedule.Load(store, "rel_Racing01_Extreme", 9, 5, 8_388_608, 1024);
        Assert.Empty(schedule.Cannons);
        Assert.False(schedule.Lists(0, ExtremeRacingCannonSchedule.FirstWeapon));
        Assert.Empty(ExtremeRacingCannonSchedule.Load(store, "rel_Racing01_Extreme", 0, 5, 8_388_608, 1024).Cannons);
    }

    /// <summary>A declared count of zero is a map without scheduled cannons, whatever the initialized pair count.</summary>
    [Fact]
    public void ZeroDeclaredCannonsScheduleNone()
    {
        Assert.Empty(Parse("[Count]\r\nCANNONCOUNT=0\r\n", 9).Cannons);
        Assert.Throws<InvalidDataException>(() => Parse("[Count]\r\nCANNONCOUNT=0\r\n" + "[Cannon0]\r\nWEAPONCOUNT=1\r\n", 9));
    }

    private static ExtremeRacingCannonSchedule Parse(string text, int initialized = 2) =>
        ExtremeRacingCannonSchedule.Parse(Encoding.Latin1.GetBytes(text), 4096, initialized, 3, 100);

    private sealed class Store(byte[] cannons) : IClientDataStore
    {
        internal List<(string Path, int Limit)> Reads { get; } = [];
        public bool Exists(string path) => true;
        public byte[] Read(string path) => throw new InvalidOperationException("The cannon loader must use bounded reads.");
        public byte[] Read(string path, int maximumBytes)
        {
            Reads.Add((path, maximumBytes));
            return cannons;
        }
    }

    private sealed class RealStore(string build) : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path) => LocalClientData.Bytes(build, path);
    }
}
