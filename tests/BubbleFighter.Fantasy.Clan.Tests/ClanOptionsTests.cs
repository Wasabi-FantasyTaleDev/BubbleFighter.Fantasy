using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Clan;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

public sealed class ClanOptionsTests
{
    [Fact]
    public void DataAnnotationsRequireProtocol()
    {
        var options = new ClanOptions { Protocol = "" };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Assert.False(ok);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ClanOptions.Protocol)));
    }

    /// <summary>The build-neutral row keeps the kr-1256 game protocol as its default; that is its one build reference.</summary>
    [Fact]
    public void ProtocolDefaultsToTheKr1256GameConnection()
    {
        Assert.Equal("kr-1256/game", new ClanOptions().Protocol);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void SnapshotLimitRejectsInvalidBounds(int limit)
    {
        var options = new ClanOptions { MaxSnapshotEntries = limit };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ClanOptions.MaxSnapshotEntries)));
    }

    [Fact]
    public void MaxTrackedOpensHasABoundedDefault()
    {
        Assert.Equal(512, new ClanOptions().MaxTrackedOpens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public void MaxTrackedOpensRejectsInvalidBounds(int limit)
    {
        var options = new ClanOptions { MaxTrackedOpens = limit };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ClanOptions.MaxTrackedOpens)));
    }
}
