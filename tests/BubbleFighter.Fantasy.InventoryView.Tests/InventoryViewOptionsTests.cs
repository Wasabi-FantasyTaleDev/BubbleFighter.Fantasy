using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.InventoryView;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>Pins the validated row configuration of the inventory view plugin.</summary>
public sealed class InventoryViewOptionsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PersistedPagesRequireAnEnabledViewPublisher(bool enabled, bool publish)
    {
        var options = new InventoryViewOptions
        {
            Enabled = enabled, PublishEquippedViewPush = publish, PersistPresetPages = true,
        };
        Assert.Contains(options.Validate(), result => result.ErrorMessage!.Contains("persist_preset_pages", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsDialect1256()
    {
        var options = new InventoryViewOptions { VersionMajor = 1256 };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidateAcceptsEveryMountedBuild()
    {
        // The row is build-neutral: the mounted view senders decide which wire
        // surface exists, so every served major version is a legal configuration.
        Assert.All(new[] { 1, 279, 812, 1256 }, major =>
            Assert.Empty(new InventoryViewOptions { VersionMajor = major }.Validate()));
    }

    [Fact]
    public void ValidateRejectsAnEmptyProtocol()
    {
        var failures = new InventoryViewOptions { Protocol = "" }.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(InventoryViewOptions.Protocol)));
    }

    [Fact]
    public void ViewRequestDefaultsToTheOnlyProvenRequestName()
    {
        // kr-1256's builder 0x80DA80 is the one proven view request; the legacy
        // builds send none and their rows leave the option empty.
        Assert.Equal("", new InventoryViewOptions().ViewRequest);
    }

    [Fact]
    public void DataAnnotationsRequireProtocolAndProfile()
    {
        var options = new InventoryViewOptions { Protocol = "", ClientProfile = "" };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Assert.False(ok);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void EnabledDefaultsToFalseSoTheRowStaysDormant()
    {
        Assert.False(new InventoryViewOptions().Enabled);
    }

    [Fact]
    public void VersionMajorDefaultsTo1256()
    {
        var options = new InventoryViewOptions();
        Assert.Equal(1256, options.VersionMajor);
        Assert.Empty(options.Validate());
    }

    /// <summary>The reviewed behaviour stays the default; the catalog-masks selection is opt-in until a live client confirms it.</summary>
    [Fact]
    public void SelectionDefaultsToIdRanges()
    {
        Assert.Equal("id_ranges", new InventoryViewOptions().Selection);
        Assert.Empty(new InventoryViewOptions().Validate());
    }

    [Fact]
    public void CatalogMasksRequiresExactlyOneWorkbookOfEachPair()
    {
        var none = new InventoryViewOptions { Selection = "catalog_masks" }.Validate().ToList();
        Assert.Contains(none, f => f.ErrorMessage!.Contains("item_locations_client_path or item_locations_path"));
        Assert.Contains(none, f => f.ErrorMessage!.Contains("fighter_config_client_path or fighter_config_path"));

        var itemOnly = new InventoryViewOptions { Selection = "catalog_masks", ItemLocationsPath = "shop.xml" }.Validate().ToList();
        Assert.DoesNotContain(itemOnly, f => f.ErrorMessage!.Contains("item_locations_client_path or item_locations_path"));
        Assert.Contains(itemOnly, f => f.ErrorMessage!.Contains("fighter_config_client_path or fighter_config_path"));

        var both = new InventoryViewOptions
        {
            Selection = "catalog_masks",
            ItemLocationsPath = "shop.xml",
            ItemLocationsClientPath = "Config/ShopItem2.xml",
            FighterConfigPath = "fighters.xml",
        }.Validate().ToList();
        Assert.Contains(both, f => f.ErrorMessage!.Contains("item_locations_client_path or item_locations_path"));

        var complete = new InventoryViewOptions
        {
            Selection = "catalog_masks",
            ItemLocationsClientPath = "Config/ShopItem2.xml",
            FighterConfigClientPath = "Config/FighterConfig.xml",
        }.Validate().ToList();
        Assert.Empty(complete);
    }

    [Fact]
    public void SelectionMustNameAKnownMode()
    {
        var options = new InventoryViewOptions { Selection = "slot_guess" };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, true);
        Assert.False(ok);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(InventoryViewOptions.Selection)));
    }
}
