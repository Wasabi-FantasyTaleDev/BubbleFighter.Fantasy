using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.InventoryView;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>Pins the validated row configuration of the inventory view plugin.</summary>
public sealed class InventoryViewOptionsTests
{
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
}
