using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Validates native helper ordering, explicit map policy, resource bounds and disjoint object identities.</summary>
public sealed class ExtremeRacingMapDataTests
{
    private const string Options = "[Cannon]\nPairCount=2\n[Coin]\nPairCount=2\n[SavePoint]\nStageCount=3\n"
        + "[GrenadeStoneArea]\nProcessStage=2\nDisappearCount=1\n";
    private static readonly ExtremeRacingMapPolicy Policy = new(100, 200, 300, 0, [3, 1]);
    private static readonly ExtremeRacingMapLimits Limits = new(4096, 100, 100);
    private static readonly int[] Actors = Enumerable.Range(0, 16).Select(slot => 1_000_000 + 100_000 * slot).ToArray();

    /// <summary>Native numeric lookup, rather than WB order, pairs the complete coin-round policy with each position.</summary>
    [Fact]
    public void ShuffledHelpersUseNumericSuffixOrderAndBoundedReads()
    {
        var store = Store();
        var map = ExtremeRacingMapData.Load(store, "race/nested", Policy, Limits, Actors);
        Assert.Equal(3, map.StageCount);
        Assert.Equal(2, map.CannonPairCount);
        Assert.Equal(new[] { new Vector3(10, 20, 30), new Vector3(40, 50, 60) }, map.CoinPositions);
        Assert.Equal(new uint[] { 200, 201 }, map.Initialization.Coins.Select(coin => coin.Identity));
        Assert.Equal(new[] { 3, 1 }, map.Initialization.Coins.Select(coin => coin.Round));
        Assert.All(map.Initialization.Coins, coin => Assert.Equal(0, coin.Variant));
        Assert.Equal(1, map.Initialization.StoneDisposerCount);
        Assert.Equal(new[] { "Maps/race/nested/option.ini", "Maps/race/nested/track.wb" }, store.Reads.Select(read => read.Path));
        Assert.All(store.Reads, read => Assert.Equal(Limits.MaximumFileBytes, read.Limit));
    }

