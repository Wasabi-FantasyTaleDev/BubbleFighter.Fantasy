using BubbleFighter.Fantasy.Contracts.Signals;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Exercises authenticated discovery, room barriers, privacy, native fallback and round revocation.</summary>
public sealed class UdpPeerCoordinatorTests
{
    /// <summary>A build that learns of a round from the load report has work exactly while a round is tracked.</summary>
    [Fact]
    public async Task TheLoopHasWorkOnlyWhileARoundIsTracked()
    {
        var fixture = new Fixture();
        var coordinator = fixture.Create();
        Assert.False(coordinator.WhenWork().IsCompleted);
        await fixture.Load(coordinator);
        Assert.True(coordinator.WhenWork().IsCompleted);
        // The round ends; the next tick prunes it and the loop may sleep again.
        fixture.Roster.Stage = RoomStage.Result;
        await coordinator.TickAsync(100, default);
        Assert.False(coordinator.WhenWork().IsCompleted);
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Starting;
        await fixture.Load(coordinator);
        Assert.True(coordinator.WhenWork().IsCompleted);
        coordinator.Dispose();
        Assert.False(coordinator.WhenWork().IsCompleted);
    }

    /// <summary>A build that prepares UDP in the waiting room is woken by the first room, and keeps ticking for a tracked round.</summary>
    [Fact]
    public async Task AWaitingRoomBuildWakesForTheFirstRoomAndStaysAwakeForItsRound()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        var rooms = new PresenceSignal();
        fixture.Roster.Rooms = rooms;
        using var coordinator = fixture.Create();
        var work = coordinator.WhenWork();
        Assert.False(work.IsCompleted);
        rooms.Refresh(() => true);
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(coordinator.WhenWork().IsCompleted);
        await coordinator.TickAsync(100, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        // The registry reports no room, but the tracked round still has to be ticked until it is pruned.
        rooms.Refresh(() => false);
        Assert.True(coordinator.WhenWork().IsCompleted);
    }

