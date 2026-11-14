using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Pins pet progression and earned skills to the native four-word ItemAttr representation.</summary>
public sealed class PetPresentationTests
{
    /// <summary>Projects the feeding date into the configured client civil clock while preserving persisted time and fullness.</summary>
    [Theory]
    [InlineData(-240, 46335u, 81250u)]
    [InlineData(0, 46335u, 97916u)]
    [InlineData(540, 46336u, 35416u)]
    public void FeedingTimestampUsesTheConfiguredClientCivilClock(int offset, uint day, uint fraction)
    {
        var state = PetTestData.State(2, 31) with { LastFedAt = PetTestData.Now };
        var slot = PetTestData.Slot(state);
        var options = PetTestData.Options(manual: true);
        options.ClientUtcOffsetMinutes = offset;
        var projected = Projection(options).Project(slot)!;
        var metadata = projected.Attributes.Single(attribute => attribute.Kind == PetCatalog.BaseSelector);
        Assert.Equal<uint>(new uint[] { 2, 31, day, fraction }, metadata.Values);
        Assert.Equal(0u, metadata.Unknown28);
        Assert.Equal("", metadata.Unknown32);
        var nativeDate = unchecked((int)metadata.Values[2]) + unchecked((int)metadata.Values[3]) / 100000.0;
        var clientTime = DateTime.FromOADate(nativeDate);
        var expected = PetTestData.Now.ToOffset(TimeSpan.FromMinutes(offset)).DateTime;
        Assert.InRange(Math.Abs((clientTime - expected).TotalSeconds), 0, 0.865);
        Assert.Equal(state.FullnessExpiresAt, projected.RemainingUntil);
        Assert.Equal(state, slot.Pet!.Find("kr-1256"));
    }

    /// <summary>Preserves all four earned-skill words, signed bit patterns and acquisition stages in the native attributes.</summary>
    [Fact]
    public void EarnedSkillsPreserveAllFourPositionsSignedBitsAndAcquisitionStages()
    {
        var state = PetTestData.State(2, 31);
        var projected = Projection(PetTestData.Options(manual: true)).Project(PetTestData.Slot(state))!;
        Assert.Equal(new uint[] { 7, 11, 21 }, projected.Attributes.Select(attribute => attribute.Kind));
        Assert.All(projected.Attributes, attribute =>
        {
            Assert.Equal(PetTestData.PetId, attribute.ItemId);
            Assert.Equal(4, attribute.Values.Count);
        });
        Assert.Equal<uint>(new uint[] { 4, 0, 0xFFFFFFFE, 0 }, projected.Attributes[1].Values);
        Assert.Equal<uint>(new uint[] { 8, 0, 0, 0 }, projected.Attributes[2].Values);
        Assert.Equal(1u, projected.Attributes[1].Unknown28);
        Assert.Equal(2u, projected.Attributes[2].Unknown28);
    }

    /// <summary>Keeps an unfed pet's date words zero even when the client civil clock has a positive offset.</summary>
    [Fact]
    public void NeverFedPetKeepsAnAbsentDateAtAnyClientOffset()
    {
        var options = PetTestData.Options(manual: true);
        options.ClientUtcOffsetMinutes = 840;
        var projected = Projection(options).Project(PetTestData.Slot(PetTestData.State(0, 5)))!;
        var metadata = Assert.Single(projected.Attributes);
        Assert.Equal<uint>(new uint[] { 0, 5, 0, 0 }, metadata.Values);
    }