    /// <summary>Native coin-count fallback still requires a full explicit round policy and complete helper sequence.</summary>
    [Fact]
    public void AbsentCoinCountUsesNativeCountWithoutInventingRounds()
    {
        var helpers = Enumerable.Range(0, 33).Select(index => ($"#eff_coin{index:D2}", new Vector3(index, 0, 0))).ToArray();
        var options = "[Cannon]\nPairCount=0\n[SavePoint]\nStageCount=5\n[GrenadeStoneArea]\nProcessStage=0\n";
        var store = new MemoryStore(Encoding.ASCII.GetBytes(options), WorldBlockFixture.Create(helpers));
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.Load(store, "race/nested", Policy, Limits, Actors));
        var policy = Policy with { CoinRounds = Enumerable.Range(0, 33).Select(index => index % 5 + 1).ToArray() };
        var map = ExtremeRacingMapData.Load(store, "race/nested", policy, Limits, Actors);
        Assert.Equal(33, map.Initialization.Coins.Count);
        Assert.Equal(0, map.Initialization.StoneDisposerCount);
    }

    /// <summary>Missing halves and gaps cannot silently reduce native object counts or shift identity assignment.</summary>
    [Theory]
    [InlineData("#eff_cannon_in00")]
    [InlineData("#eff_cannon_out01")]
    [InlineData("#eff_coin00")]
    [InlineData("stone_disappear00")]
    public void IncompleteNativeHelperSetsFail(string missing)
    {
        var helpers = Helpers().Where(helper => helper.Name != missing).ToArray();
        Assert.Throws<InvalidDataException>(() => Load(Store(helpers: helpers)));
    }

    /// <summary>Duplicate, out-of-range and noncanonical helper suffixes are rejected before any initialization returns.</summary>
    [Theory]
    [InlineData("#eff_coin00")]
    [InlineData("#eff_coin02")]
    [InlineData("#eff_coin0")]
    [InlineData("#eff_coin000")]
    [InlineData("#eff_cannon_outbad")]
    public void AmbiguousOrUnexpectedHelperNamesFail(string extra)
    {
        Assert.Throws<InvalidDataException>(() => Load(Store(helpers: [.. Helpers(), (extra, Vector3.Zero)])));
    }

    /// <summary>Every coin requires an explicit supported variant and a round within the map's native stage range.</summary>
    [Fact]
    public void IncompleteOrUnsupportedCoinPoliciesFail()
    {
        foreach (var policy in new[]
        {
            Policy with { CoinRounds = [] }, Policy with { CoinRounds = [1] }, Policy with { CoinRounds = [0, 1] },
            Policy with { CoinRounds = [1, 4] }, Policy with { CoinVariant = 1 },
        })
            Assert.Throws<InvalidDataException>(() => Load(Store(), policy));
    }

    /// <summary>Both IDs of every cannon pair participate in overlap, zero, overflow and actor-namespace checks.</summary>
    [Fact]
    public void ObjectRangesMustBeDisjointAndBelowAllActors()
    {
        foreach (var policy in new[]
        {
            Policy with { FirstCannonIdentity = 0 }, Policy with { FirstCannonIdentity = 199 },
            Policy with { FirstCannonIdentity = uint.MaxValue - 1 }, Policy with { FirstCannonIdentity = 999_999 },
            Policy with { FirstCoinIdentity = 300 }, Policy with { FirstStoneDisposerIdentity = 2_000_001 },
        })
            Assert.Throws<InvalidDataException>(() => Load(Store(), policy));
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.Load(Store(), "race/nested", Policy, Limits, []));
    }

    /// <summary>Invalid native count and stage fields never become defaults.</summary>
    [Theory]
    [InlineData("[Coin]\nPairCount=2", "[Coin]\nPairCount=-1")]
    [InlineData("[Coin]\nPairCount=2", "[Coin]\nPairCount=two")]
    [InlineData("StageCount=3", "StageCount=0")]
    [InlineData("ProcessStage=2", "ProcessStage=4")]
    [InlineData("ProcessStage=2\n", "")]
    public void InvalidOptionFieldsFail(string original, string replacement)
    {
        Assert.Throws<InvalidDataException>(() => Load(Store(options: Options.Replace(original, replacement, StringComparison.Ordinal))));
    }

    /// <summary>Duplicate keys and broken section headers fail instead of choosing one interpretation.</summary>
    [Theory]
    [InlineData("[Coin]\nPairCount=2\n")]
    [InlineData("[Coin\nPairCount=2\n")]
    public void AmbiguousOptionStructureFails(string invalid)
    {
        Assert.Throws<InvalidDataException>(() => Load(Store(options: Options + invalid)));
    }

    /// <summary>ASCII keys are read through native legacy comment bytes and an optional UTF-8 BOM.</summary>
    [Fact]
    public void NativeCommentEncodingDoesNotChangeCounts()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes(Options))
            .Concat(new byte[] { (byte)';', 0xB0, 0xA1, (byte)'\n' }).ToArray();
        var map = Load(new MemoryStore(bytes, WorldBlockFixture.Create(Helpers())));
        Assert.Equal(3, map.StageCount);
    }

    /// <summary>Resource, helper and total-object limits fail before a usable initialization can escape.</summary>
    [Fact]
    public void EveryAllocationBoundIsEnforced()
    {
        Assert.Throws<InvalidDataException>(() => Load(Store(), limits: Limits with { MaximumFileBytes = 8 }));
        Assert.Throws<InvalidDataException>(() => Load(Store(), limits: Limits with { MaximumHelpers = 3 }));
        Assert.Throws<InvalidDataException>(() => Load(Store(), limits: Limits with { MaximumObjects = 6 }));
        var malformed = new MemoryStore(Encoding.ASCII.GetBytes(Options), WorldBlockFixture.Create(Helpers())[..^1]);
        Assert.Throws<InvalidDataException>(() => Load(malformed));
        var excessivePolicy = Policy with { CoinRounds = new int[4094] };
        Assert.Throws<InvalidDataException>(() => Load(Store(), excessivePolicy, Limits with { MaximumObjects = 10000 }));
    }

    /// <summary>A configured map cannot escape its virtual Maps directory.</summary>
    [Theory]
    [InlineData("../race")]
    [InlineData("race/../other")]
    [InlineData("race\\other")]
    [InlineData("C:/race")]
    [InlineData("/race")]
    public void InvalidVirtualMapPathsFailBeforeRead(string path)
    {
        var store = Store();
        Assert.Throws<InvalidDataException>(() => ExtremeRacingMapData.Load(store, path, Policy, Limits, Actors));
        Assert.Empty(store.Reads);
    }

    private static ExtremeRacingMapData Load(MemoryStore store, ExtremeRacingMapPolicy? policy = null,
        ExtremeRacingMapLimits? limits = null) => ExtremeRacingMapData.Load(store, "race/nested", policy ?? Policy, limits ?? Limits, Actors);

    private static MemoryStore Store(string? options = null, (string Name, Vector3 Position)[]? helpers = null) =>
        new(Encoding.ASCII.GetBytes(options ?? Options), WorldBlockFixture.Create(helpers ?? Helpers()));

    internal static IClientDataStore ForStage(string stageDirectory, bool malformed = false, byte[]? cannons = null) =>
        new MemoryStore(Encoding.ASCII.GetBytes(Options),
            WorldBlockFixture.Create(malformed ? Helpers().Where(helper => helper.Name != "#eff_coin00").ToArray() : Helpers()), stageDirectory, cannons);

    private static (string Name, Vector3 Position)[] Helpers() =>
    [
        ("#eff_coin01", new(40, 50, 60)), ("#eff_cannon_out01", Vector3.Zero), ("stone_disappear00", Vector3.Zero),
        ("#eff_cannon_in00", Vector3.Zero), ("#eff_coin00", new(10, 20, 30)), ("#eff_cannon_in01", Vector3.Zero),
        ("#eff_cannon_out00", Vector3.Zero), ("#eff_rollingStone_out00", Vector3.Zero),
    ];

    private sealed class MemoryStore(byte[] options, byte[] worldBlock, string stageDirectory = "race/nested", byte[]? cannons = null) : IClientDataStore
    {
        internal List<(string Path, int Limit)> Reads { get; } = [];
        public bool Exists(string path) => path == $"Maps/{stageDirectory}/option.ini" || path == $"Maps/{stageDirectory}/track.wb" || cannons is not null && path == $"Maps/{stageDirectory}/cannon.ini";
        public byte[] Read(string path) => throw new InvalidOperationException("The racing loader must use bounded reads.");
        public byte[] Read(string path, int maximumBytes)
        {
            Reads.Add((path, maximumBytes));
            var bytes = path switch
            {
                var option when option == $"Maps/{stageDirectory}/option.ini" => options,
                var track when track == $"Maps/{stageDirectory}/track.wb" => worldBlock,
                var cannon when cannons is not null && cannon == $"Maps/{stageDirectory}/cannon.ini" => cannons,
                _ => throw new FileNotFoundException(path),
            };
            return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException("Synthetic resource exceeds its limit.");
        }
    }
}
