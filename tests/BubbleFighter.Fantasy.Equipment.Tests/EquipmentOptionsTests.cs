using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Equipment;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>Pins the validated row configuration of the equipment plugin.</summary>
public sealed class EquipmentOptionsTests
{
    [Fact]
    public void ValidateAcceptsDialect1256()
    {
        var options = new EquipmentOptions { VersionMajor = 1256 };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidateAcceptsEveryMountedBuild()
    {
        // The row is build-neutral: the mounted codec bindings decide which wire
        // surface exists, so every served major version is a legal configuration.
        Assert.All(new[] { 1, 324, 717, 812, 1256 }, major =>
            Assert.Empty(new EquipmentOptions { VersionMajor = major }.Validate()));
    }

    [Fact]
    public void ValidateRejectsAnEmptyProtocol()
    {
        var failures = new EquipmentOptions { Protocol = "" }.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(EquipmentOptions.Protocol)));
    }

    [Fact]
    public void DataAnnotationsRequireProtocolAndProfile()
    {
        var options = new EquipmentOptions { Protocol = "", ClientProfile = "" };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Assert.False(ok);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void EnabledDefaultsToTrue()
    {
        Assert.True(new EquipmentOptions().Enabled);
    }

    [Fact]
    public void PersistWornStateDefaultsToTrue()
    {
        Assert.True(new EquipmentOptions().PersistWornState);
    }

    [Fact]
    public void MaxEquippedPerSessionHasABoundedDefault()
    {
        var options = new EquipmentOptions();
        Assert.Equal(256, options.MaxEquippedPerSession);
        Assert.Empty(options.Validate());

        var results = new List<ValidationResult>();
        var zero = new EquipmentOptions { MaxEquippedPerSession = 0 };
        Assert.False(Validator.TryValidateObject(zero, new ValidationContext(zero), results, validateAllProperties: true));
        Assert.NotEmpty(results);
    }

    [Fact]
    public void MaxTrackedConnectionsHasABoundedDefault()
    {
        var options = new EquipmentOptions();
        Assert.Equal(1024, options.MaxTrackedConnections);

        var results = new List<ValidationResult>();
        var zero = new EquipmentOptions { MaxTrackedConnections = 0 };
        Assert.False(Validator.TryValidateObject(zero, new ValidationContext(zero), results, validateAllProperties: true));
        Assert.NotEmpty(results);
    }
}