    /// <summary>Projects each build's saved grade, experience, skills and fullness deadline without changing either saved state.</summary>
    [Fact]
    public void EachBuildProjectsItsOwnGradeExperienceSkillsAndTimer()
    {
        var legacy = PetTestData.State(1, 17) with { FullnessExpiresAt = PetTestData.Now.AddHours(3) };
        var modern = PetTestData.State(2, 29) with { FullnessExpiresAt = PetTestData.Now.AddHours(7) };
        var slot = PetTestData.Slot(legacy, "kr-812");
        slot = slot with { Pet = slot.Pet!.With("kr-1256", modern) };
        var legacyView = Projection(PetTestData.Options()).Project(slot)!;
        var modernView = Projection(PetTestData.Options(manual: true)).Project(slot)!;
        Assert.Equal<uint>(new uint[] { 1, 17, 0, 0 }, legacyView.Attributes[0].Values);
        Assert.Equal<uint>(new uint[] { 2, 29, 0, 0 }, modernView.Attributes[0].Values);
        Assert.Equal(2, legacyView.Attributes.Count);
        Assert.Equal(3, modernView.Attributes.Count);
        Assert.Equal(legacy.FullnessExpiresAt, legacyView.RemainingUntil);
        Assert.Equal(modern.FullnessExpiresAt, modernView.RemainingUntil);
        Assert.Equal(legacy, slot.Pet!.Find("kr-812"));
        Assert.Equal(modern, slot.Pet.Find("kr-1256"));
    }

    /// <summary>Rejects presentation for an uninitialized build instead of creating new progression or fullness.</summary>
    [Fact]
    public void UninitializedBuildCannotRegenerateFullnessDuringPresentation()
    {
        var other = PetTestData.State(2, 77) with { LastFedAt = PetTestData.Now };
        var slot = PetTestData.Slot(other, "kr-812");
        Assert.Throws<InvalidDataException>(() => Projection(PetTestData.Options(manual: true)).Project(slot));
        Assert.Null(slot.Pet!.Find("kr-1256"));
        Assert.Equal(other, slot.Pet.Find("kr-812"));
    }

    /// <summary>Returns no pet projection for food or unknown items so their owning features can present them.</summary>
    [Fact]
    public void NonPetItemsLeavePresentationToTheirOwningFeature()
    {
        var projection = Projection(PetTestData.Options(manual: true));
        var row = PetTestData.Slot(PetTestData.State(0, 0));
        Assert.Null(projection.Project(row with { ItemId = PetTestData.NormalFood }));
        Assert.Null(projection.Project(row with { ItemId = 999999 }));
    }

    /// <summary>Rejects unknown selectors and invalid saved buff values before emitting native attribute words.</summary>
    [Theory]
    [InlineData(999, 4, 0, -2, 0)]
    [InlineData(11, 5, 0, -2, 0)]
    [InlineData(11, 4, 1, -2, 0)]
    public void InvalidPersistedSkillsFailBeforePublishingUncheckedAttributeWords(int selector, int a, int b, int c, int d)
    {
        var state = PetTestData.State(1, 0) with { FirstSkill = new(selector, a, b, c, d) };
        Assert.Throws<InvalidDataException>(() => Projection(PetTestData.Options(manual: true))
            .Project(PetTestData.Slot(state)));
    }

    /// <summary>Uses fullness for the pet presentation timer while preserving the item's separate ownership expiry.</summary>
    [Fact]
    public void PresentationDoesNotReplaceOwnershipExpiryWithTheFullnessDeadline()
    {
        var state = PetTestData.State(1, 3) with { FullnessExpiresAt = PetTestData.Now.AddHours(2) };
        var lifetime = InventoryLifetime.Until(PetTestData.Now.AddDays(10));
        var slot = PetTestData.Slot(state) with { Lifetime = lifetime };
        var projected = Projection(PetTestData.Options(manual: true)).Project(slot)!;
        Assert.Equal(state.FullnessExpiresAt, projected.RemainingUntil);
        Assert.Equal(lifetime, slot.Lifetime);
        Assert.NotEqual(slot.Lifetime.ExpiresAtUnixMilliseconds, projected.RemainingUntil!.Value.ToUnixTimeMilliseconds());
    }

    private static PetPresentation Projection(PetsOptions options)
    {
        var catalog = PetTestData.Catalog();
        return new(catalog, options, new PetTestData.Clock());
    }
}
