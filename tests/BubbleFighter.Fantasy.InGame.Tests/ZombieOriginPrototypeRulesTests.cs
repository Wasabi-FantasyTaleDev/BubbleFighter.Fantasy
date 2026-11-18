using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The server validates the report against the operator's installed client weights and option.</summary>
public sealed class ZombieOriginPrototypeRulesTests
{
    /// <summary>Weights take native order and scale over all 10000 draws; the equipped queen override is optional.</summary>
    [Fact]
    public void InstalledWeightsAndForceItemDetermineTheReport()
    {
        var rules = ZombieOriginPrototypeRules.Load(new RulesStore("zombieStein: 2, zombieKing: 1, succubusQueen: 1"));
        Assert.Equal(new int?[] { 201, 201, 203, 203, 212, 212 }, new[] { 0, 2499, 2500, 4999, 5000, 9999 }.Select(n => rules.Choose(n, false)));
        Assert.Equal(203, rules.Choose(0, true));
        Assert.Equal(201, (rules with { IgnoreForceItem = true }).Choose(0, true));
        var noQueen = ZombieOriginPrototypeRules.Load(new RulesStore("zombieKing: 1"));
        Assert.Equal(201, noQueen.Choose(8500, true));
        var zero = ZombieOriginPrototypeRules.Load(new RulesStore("zombieKing: 0"));
        Assert.Null(zero.Choose(7500, false));
    }

    /// <summary>Reject an unproved fourth kind, negative weights and totals beyond the client's precise range.</summary>
    [Theory]
    [InlineData("newRole: 4")]
    [InlineData("zombieKing: -7")]
    [InlineData("zombieKing: 500000")]
    public void UnsupportedWeightTablesFailClosed(string weights) =>
        Assert.Throws<InvalidDataException>(() => ZombieOriginPrototypeRules.Load(new RulesStore(weights)));

    private sealed class RulesStore(string weights) : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/GameSpec/Modes/ZombieOrigin.ZombieRule.yaml";
        public byte[] Read(string path) => Exists(path) ? Encoding.UTF8.GetBytes($"protoSelect:\n  randomWeights: {{ {weights} }}\n  ignoreForceItem: false\n")
            : throw new FileNotFoundException(path);
    }
}
