using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the native local-seat gate used by plaza recruitment controls.</summary>
public sealed class NativePlazaRecruitmentTests
{
    /// <summary>The final legacy entry records retain the audited gate instead of clearing it with menu defaults.</summary>
    [Theory]
    [InlineData("cn-279", "unknown_104")]
    [InlineData("cn-324", "unknown_108")]
    [InlineData("cn-406", "unknown_136")]
    [InlineData("cn-717", "unknown_140")]
    [InlineData("kr-812", "unknown_140")]
    public void LegacyEntryPreservesRecruitmentGate(string build, string field)
    {
        var tables = TestBuilds.Packets;
        var schema = TestBuilds.Of(build).Schema;
        var member = new PlazaMember(0, 7, "Ab", 194, 300001, [300001]);
        foreach (var allowed in new[] { false, true })
        {
            var record = PlayRoomCodec.WritePlazaSeatRecord(tables, build, schema, member, allowed);
            Assert.Equal(allowed, PacketCodec.Read(tables, build, "UserSlot", record)[field]);
            var slot = PlayRoomCodec.WritePlazaSeatSlot(tables, build, schema, member, allowed);
            var parsed = PacketCodec.Read(tables, build, "RoomSeatUpdate", slot);
            var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed["user"]);
            Assert.Equal(allowed, user[field]);
        }
    }

    /// <summary>The modern plaza enables the solo leader's control without changing a normal room install.</summary>
    [Fact]
    public void ModernPlazaGateDoesNotLeakIntoRoomSeats()
    {
        var tables = TestBuilds.Packets;
        var member = new RoomMemberView(0, 7, "Ab", 194, 0, false, 300001, [300001]);
        var body = PlazaRecords.Install(tables, "kr-1256", [member], 0, 3, false);
        var parsed = PacketCodec.Read(tables, "kr-1256", "gspGotoRoom", body);
        var seats = Assert.IsType<object?[]>(parsed["user_slots"]);
        var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(seats[0]);
        Assert.True(Assert.IsType<bool>(user["unknown_104"]));
        Assert.False(Assert.IsType<bool>(RoomRecords.Seat(tables, "kr-1256", member).GetValueOrDefault("unknown_104", false)));
    }

    /// <summary>A committed party follower must not gain its leader's recruitment control.</summary>
    [Fact]
    public void PartyFollowersAreNotRecruiters()
    {
        var session = new Session();
        Assert.True(NativePlazaRecruitment.Allowed(session, 9));
        using var registration = session.RegisterFeature<IPartyMembership>(new Membership(new PartySnapshot([new(7, "Ab"), new(9, "Cd")])));
        Assert.True(NativePlazaRecruitment.Allowed(session, 7));
        Assert.False(NativePlazaRecruitment.Allowed(session, 9));
    }

    private sealed record Membership(PartySnapshot Party) : IPartyMembership;

    private sealed class Session : ISessionWithFeatures
    {
        public long Id => 1;
        public ClientProfile Profile => new("kr-812", "kr", 812, 0, 0, "kr-812");
        public IConnection Connection => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