    [Fact]
    public async Task TwoPeersRequireObservedEndpointsAndBothTableAcksBeforeDirectDelivery()
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        await coordinator.TickAsync(0, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        var a = fixture.Wire.Offers[0];
        var b = fixture.Wire.Offers[1];
        await fixture.Discover(coordinator, a, "198.51.100.10", 7601, "192.168.1.10");
        await fixture.Discover(coordinator, b, "198.51.100.99", 7602, "192.168.1.11");
        await coordinator.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Rows);
        await fixture.Discover(coordinator, b, "198.51.100.10", 7602, "192.168.1.11");
        await fixture.Discover(coordinator, a, "198.51.100.10", 7699, "192.168.1.99");
        await coordinator.TickAsync(250, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        Assert.Equal(2, fixture.Wire.Ends.Count);
        Assert.Empty(fixture.Wire.Starts);
        Assert.All(fixture.Wire.Rows, row => Assert.Single(row.Candidate.LocalAddresses));
        Assert.All(fixture.Wire.Rows.Where(row => row.Candidate.Slot == 0), row => Assert.Equal(7601, row.Candidate.OuterEndPoint.Port));
        Assert.All(fixture.Wire.Rows.GroupBy(row => row.Session.Id), rows =>
            Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.Candidate.Slot)));

        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TickAsync(300, default);
        Assert.Empty(fixture.Wire.Starts);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(350, default);
        Assert.Equal(2, fixture.Wire.Starts.Count);
        await coordinator.TickAsync(449, default);
        Assert.Empty(fixture.Wire.Finishes);
        await coordinator.TickAsync(450, default);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        await coordinator.PunchReportedAsync(fixture.A, new(2, 1), default);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(b.Identity, false), default);
        Assert.Equal(new[] { fixture.B.Id }, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients);
        Assert.Null((await fixture.Filter(coordinator, fixture.RelayA, 32900)).DirectPeerRecipients);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(b.Identity, true), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(a.Identity, false), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
    }

    [Fact]
    public async Task StatusFollowsThePeerSetupFromDiscoveryToADirectPath()
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "no UDP setup for this round"), coordinator.Describe(fixture.A));
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        Assert.Equal(new UdpPathStatus(UdpPathState.Negotiating, "discovering endpoints"), coordinator.Describe(fixture.A));
        var a = fixture.Wire.Offers[0];
        var b = fixture.Wire.Offers[1];
        await fixture.Discover(coordinator, a, "198.51.100.10", 7601, "192.168.1.10");
        await fixture.Discover(coordinator, b, "198.51.100.10", 7602, "192.168.1.11");
        await coordinator.TickAsync(250, default);
        Assert.Equal(new UdpPathStatus(UdpPathState.Negotiating, "exchanging peer tables"), coordinator.Describe(fixture.B));
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(350, default);
        Assert.Equal(new UdpPathStatus(UdpPathState.Negotiating, "punching"), coordinator.Describe(fixture.A));
        await coordinator.TickAsync(450, default);
        await coordinator.PunchReportedAsync(fixture.A, new(2, 1), default);
        Assert.Equal(new UdpPathStatus(UdpPathState.Tcp, "fallback: client reported 1 direct path(s); no authenticated direct membership"), coordinator.Describe(fixture.A));
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(b.Identity, false), default);
        Assert.Equal(new UdpPathStatus(UdpPathState.Udp, "direct to 1 of 1"), coordinator.Describe(fixture.A));
        Assert.Equal("peer", coordinator.Kind);
    }

    /// <summary>Repeated native probes preserve the original deadline and require authenticated membership after finish.</summary>
    [Theory]
    [InlineData(250, 4)]
    [InlineData(0, 1)]
    public async Task PunchRetriesStayBoundedAndDoNotGrantUnreportedPaths(int retryMs, int attempts)
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create(punchWindowMs: 1000, punchRetryMs: retryMs);
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        foreach (var offer in fixture.Wire.Offers.ToArray())
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        await coordinator.TickAsync(449, default);
        Assert.Equal(2, fixture.Wire.Starts.Count);
        foreach (var now in new long[] { 450, 700, 950, 1199 })
            await coordinator.TickAsync(now, default);
        Assert.Equal(attempts * 2, fixture.Wire.Starts.Count);
        Assert.Empty(fixture.Wire.Finishes);
        Assert.Equal(UdpPathState.Negotiating, coordinator.Describe(fixture.A).State);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(fixture.Wire.Offers[1].Identity, false), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
        await coordinator.TickAsync(1200, default);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        Assert.Contains("result not received", coordinator.Describe(fixture.A).Detail);
        await coordinator.PunchReportedAsync(fixture.A, new(2, 0), default);
        Assert.Contains("client reported 0", coordinator.Describe(fixture.A).Detail);
        Assert.Equal(UdpPathState.Tcp, coordinator.Describe(fixture.A).State);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(fixture.Wire.Offers[1].Identity, false), default);
        Assert.Equal(UdpPathState.Udp, coordinator.Describe(fixture.A).State);
        await coordinator.TickAsync(2000, default);
        Assert.Equal(attempts * 2, fixture.Wire.Starts.Count);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
    }

    /// <summary>
    /// cn-406 sub_70E190 empties the peer map that table rows fill, so no offer follows an installed table:
    /// a lost acknowledgment re-sends the table instead.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    public async Task WaitingRoomNeverOffersAfterTheTableAndResendsAnUnacknowledgedTable(int refreshMs)
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        using var coordinator = fixture.Create(discoveryRefreshMs: refreshMs);
        await coordinator.TickAsync(100, default);
        foreach (var offer in fixture.Wire.Offers.ToArray())
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        foreach (var now in new long[] { 300, 349, 350 })
            await coordinator.TickAsync(now, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        Assert.Equal(8, fixture.Wire.Rows.Count);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        foreach (var now in new long[] { 5100, 20000 })
            await coordinator.TickAsync(now, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        Assert.Equal(8, fixture.Wire.Rows.Count);
        fixture.Roster.Generation++;
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(20010, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        Assert.Equal(2, fixture.Wire.Starts.Count);
    }

    /// <summary>A waiting room re-offers a lost one-shot discovery reply instead of falling back before the round loads.</summary>
    [Fact]
    public async Task WaitingRoomRetriesMissingDiscoveryUntilTheRoundLoads()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        var offers = fixture.Wire.Offers.ToArray();
        var joiner = offers.Single(offer => ReferenceEquals(offer.Session, fixture.B));
        await fixture.Discover(coordinator, joiner, "198.51.100.10", 7602, "192.168.1.10");
        foreach (var now in new long[] { 400, 1099, 1100, 2100, 3100 })
            await coordinator.TickAsync(now, default);
        Assert.Equal(UdpPathState.Negotiating, coordinator.Describe(fixture.A).State);
        Assert.Equal(8, fixture.Wire.Offers.Count);
        Assert.Empty(fixture.Wire.Rows);
        await fixture.Discover(coordinator, offers.Single(offer => ReferenceEquals(offer.Session, fixture.A)), "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(3150, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
    }

    /// <summary>A waiting-room discovery still missing when the round loads reports the timeout, not a closed relay.</summary>
    [Fact]
    public async Task WaitingRoomDiscoveryMissingAtLoadReportsTheMissingSlot()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        await fixture.Discover(coordinator, fixture.Wire.Offers.Single(offer => ReferenceEquals(offer.Session, fixture.B)), "198.51.100.10", 7602, "192.168.1.10");
        await coordinator.TickAsync(5000, default);
        fixture.Roster.Generation++;
        fixture.Roster.Stage = RoomStage.Starting;
        await coordinator.TickAsync(5010, default);
        var status = coordinator.Describe(fixture.A);
        Assert.Equal(UdpPathState.Tcp, status.State);
        Assert.Contains("no UDP discovery from slot 1 before the round loaded", status.Detail);
    }

    /// <summary>Waiting-room discovery refreshes changed mappings and stops once the actual punch begins.</summary>
    [Fact]
    public async Task WaitingRoomKeepsDiscoveryAliveAndReacknowledgesChangedCandidates()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        using var coordinator = fixture.Create(punchWindowMs: 1000);
        await coordinator.TickAsync(100, default);
        var offers = fixture.Wire.Offers.ToArray();
        foreach (var offer in offers)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        Assert.Empty(fixture.Wire.Stops);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        await fixture.Discover(coordinator, offers[0], "198.51.100.10", 7901, "192.168.1.20");
        await coordinator.TickAsync(50000, default);
        Assert.Equal(8, fixture.Wire.Rows.Count);
        Assert.All(fixture.Wire.Rows.TakeLast(4).Where(row => row.Candidate.Slot == 0), row =>
        {
            Assert.Equal(7901, row.Candidate.OuterEndPoint.Port);
            Assert.Equal(IPAddress.Parse("192.168.1.20"), Assert.Single(row.Candidate.LocalAddresses));
        });
        Assert.Empty(fixture.Wire.Stops);
        Assert.Empty(fixture.Wire.Starts);
        fixture.Roster.Generation++;
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(50010, default);
        Assert.Empty(fixture.Wire.Starts);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(50020, default);
        await coordinator.TickAsync(50030, default);
        Assert.Equal(2, fixture.Wire.Stops.Count);
        Assert.Equal(2, fixture.Wire.Starts.Count);
        Assert.Equal(12, fixture.Wire.Rows.Count);
        Assert.Equal(fixture.Wire.Rows.Skip(4).Take(4).Select(row => (row.Session.Id, row.Candidate.Slot, row.Candidate.PeerIdentity, row.Candidate.OuterEndPoint)),
            fixture.Wire.Rows.TakeLast(4).Select(row => (row.Session.Id, row.Candidate.Slot, row.Candidate.PeerIdentity, row.Candidate.OuterEndPoint)));
        await coordinator.TickAsync(50280, default);
        Assert.Equal(4, fixture.Wire.Starts.Count);
        Assert.Equal(2, fixture.Wire.Stops.Count);
        Assert.Equal(offers, fixture.Wire.Offers);
        await fixture.Discover(coordinator, offers[0], "198.51.100.10", 7999, "192.168.1.30");
        await coordinator.TickAsync(51030, default);
        Assert.Equal(12, fixture.Wire.Rows.Count);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(offers[1].Identity, false), default);
        Assert.Equal(UdpPathState.Udp, coordinator.Describe(fixture.A).State);
    }

    [Fact]
    public async Task RelayReplacementRevokesDirectDeliveryBeforeTheNextTickAndResetsBothClients()
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        var offers = fixture.Wire.Offers.ToArray();
        foreach (var offer in offers)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        await coordinator.TickAsync(300, default);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(offers[1].Identity, false), default);
        await coordinator.MembershipChangedAsync(fixture.RelayB, new(offers[0].Identity, false), default);
        Assert.Contains(fixture.B.Id, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);

        var replacement = new Session(21, "198.51.100.10");
        fixture.Roster.Seats = fixture.Roster.Seats.Select(seat => seat.Game.Id == fixture.A.Id
            ? seat with { Relay = replacement } : seat).ToArray();
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayB, 32885)).DirectPeerRecipients!);
        Assert.Null((await fixture.Filter(coordinator, replacement, 32885)).DirectPeerRecipients);
        await coordinator.MembershipChangedAsync(replacement, new(offers[1].Identity, false), default);
        Assert.Null((await fixture.Filter(coordinator, replacement, 32885)).DirectPeerRecipients);
        await coordinator.TickAsync(400, default);
        Assert.Equal(4, fixture.Wire.Offers.Count);
        Assert.Equal(4, fixture.Wire.Finishes.Count);
        await coordinator.MembershipChangedAsync(fixture.RelayB, new(offers[0].Identity, false), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayB, 32885)).DirectPeerRecipients!);
    }

    [Fact]
    public async Task SeparateNatsDoNotReceivePrivateCandidatesAndForeignPeersCannotBeSelected()
    {
        var fixture = new Fixture(secondAddress: "203.0.113.20");
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        await fixture.Discover(coordinator, fixture.Wire.Offers[0], "198.51.100.10", 7601, "192.168.1.10");
        await fixture.Discover(coordinator, fixture.Wire.Offers[1], "203.0.113.20", 7602, "10.0.0.10");
        await coordinator.TickAsync(200, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        Assert.All(fixture.Wire.Rows.Where(row => row.Session.Id != (row.Candidate.Slot == 0 ? fixture.A.Id : fixture.B.Id)), row =>
        {
            Assert.Empty(row.Candidate.LocalAddresses);
            Assert.Equal((ushort)0, row.Candidate.LocalPort);
        });
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(0x12345678, false), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedMembershipFromBeforeAToggleCannotChangeTheReplacementPeerPaths(bool relayed)
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await Prepare(100);
        var retired = fixture.Wire.Offers.Take(2).ToArray();
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(retired[1].Identity, false), default);
        Assert.Contains(fixture.B.Id, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);

        fixture.Roster.Override = false;
        await coordinator.TickAsync(400, default);
        fixture.Roster.Override = true;
        await Prepare(500);
        var current = fixture.Wire.Offers.TakeLast(2).ToArray();
        Assert.DoesNotContain(current, offer => retired.Any(old => old.Identity == offer.Identity));
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(current[1].Identity, false), default);
        var before = (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients;
        Assert.Equal(new[] { fixture.B.Id }, before);

        await coordinator.MembershipChangedAsync(fixture.RelayA, new(retired[1].Identity, relayed), default);
        Assert.Equal(before, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients);
        Assert.False(fixture.A.Connection.IsClosed);
        Assert.False(fixture.RelayA.Connection.IsClosed);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(current[1].Identity, true), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);

        async Task Prepare(long now)
        {
            await coordinator.TickAsync(now, default);
            foreach (var offer in fixture.Wire.Offers.TakeLast(2).ToArray())
                await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
            await coordinator.TickAsync(now + 50, default);
            await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
            await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
            await coordinator.TickAsync(now + 100, default);
            await coordinator.TickAsync(now + 200, default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDiscoveryOrTableAckTimesOutToTcpWithoutBlockingGameReads(bool discover)
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        if (discover)
        {
            foreach (var offer in fixture.Wire.Offers.ToArray())
                await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
            await coordinator.TickAsync(150, default);
            await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        }
        await coordinator.TickAsync(1000, default);
        Assert.StartsWith(discover ? "fallback: peer table not acknowledged by slot 2 within "
            : "fallback: no UDP discovery from slot 1, 2 within ", coordinator.Describe(fixture.A).Detail);
        Assert.Equal(4, fixture.Wire.Offers.Count);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        Assert.Empty(fixture.Wire.Starts);
        await fixture.Discover(coordinator, fixture.Wire.Offers[0], "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(2000, default);
        Assert.Equal(4, fixture.Wire.Offers.Count);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
    }

    [Theory]
    [InlineData(2u, 3u)]
    [InlineData(5u, 1u)]
    public async Task InvalidPunchCountsDoNotGrantDirectDeliveryOrInterruptAValidNegotiation(uint occupied, uint connected)
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        await coordinator.PunchReportedAsync(fixture.A, new(occupied, connected), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
        foreach (var offer in fixture.Wire.Offers.ToArray())
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        await coordinator.TickAsync(300, default);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(fixture.Wire.Offers[1].Identity, false), default);
        Assert.Contains(fixture.B.Id, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
        Assert.False(fixture.A.Connection.IsClosed);
        Assert.False(fixture.RelayA.Connection.IsClosed);
    }

    [Fact]
    public async Task NewRoundRevokesPriorProbeIdentityAndUnloadRestoresTcp()
    {
        var fixture = new Fixture();
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        var oldOffers = fixture.Wire.Offers.ToArray();
        fixture.Roster.Generation++;
        fixture.Roster.Stage = RoomStage.Starting;
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(200, default);
        Assert.Equal(4, fixture.Wire.Offers.Count);
        foreach (var offer in oldOffers)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(250, default);
        Assert.Empty(fixture.Wire.Rows);
        foreach (var offer in fixture.Wire.Offers.Skip(2).ToArray())
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(300, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await coordinator.RunAsync(stop.Token);
        Assert.Equal(6, fixture.Wire.Offers.Count);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        coordinator.Dispose();
        await fixture.Discover(coordinator, fixture.Wire.Offers[2], "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(1000, default);
        Assert.Equal(6, fixture.Wire.Offers.Count);
    }

    [Fact]
    public async Task CapacityFallbackPreservesLoadAndTcpAndUnrelatedRosterIsIgnored()
    {
        var fixture = new Fixture();
        using var tooSmall = fixture.Create(maxPeers: 1);
        await fixture.Load(tooSmall);
        fixture.Roster.Stage = RoomStage.Playing;
        await tooSmall.TickAsync(100, default);
        Assert.Empty(fixture.Wire.Offers);
        foreach (var seat in fixture.Roster.Seats)
        {
            Assert.False(seat.Game.Connection.IsClosed);
            var movement = await fixture.Filter(tooSmall, seat.Game, 32885);
            Assert.Null(movement.DirectPeerRecipients);
        }
        using var coordinator = fixture.Create();
        var other = new Session(99, "203.0.113.99");
        var ran = false;
        await coordinator.LoadAsync(other, new(), (_, _, _) => { ran = true; return ValueTask.CompletedTask; }, default);
        Assert.True(ran);
        await coordinator.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Offers);
    }

    [Fact]
    public async Task SinglePlayerDefaultsToTcpAndExplicitOnRestartsDiscoveryWithAFreshIdentity()
    {
        var fixture = new Fixture();
        fixture.Roster.Seats = [fixture.Roster.Seats[0]];
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.Override = true;
        await coordinator.TickAsync(200, default);
        var first = Assert.Single(fixture.Wire.Offers);
        await fixture.Discover(coordinator, first, "198.51.100.10", 7601, "192.168.1.10");
        fixture.Roster.Override = false;
        await coordinator.TickAsync(250, default);
        Assert.Single(fixture.Wire.Finishes);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        fixture.Roster.Override = true;
        fixture.Roster.Seats = [fixture.Roster.Seats[0] with { RelayInitialized = false }];
        await coordinator.TickAsync(300, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        fixture.Roster.Seats = [fixture.Roster.Seats[0] with { RelayInitialized = true }];
        await coordinator.TickAsync(350, default);
        Assert.Equal(3, fixture.Wire.Offers.Count);
        var current = fixture.Wire.Offers[^1];
        Assert.NotEqual(first.Identity, current.Identity);
        await fixture.Discover(coordinator, first, "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(400, default);
        Assert.Empty(fixture.Wire.Rows);
        await fixture.Discover(coordinator, current, "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(450, default);
        Assert.Single(fixture.Wire.Rows);
    }

    [Fact]
    public async Task NonMatchLoadReportsCannotAllocatePeerSetupEvenWithAForcedOverride()
    {
        var fixture = new Fixture();
        fixture.Roster.IsMatchRoom = false;
        fixture.Roster.Override = true;
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.IsMatchRoom = true;
        await coordinator.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.Stage = RoomStage.Starting;
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(300, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
    }

    [Fact]
    public async Task RoomStageDiscoveryPreparesBeforeLoadingButPunchingRequiresAllInitializedPlayingRelays()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.Generation = 0;
        var playingSeats = fixture.Roster.Seats;
        fixture.Roster.Seats = playingSeats.Select(seat => seat with { Relay = null, RelayInitialized = false }).ToArray();
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        foreach (var offer in fixture.Wire.Offers.ToArray())
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(150, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        await coordinator.TickAsync(10000, default);
        Assert.Empty(fixture.Wire.Starts);
        Assert.Empty(fixture.Wire.Finishes);
        fixture.Roster.Stage = RoomStage.Starting;
        fixture.Roster.Generation = 1;
        await fixture.Load(coordinator);
        await coordinator.TickAsync(10100, default);
        Assert.Empty(fixture.Wire.Starts);
        fixture.Roster.Stage = RoomStage.Playing;
        fixture.Roster.Seats = [playingSeats[0], playingSeats[1] with { RelayInitialized = false }];
        await coordinator.TickAsync(10200, default);
        Assert.Empty(fixture.Wire.Starts);
        fixture.Roster.Seats = playingSeats;
        await coordinator.TickAsync(10300, default);
        Assert.Equal(2, fixture.Wire.Starts.Count);
        await coordinator.TickAsync(10400, default);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        Assert.Equal(2, fixture.Wire.Offers.Count);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(fixture.Wire.Offers[1].Identity, false), default);
        Assert.Contains(fixture.B.Id, (await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingDiscoveryCanFinishDuringLoadingWithoutBlockingLoadOrOfferingAgain(bool discoveredBeforeStart)
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.Generation = 0;
        var playingSeats = fixture.Roster.Seats;
        fixture.Roster.Seats = playingSeats.Select(seat => seat with { Relay = null, RelayInitialized = false }).ToArray();
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        if (discoveredBeforeStart) await Discover();
        fixture.Roster.Stage = RoomStage.Starting;
        fixture.Roster.Generation = 1;
        await fixture.Load(coordinator);
        if (!discoveredBeforeStart) await Discover();
        await coordinator.TickAsync(150, default);
        Assert.Equal(4, fixture.Wire.Rows.Count);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Starts);
        fixture.Roster.Stage = RoomStage.Playing;
        fixture.Roster.Seats = playingSeats;
        await coordinator.TickAsync(250, default);
        Assert.Equal(2, fixture.Wire.Starts.Count);
        Assert.Equal(2, fixture.Wire.Offers.Count);

        async Task Discover()
        {
            foreach (var offer in fixture.Wire.Offers.ToArray())
                await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        }
    }

    [Fact]
    public async Task StartingBeforeDiscoveryReturnsKeepsTcpAndDoesNotStartPunchingFromLateReports()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.Generation = 0;
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        var offers = fixture.Wire.Offers.ToArray();
        fixture.Roster.Stage = RoomStage.Starting;
        fixture.Roster.Generation = 1;
        await fixture.Load(coordinator);
        await coordinator.TickAsync(300, default);
        Assert.Equal(2, fixture.Wire.Finishes.Count);
        fixture.Roster.Stage = RoomStage.Playing;
        foreach (var offer in offers)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TableAcknowledgedAsync(fixture.B, new(), default);
        await coordinator.TickAsync(350, default);
        Assert.Empty(fixture.Wire.Rows);
        Assert.Empty(fixture.Wire.Starts);
        await coordinator.MembershipChangedAsync(fixture.RelayA, new(offers[1].Identity, false), default);
        Assert.Empty((await fixture.Filter(coordinator, fixture.RelayA, 32885)).DirectPeerRecipients!);
        Assert.All(fixture.Roster.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingRoomReinstallOrSeatChangeRevokesEveryOldDiscoveryIdentity(bool reinstall)
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.Override = true;
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        var old = fixture.Wire.Offers.ToArray();
        if (reinstall) fixture.Roster.InstallationVersion++;
        else fixture.Roster.Seats = [fixture.Roster.Seats[0]];
        await coordinator.TickAsync(150, default);
        var current = fixture.Wire.Offers.Skip(old.Length).ToArray();
        Assert.Equal(reinstall ? 2 : 1, current.Length);
        Assert.DoesNotContain(current, offer => old.Any(previous => previous.Identity == offer.Identity));
        foreach (var offer in old)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Rows);
        foreach (var offer in current)
            await fixture.Discover(coordinator, offer, "198.51.100.10", 7601 + (int)offer.Session.Id, "192.168.1.10");
        await coordinator.TickAsync(250, default);
        Assert.Equal(current.Length * current.Length, fixture.Wire.Rows.Count);
    }

    [Fact]
    public async Task RoomOnlyDiscoveryDoesNotOfferAfterLoadingOrInNonMatchScenesAndCapacityFallsBackToTcp()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        using var coordinator = fixture.Create();
        await fixture.Load(coordinator);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(100, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.IsMatchRoom = false;
        fixture.Roster.Override = true;
        await coordinator.TickAsync(150, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.IsMatchRoom = true;
        using var tooSmall = fixture.Create(maxPeers: 1);
        await tooSmall.TickAsync(200, default);
        Assert.Empty(fixture.Wire.Offers);
        await coordinator.TickAsync(250, default);
        Assert.Equal(2, fixture.Wire.Offers.Count);
    }

    [Fact]
    public async Task RoomPreparationHonorsSinglePlayerDefaultForcedToggleAndTheNextRoundGeneration()
    {
        var fixture = new Fixture();
        fixture.Wire.DiscoveryInWaitingRoom = true;
        fixture.Roster.Stage = RoomStage.Waiting;
        fixture.Roster.Seats = [fixture.Roster.Seats[0]];
        using var coordinator = fixture.Create();
        await coordinator.TickAsync(100, default);
        Assert.Empty(fixture.Wire.Offers);
        fixture.Roster.Override = true;
        await coordinator.TickAsync(150, default);
        var original = Assert.Single(fixture.Wire.Offers);
        fixture.Roster.Override = false;
        await coordinator.TickAsync(200, default);
        Assert.Single(fixture.Wire.Finishes);
        fixture.Roster.Override = true;
        await coordinator.TickAsync(250, default);
        var resumed = fixture.Wire.Offers[^1];
        Assert.NotEqual(original.Identity, resumed.Identity);
        await fixture.Discover(coordinator, original, "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(300, default);
        Assert.Empty(fixture.Wire.Rows);
        await fixture.Discover(coordinator, resumed, "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(350, default);
        Assert.Single(fixture.Wire.Rows);
        await coordinator.TableAcknowledgedAsync(fixture.A, new(), default);
        await coordinator.TickAsync(400, default);
        fixture.Roster.Generation++;
        fixture.Roster.Stage = RoomStage.Starting;
        await coordinator.TickAsync(450, default);
        Assert.Empty(fixture.Wire.Starts);
        fixture.Roster.Stage = RoomStage.Playing;
        await coordinator.TickAsync(500, default);
        Assert.Single(fixture.Wire.Starts);
        // The round's seat records replaced the seat identity, so the same table is installed again before punching.
        Assert.Equal(2, fixture.Wire.Rows.Count);
        Assert.Equal((fixture.Wire.Rows[0].Candidate.Slot, fixture.Wire.Rows[0].Candidate.PeerIdentity, fixture.Wire.Rows[0].Candidate.OuterEndPoint),
            (fixture.Wire.Rows[1].Candidate.Slot, fixture.Wire.Rows[1].Candidate.PeerIdentity, fixture.Wire.Rows[1].Candidate.OuterEndPoint));
        await coordinator.TickAsync(600, default);
        fixture.Roster.Stage = RoomStage.Result;
        await coordinator.TickAsync(650, default);
        fixture.Roster.Stage = RoomStage.Waiting;
        await coordinator.TickAsync(700, default);
        var next = fixture.Wire.Offers[^1];
        Assert.NotEqual(resumed.Identity, next.Identity);
        await fixture.Discover(coordinator, resumed, "198.51.100.10", 7601, "192.168.1.10");
        await coordinator.TickAsync(750, default);
        Assert.Equal(2, fixture.Wire.Rows.Count);
    }

    private sealed class Fixture
    {
        internal Session A { get; } = new(1, "198.51.100.10");
        internal Session B { get; }
        internal Session RelayA { get; } = new(11, "198.51.100.10");
        internal Session RelayB { get; }
        internal Roster Roster { get; }
        internal Wire Wire { get; } = new();

        internal Fixture(string secondAddress = "198.51.100.10")
        {
            B = new(2, secondAddress);
            RelayB = new(12, secondAddress);
            Roster = new([new(0, 1, 1, A, RelayA, true), new(1, 2, 2, B, RelayB, true)]);
        }

        internal UdpPeerCoordinator Create(int maxPeers = 4, int punchWindowMs = 100, int punchRetryMs = 250,
            int discoveryRefreshMs = 1000) => new(new()
        {
            Protocol = Roster.Protocol, UdpProtocol = "test/udp", MaxPeers = maxPeers, MaxRounds = 2,
            DiscoveryTimeoutMs = 200, TableTimeoutMs = 200, PunchWindowMs = punchWindowMs, PunchRetryMs = punchRetryMs,
            DiscoveryRefreshMs = discoveryRefreshMs,
        }, Roster, Wire, new Endpoint(), NullLogger.Instance);

        internal async Task Load(UdpPeerCoordinator coordinator)
        {
            var forwarded = 0;
            foreach (var session in new[] { A, B })
                await coordinator.LoadAsync(session, new(), (_, _, _) => { forwarded++; return ValueTask.CompletedTask; }, default);
            Assert.Equal(2, forwarded);
        }

        internal ValueTask Discover(UdpPeerCoordinator coordinator, (ISession Session, uint Identity) offer,
            string outer, int port, string local)
        {
            var bytes = new byte[10];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, offer.Identity);
            IPAddress.Parse(local).GetAddressBytes().CopyTo(bytes, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)port);
            return coordinator.ReceiveAsync(new(IPAddress.Parse(outer), port), bytes, default);
        }

        internal async Task<RoundFeedSent> Filter(UdpPeerCoordinator coordinator, ISession session, ushort opcode)
        {
            RoundFeedSent? forwarded = null;
            await coordinator.FilterMovementAsync(session, new(opcode, ReadOnlyMemory<byte>.Empty), (_, message, _) =>
            { forwarded = message; return ValueTask.CompletedTask; }, default);
            return Assert.IsType<RoundFeedSent>(forwarded);
        }
    }

    private sealed class Roster(IReadOnlyList<RoomSeatView> seats) : IRoomPeerRoster
    {
        internal PresenceSignal? Rooms;
        public Task WhenAnyRoom() => Rooms?.WhenPresent ?? Task.CompletedTask;
        internal IReadOnlyList<RoomSeatView> Seats = seats;
        public string Protocol => "cn-324/game";
        internal RoomStage Stage = RoomStage.Starting;
        internal int Generation = 1;
        internal bool? Override;
        internal bool IsMatchRoom = true;
        internal long InstallationVersion;
        public IReadOnlyList<RoomRosterView> Snapshot() => [Find(1)!];
        public async ValueTask<bool> ExecuteAsync(int roomId, Func<RoomRosterView, CancellationToken, ValueTask> operation, CancellationToken ct)
        {
            if (Find(roomId) is not { } view) return false;
            await operation(view, ct);
            return true;
        }
        public RoomRosterView? Find(int roomId) => roomId == 1 ? new(1, Stage, Generation, 1, Seats, false)
            { IsMatchRoom = IsMatchRoom, UdpOverride = Override, InstallationVersion = InstallationVersion } : null;
        public RoomRosterView? FindBySession(long sessionId) =>
            Seats.Any(seat => seat.Game.Id == sessionId || seat.Relay?.Id == sessionId) ? Find(1) : null;
    }

    private sealed class Wire : IUdpPeerDialect
    {
        public string Protocol => "cn-324/game";
        public bool DiscoveryInWaitingRoom { get; set; }
        internal List<(ISession Session, uint Identity)> Offers { get; } = [];
        internal List<(ISession Session, UdpPeerCandidate Candidate)> Rows { get; } = [];
        internal List<ISession> Ends { get; } = [];
        internal List<ISession> Stops { get; } = [];
        internal List<ISession> Starts { get; } = [];
        internal List<ISession> Finishes { get; } = [];
        public UdpPeerDiscovery? ReadDiscovery(ReadOnlyMemory<byte> datagram) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(datagram.Span), new IPAddress(datagram.Span.Slice(4, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram.Span[8..]));
        public bool IsPeerMovement(ushort opcode) => opcode == 32885;
        public ValueTask OfferAsync(ISession session, ushort port, uint identity, CancellationToken ct)
        { Offers.Add((session, identity)); return ValueTask.CompletedTask; }
        public ValueTask StopDiscoveryAsync(ISession session, CancellationToken ct)
        { Stops.Add(session); return ValueTask.CompletedTask; }
        public ValueTask SendCandidateAsync(ISession session, UdpPeerCandidate candidate, CancellationToken ct)
        { Rows.Add((session, candidate)); return ValueTask.CompletedTask; }
        public ValueTask EndTableAsync(ISession session, CancellationToken ct)
        { Ends.Add(session); return ValueTask.CompletedTask; }
        public ValueTask StartPunchAsync(ISession session, CancellationToken ct)
        { Starts.Add(session); return ValueTask.CompletedTask; }
        public ValueTask FinishPunchAsync(ISession session, CancellationToken ct)
        { Finishes.Add(session); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id, string address) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id, address);
        public ClientProfile Profile { get; } = new("cn-324", "cn", 324, 0, 0, "cn-324");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id, string address) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Parse(address), 7500);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Endpoint : IUdpDatagramEndpoint
    {
        public string Protocol => "test/udp";
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 7600);
        public IUdpDatagramSubscription Register(UdpDatagramHandler handler) => throw new NotSupportedException();
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, CancellationToken ct) => throw new NotSupportedException();
    }
}
