using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    /// <summary>The build's native view path determines whether even an unchanged worn set rebuilds a previously replaced weapon.</summary>
    [Theory]
    [InlineData("cn-324", false)]
    [InlineData("cn-406", true)]
    [InlineData("cn-404", true)]
    [InlineData("cn-408", true)]
    [InlineData("id-400", true)]
    public async Task EquipmentViewResolvesInstalledWeaponsThroughItsOwnBuildPolicy(string build, bool rebuilds)
    {
        var authority = WeaponAuthority(new FixedRandom(0.1), build);
        var session = new SeedSession(1, build);
        await authority.SendAsync(session, WeaponOpening, default);
        var replaced = Revision() with { EffectivePrimarySpecification = 3103 };
        await authority.SendAsync(session, replaced, default);
        var republished = authority.WithPublishedEquipment(replaced, WeaponOpening.Equipment!) with { Revision = 2 };
        Assert.Equal(rebuilds ? 3221 : 3103, republished.EffectivePrimarySpecification);
        await authority.SendAsync(session, republished, default);
        Assert.Equal(rebuilds ? 1000 : 0, SetBits(session.Sent.Last().Payload));
        Assert.Equal(rebuilds ? 5 : 4, session.Attempts);
        var changed = authority.WithPublishedEquipment(republished,
            new(101, new[] { 202001, 201001 })) with { Revision = 3 };
        Assert.Equal(rebuilds ? 2001 : 3103, changed.EffectivePrimarySpecification);
        await authority.SendAsync(session, changed, default);
        Assert.Equal(0, SetBits(session.Sent.Last().Payload));
        Assert.Equal(rebuilds ? 6 : 4, session.Attempts);
    }

    /// <summary>A view replaces the attribute aggregate while installed weapons retain their resolved mode replacement and reported slot identity.</summary>
    [Fact]
    public async Task PublishedEquipmentChangesAttributesWithoutGuessingInstalledWeapons()
    {
        var store = WeaponCriticalCatalogTests.Fixture();
        WeaponCriticalCatalogTests.AddEffect(store, 100051, "40");
        var authority = WeaponAuthority(new FixedRandom(0.3), store: store);
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        var equipment = new MatchSeedEquipment(101, new[] { 202001, 201001, 100051 });
        var candidate = authority.WithPublishedEquipment(Revision(), equipment);
        Assert.Equal(3221, candidate.EffectivePrimarySpecification);
        Assert.Equal(1001, candidate.EffectiveSidearmSpecification);
        Assert.Equal(equipment, candidate.Equipment);
        await authority.SendAsync(session, candidate, default);
        Assert.Equal(2000, SetBits(session.Sent.Last().Payload));
        var reported = candidate with { EffectivePrimarySpecification = 1001, EffectiveSidearmSpecification = 3103, Revision = 2 };
        var republished = authority.WithPublishedEquipment(reported, new(101, new[] { 203103, 201001 }));
        Assert.Equal(1001, republished.EffectivePrimarySpecification);
        Assert.Equal(3103, republished.EffectiveSidearmSpecification);
        await authority.SendAsync(session, republished, default);
        Assert.Equal(0, SetBits(session.Sent.Last().Payload));
        Assert.Throws<InvalidOperationException>(() => authority.WithPublishedEquipment(reported, new(16, new[] { 203103 })));
    }

    private static readonly MatchSeedTablesRequested WeaponOpening = Opening with
    {
        Equipment = new(101, new[] { 203103, 201001 }),
    };

    private static MatchCriticalSeedUpdate Revision(long revision = 1) =>
        new(Opening.RoomId, Opening.Generation, Opening.EntityId, revision, WeaponOpening.Equipment!);

    /// <summary>A changed effective specification replaces only critical tables without applying a second mode substitution.</summary>
    [Fact]
    public async Task EffectiveWeaponRevisionDoesNotRepeatModeReplacement()
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        var update = Revision() with { EffectivePrimarySpecification = 3103 };
        await authority.SendAsync(session, update, default);
        await authority.SendAsync(session, update with { }, default);
        await authority.SendAsync(session, WeaponOpening, default);
        Assert.Equal(new ushort[] { 4, 5, 6, 4 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(1000, SetBits(session.Sent.First().Payload));
        Assert.Equal(0, SetBits(session.Sent.Last().Payload));
        Assert.Equal(4000, random.Draws);
    }

    /// <summary>Equivalent probabilities advance input identity without resetting the native critical cursors.</summary>
    [Fact]
    public async Task EqualProbabilitiesDoNotRedrawOrReinstall()
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        await authority.SendAsync(session, Revision() with { EffectivePrimarySpecification = 3221 }, default);
        await authority.SendAsync(session, Revision(2) with { EffectivePrimarySpecification = 3221 }, default);
        Assert.Equal(3, session.Attempts);
        Assert.Equal(2000, random.Draws);
    }

    /// <summary>Session, round, actor, ordering and resource failures cannot draw or publish a critical revision.</summary>
    [Theory]
    [InlineData("session")]
    [InlineData("build")]
    [InlineData("room")]
    [InlineData("generation")]
    [InlineData("actor")]
    [InlineData("skipped")]
    [InlineData("zero")]
    [InlineData("specification")]
    public async Task InvalidCriticalRevisionKeepsTheOpeningIssuance(string fault)
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        var update = Revision() with { EffectivePrimarySpecification = 3103 };
        update = fault switch
        {
            "room" => update with { RoomId = 42 },
            "generation" => update with { Generation = 8 },
            "actor" => update with { EntityId = 1_100_000 },
            "skipped" => update with { Revision = 2 },
            "zero" => update with { Revision = 0 },
            "specification" => update with { EffectivePrimarySpecification = 999999 },
            _ => update,
        };
        var target = fault == "session" ? new SeedSession(2) : fault == "build" ? new SeedSession(1, "cn-406") : session;
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(target, update, default).AsTask());
        Assert.Equal(3, session.Attempts);
        Assert.Equal(2000, random.Draws);
        await authority.SendAsync(session, Revision(), default);
        Assert.Equal(3, session.Attempts);
    }

    /// <summary>Old or conflicting revisions cannot reinstall a previous table after a successful transition.</summary>
    [Fact]
    public async Task StaleAndConflictingCriticalRevisionsAreRejected()
    {
        var authority = WeaponAuthority(new FixedRandom(0.1));
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        await authority.SendAsync(session, Revision(), default);
        await authority.SendAsync(session, Revision(2) with { EffectivePrimarySpecification = 3103 }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Revision(), default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session,
            Revision(2) with { EffectivePrimarySpecification = 2001 }, default).AsTask());
        Assert.Equal(4, session.Attempts);
    }

    /// <summary>A blocked send shares duplicate completion and refuses both a later revision and a newer opening.</summary>
    [Fact]
    public async Task PendingCriticalRevisionCannotBeOvertaken()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authority = WeaponAuthority(new FixedRandom(0.1));
        var session = new SeedSession(1) { BeforeSend = (attempt, ct) => attempt == 4 ? release.Task.WaitAsync(ct) : Task.CompletedTask };
        await authority.SendAsync(session, WeaponOpening, default);
        var update = Revision() with { EffectivePrimarySpecification = 3103 };
        var first = authority.SendAsync(session, update, default).AsTask();
        var duplicate = authority.SendAsync(session, update, default).AsTask();
        try
        {
            Assert.False(duplicate.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Revision(2), default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session,
                WeaponOpening with { Generation = 8 }, default).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, duplicate);
        }
        Assert.Equal(4, session.Attempts);
    }

    /// <summary>Failure is retained without replay or redraw, and later updates cannot conceal the failed installation.</summary>
    [Fact]
    public async Task FailedCriticalRevisionRetainsItsOriginalOutcome()
    {
        var failure = new IOException("critical revision transport failed");
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1) { BeforeSend = (attempt, _) => attempt == 4 ? Task.FromException(failure) : Task.CompletedTask };
        await authority.SendAsync(session, WeaponOpening, default);
        var update = Revision() with { EffectivePrimarySpecification = 3103 };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, update, default).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => authority.SendAsync(session, update, default).AsTask()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Revision(2), default).AsTask());
        Assert.Equal(4, session.Attempts);
        Assert.Equal(4000, random.Draws);
    }

    /// <summary>Closing the relay removes every revision association and forbids an orphaned live update.</summary>
    [Fact]
    public async Task RemovedSessionHasNoLiveCriticalRevision()
    {
        var authority = WeaponAuthority(new FixedRandom(0.1));
        var session = new SeedSession(1);
        await authority.SendAsync(session, WeaponOpening, default);
        authority.Remove(session.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Revision(), default).AsTask());
        Assert.Equal(3, session.Attempts);
    }

    /// <summary>An eligible timed set changes both slots only while the accepted effect is active.</summary>
    [Fact]
    public async Task TimedCriticalActivationAndExpiryUseSeparateRevisions()
    {
        var store = WeaponCriticalCatalogTests.Fixture();
        WeaponCriticalCatalogTests.AddEffect(store, 100216, "5", name: "AMequipValue");
        WeaponCriticalCatalogTests.AddEffect(store, 100216, "5000", name: "AMcriFactorIncDuration");
        WeaponCriticalCatalogTests.AddEffect(store, 100216, "40", name: "AMcriFactorIncAmount");
        var random = new FixedRandom(0.3);
        var authority = WeaponAuthority(random, store: store);
        var session = new SeedSession(1);
        var equipment = new MatchSeedEquipment(1, new[] { 203103, 201001, 100216 });
        await authority.SendAsync(session, WeaponOpening with { Equipment = equipment }, default);
        Assert.Equal(5000, authority.TimedEffectDuration(equipment));
        var active = Revision() with { Equipment = equipment, TimedEffectActive = true };
        await authority.SendAsync(session, active, default);
        await authority.SendAsync(session, active with { Revision = 2 }, default);
        await authority.SendAsync(session, active with { Revision = 3, TimedEffectActive = false }, default);
        Assert.Equal(new ushort[] { 4, 5, 6, 4, 4 }, session.Sent.Select(frame => frame.Opcode));
        Assert.Equal(0, SetBits(session.Sent.First().Payload));
        Assert.Equal(2000, SetBits(session.Sent.ElementAt(3).Payload));
        Assert.Equal(0, SetBits(session.Sent.Last().Payload));
        Assert.Equal(6000, random.Draws);
    }
}
