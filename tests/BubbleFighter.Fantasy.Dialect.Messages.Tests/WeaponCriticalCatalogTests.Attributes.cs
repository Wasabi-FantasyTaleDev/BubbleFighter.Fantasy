using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class WeaponCriticalCatalogTests
{
    /// <summary>Ordinary critical attributes apply to both slots and do not acquire a weapon's mode replacement.</summary>
    [Theory]
    [InlineData(1, 0.06)]
    [InlineData(16, 0.26)]
    [InlineData(101, 0.26)]
    public void OrdinaryCatalogAttributeIsIndependentOfTheWeaponReplacement(int mode, double primary)
    {
        var store = Fixture();
        AddEffect(store, 100051, "4");
        var actual = Load(store).Contributions(new(mode, new[] { 300001, 203103, 201001, 100051 }));
        Assert.Equal(primary, actual.Primary, 6);
        Assert.Equal(0.06, actual.Sidearm, 6);
        Assert.Equal(0.22, Load(store).Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>The character applies first, then native item ordering; assignment effects never become a sum.</summary>
    [Fact]
    public void OrdinaryAttributesFollowCharacterThenOrderedWornAssignments()
    {
        var store = Fixture();
        AddEffect(store, 190044, "1");
        AddEffect(store, 100051, "4");
        AddEffect(store, 300001, "8");
        AddEffect(store, 190044, "3");
        var catalog = Load(store);
        var actual = catalog.Contributions(new(1, new[] { 190044, 300001, 100051, 201001, 203103, 190044 }));
        Assert.Equal(0.05, actual.Primary, 6);
        Assert.Equal(0.05, actual.Sidearm, 6);
        Assert.Equal(0.10, catalog.Contributions(new(1, new[] { 300001, 203103, 201001 })).Primary, 6);
    }

    /// <summary>Catalog scope selectors need authoritative inputs before they can affect a seed probability.</summary>
    [Theory]
    [InlineData("10", "0")]
    [InlineData("0", "1")]
    public void UnsupportedOrdinaryAttributeSelectorsFailAtLoad(string channel, string group)
    {
        var store = Fixture();
        AddEffect(store, 100051, "4", channel, group);
        Assert.Throws<ConfigException>(() => Load(store));
    }

    /// <summary>A selected catalog attribute must carry an explicit finite value.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidOrdinaryAttributeValuesFailAtLoad(string amount)
    {
        var store = Fixture();
        AddEffect(store, 100051, amount);
        Assert.Throws<ConfigException>(() => Load(store));
    }

    /// <summary>A timed amount remains inactive at round start, independently of a worn ordinary attribute.</summary>
    [Fact]
    public void TimedCatalogEffectsAreNotUnconditionalOrdinaryAttributes()
    {
        var store = Fixture();
        AddEffect(store, 100051, "4");
        AddEffect(store, 100216, "40", name: "AMcriFactorIncAmount");
        var actual = Load(store).Contributions(new(16, new[] { 203103, 201001, 100051, 100216 }));
        Assert.Equal(0.26, actual.Primary, 6);
        Assert.Equal(0.06, actual.Sidearm, 6);
    }

    /// <summary>Missing timed rows retain the client's five-piece threshold and inactive zero duration.</summary>
    [Fact]
    public void TimedAttributesStartFromTheNativeResetValues()
    {
        var actual = Load().Attributes(new(1, new[] { 300001, 203103, 201001 }));
        Assert.Equal(new WeaponCriticalCatalog.EquipmentAttributes(0, 5, 0, 0, 0), actual);
        Assert.False(actual.TimedEligible);
    }

    /// <summary>The native threshold assigns, equipment values truncate before addition, and duration truncates each running sum.</summary>
    [Fact]
    public void TimedAttributeArithmeticPreservesTheNativeEffectOrderAndWidths()
    {
        var store = Fixture();
        AddEffect(store, 300001, "9", name: "AMequipCheckValue");
        AddEffect(store, 100216, "4.9", name: "AMequipCheckValue");
        AddEffect(store, 100213, "2.9", name: "AMequipValue");
        AddEffect(store, 100214, "2.9", name: "AMequipValue");
        AddEffect(store, 100216, "3.75", name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, "-1.5", name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, "0.75", name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, "40", name: "AMcriFactorIncAmount");
        AddEffect(store, 100216, "-2.5", name: "AMcriFactorIncAmount");
        var actual = Load(store).Attributes(new(16, new[] { 100216, 100214, 300001, 100213, 100213 }));
        Assert.Equal(new WeaponCriticalCatalog.EquipmentAttributes(0, 4, 4, 1, 37.5f), actual);
        Assert.True(actual.TimedEligible);
    }

    /// <summary>Eligibility requires the complete equipped set and positive accumulated duration.</summary>
    [Theory]
    [InlineData(4, "5000", false)]
    [InlineData(5, "0", false)]
    [InlineData(5, "5000", true)]
    public void TimedEligibilityUsesTheResolvedEquipmentTotalAndDuration(int count, string duration, bool expected)
    {
        var store = Fixture();
        AddEffect(store, 100216, duration, name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, count.ToString(System.Globalization.CultureInfo.InvariantCulture), name: "AMequipValue");
        Assert.Equal(expected, Load(store).Attributes(new(1, new[] { 100216 })).TimedEligible);
    }

    /// <summary>Timed effect rows require the same explicit finite values and modeled scope as ordinary rows.</summary>
    [Theory]
    [InlineData("AMequipCheckValue", "2147483648", "0", "0")]
    [InlineData("AMequipValue", "NaN", "0", "0")]
    [InlineData("AMcriFactorIncDuration", "5000", "1", "0")]
    [InlineData("AMcriFactorIncAmount", "40", "0", "1")]
    [InlineData("AMcriFactorIncAmount", "", "0", "0")]
    public void UnsupportedTimedAttributesFailAtLoad(string name, string amount, string channel, string group)
    {
        var store = Fixture();
        AddEffect(store, 100216, amount, channel, group, name);
        Assert.Throws<ConfigException>(() => Load(store));
    }

    /// <summary>Attribute resolution starts afresh after equipment changes and never retains a preceding set's timer inputs.</summary>
    [Fact]
    public void RemovingTheSetRestoresTheNativeTimedResetValues()
    {
        var store = Fixture();
        AddEffect(store, 100216, "5", name: "AMequipValue");
        AddEffect(store, 100216, "5000", name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, "40", name: "AMcriFactorIncAmount");
        var catalog = Load(store);
        Assert.True(catalog.Attributes(new(1, new[] { 100216 })).TimedEligible);
        Assert.Equal(new WeaponCriticalCatalog.EquipmentAttributes(0, 5, 0, 0, 0),
            catalog.Attributes(new(1, Array.Empty<int>())));
    }

    /// <summary>A running native timer uses the current amount even after the set no longer qualifies for another activation.</summary>
    [Fact]
    public void RunningTimedEffectReadsCurrentAmountWithoutRecheckingItsActivationThreshold()
    {
        var store = Fixture();
        AddEffect(store, 100216, "5000", name: "AMcriFactorIncDuration");
        AddEffect(store, 100216, "40", name: "AMcriFactorIncAmount");
        AddEffect(store, 100213, "5", name: "AMequipValue");
        var catalog = Load(store);
        var complete = new MatchSeedEquipment(1, new[] { 203103, 201001, 100216, 100213 });
        var incomplete = new MatchSeedEquipment(1, new[] { 203103, 201001, 100216 });
        Assert.True(catalog.Attributes(complete).TimedEligible);
        Assert.False(catalog.Attributes(incomplete).TimedEligible);
        Assert.Equal(catalog.Contributions(complete, timedEffectActive: true),
            catalog.Contributions(incomplete, timedEffectActive: true));
        Assert.Equal(0.42, catalog.Contributions(incomplete, timedEffectActive: true).Sidearm, 6);
        Assert.Equal(0.02, catalog.Contributions(incomplete).Sidearm, 6);
        Assert.Equal(0.02, catalog.Contributions(new(1, new[] { 203103, 201001 }), timedEffectActive: true).Sidearm, 6);
    }

    internal static void AddEffect(FixtureStore store, int item, string amount, string channel = "0", string group = "0",
        string name = "criFactorIncAmount") => Edit(store, "Config/ShopItem2.xml", document =>
        document.Descendants(Spreadsheet + "Worksheet").Single(sheet =>
            (string?)sheet.Attribute(Spreadsheet + "Name") == "ShopItemSpec").Element(Spreadsheet + "Table")!.Add(
                new XElement(Spreadsheet + "Row", new[] { item.ToString(System.Globalization.CultureInfo.InvariantCulture), name, amount, channel, group }
                    .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))))));
}
