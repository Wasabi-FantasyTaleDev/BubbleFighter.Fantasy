using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class SceneSeedAuthorityTests
{
    /// <summary>Every audited build publishes ordinary weapon bits in all modes and applies HC replacements only where selected.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    public async Task AuditedBuildsPublishWeaponContributionsOutsideReplacementModes(string build)
    {
        foreach (var mode in new[] { 1, 3, 8, 9, 12, 13, 14, 15, 16, 18, 19, 101 })
        {
            foreach (var draw in new[] { 0.03, 0.1 })
            {
                var random = new FixedRandom(draw);
                var authority = WeaponAuthority(random, build);
                var session = new SeedSession(1, build);
                await authority.SendAsync(session, Opening with
                {
                    Equipment = new(mode, new[] { 203103, 201001 }),
                }, default);
                var expected = draw == 0.03 ? 2000 : mode is 16 or 101 ? 1000 : 0;
                Assert.Equal(expected, SetBits(session.Sent.First().Payload));
                Assert.Equal(new ushort[] { 4, 5, 6 }, session.Sent.Select(frame => frame.Opcode));
                Assert.Equal(2000, random.Draws);
            }
        }
    }

    /// <summary>A deterministic draw between the two thresholds gives the HC primary bits and no sidearm bits.</summary>
    [Fact]
    public async Task WeaponAwareIssuanceUsesDifferentSlotThresholds()
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        var request = Opening with { Equipment = new(101, new[] { 203103, 201001 }) };
        authority.Validate(request);
        Assert.Equal(0, random.Draws);
        await authority.SendAsync(session, request, default);
        var critical = session.Sent.First().Payload;
        Assert.Equal(268, critical.Length);
        Assert.Equal(1000, SetBits(critical));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(critical, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(critical, 140));
        Assert.Equal(0xFFu, BitConverter.ToUInt32(critical, 8 + 31 * 4));
        Assert.Equal(2000, random.Draws);
    }

    /// <summary>Adding a weapon term retains the existing base policy instead of multiplying or replacing it.</summary>
    [Fact]
    public async Task WeaponTermAddsToTheExistingBasePolicy()
    {
        var session = new SeedSession(1);
        await WeaponAuthority(new FixedRandom(0.03)).SendAsync(session,
            Opening with { Equipment = new(16, new[] { 202001, 201001 }) }, default);
        Assert.Equal(2000, SetBits(session.Sent.First().Payload));
    }

    /// <summary>Equivalent copied equipment shares one issuance; changed equipment in the same round cannot redraw.</summary>
    [Fact]
    public async Task EquipmentValuesParticipateInIssuanceIdentity()
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        await authority.SendAsync(session, Opening with { Equipment = new(101, new[] { 203103, 201001 }) }, default);
        await authority.SendAsync(session, Opening with { Equipment = new(101, new[] { 201001, 203103 }) }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session,
            Opening with { Equipment = new(101, new[] { 202001, 201001 }) }, default).AsTask());
        Assert.Equal(3, session.Sent.Count);
        Assert.Equal(2000, random.Draws);
    }

    /// <summary>Missing weapon inputs reserve no issuance and publish no partial seed burst.</summary>
    [Fact]
    public async Task MissingWeaponInputsFailBeforeDrawingOrPublishing()
    {
        var random = new FixedRandom(0.1);
        var authority = WeaponAuthority(random);
        var session = new SeedSession(1);
        Assert.Throws<InvalidOperationException>(() => authority.Validate(Opening));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.SendAsync(session, Opening, default).AsTask());
        Assert.Equal(0, random.Draws);
        Assert.Empty(session.Sent);
        Assert.False(authority.Issued(1, NativeSceneSeedKind.Critical, Opening.EntityId, out _));
    }

    private static SceneSeedAuthority WeaponAuthority(Random random, string build = "cn-324",
        WeaponCriticalCatalogTests.FixtureStore? store = null) => new(build + "/relay", build, Tables, 4, 5, 6,
        Chances(Workbook(("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4"))),
        4, random, WeaponCriticalCatalog.Load(store ?? WeaponCriticalCatalogTests.Fixture(), Tables, build, new()));

    private sealed class FixedRandom(double value) : Random
    {
        internal int Draws { get; private set; }

        /// <summary>Supplies a chosen draw so threshold assertions do not depend on random frequency.</summary>
        public override double NextDouble() { Draws++; return value; }
    }
}
