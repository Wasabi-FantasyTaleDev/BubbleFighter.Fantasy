using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Dialect.Modern.Messages.Party;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Scene refreshes retain party members and the recipient's equipped character on the existing room path.</summary>
public sealed class PartyScenePushTests
{
    private static readonly PartySnapshot Group = new([new(22, "leader"), new(11, "member")]);

    [Fact]
    public async Task RoomInstallKeepsThePartyEvenWhenTheLeaderIsOutsideTheRoom()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var member = new RoomPushTests.RecordingSession(10);
        member.SetLobbyPlayer(11, "member");
        using var membership = member.RegisterFeature<IPartyMembership>(new Membership(Group));
        var (room, seat) = registry.CreateFromSetting(member, RoomWireSupport.OrdinarySettingWithMap(), 11, "member");
        seat.CharacterId = 300017;
        seat.EquippedItemIds = [300017, 200100];
        var sender = new CapturingSender<PartyUpdated>();
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance,
            partySender: sender);

        await loop.PushRoomToSeatAsync(room, seat, default);

        var update = Assert.Single(sender.Sent);
        Assert.Equal(Group, update.Party);
        Assert.Equal(11, update.Appearance!.UserNum);
        Assert.Equal(300017, update.Appearance.CharacterItemId);
        Assert.Equal([300017, 200100], update.Appearance.EquippedItemIds);
        Assert.DoesNotContain(InGameOpcodes.S2CUpdateParty, member.Opcodes);
    }

    [Fact]
    public async Task PlazaAndMenuRefreshRetainMembershipThenReturnToSoloAfterLeaving()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true, SendPartyUpdate = true };
        var registry = new RoomRegistry(options);
        var member = new RoomPushTests.RecordingSession(10);
        member.SetLobbyPlayer(11, "member");
        using var membership = member.RegisterFeature<IPartyMembership>(new Membership(Group));
        var sender = new CapturingSender<PartyUpdated>();
        var plaza = new PlazaRoster();
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance,
            plaza: plaza, partySender: sender);
        try
        {
            await loop.Plaza.HandleEnterPlazaAsync(member, new(InGameOpcodes.C2SUnknown106, ReadOnlyMemory<byte>.Empty), default);
            await loop.Plaza.HandleReturnToMenuAsync(member, new(105, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(2, sender.Sent.Count);
            Assert.All(sender.Sent, update =>
            {
                Assert.Equal(Group, update.Party);
                Assert.Equal(11, update.Appearance!.UserNum);
                Assert.Equal(300001, update.Appearance.CharacterItemId);
            });
            membership.Dispose();
            await loop.Plaza.HandleEnterPlazaAsync(member, new(InGameOpcodes.C2SUnknown106, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(new PartyMember(11, "member"), Assert.Single(sender.Sent[2].Party.Members));
            Assert.DoesNotContain(InGameOpcodes.S2CUpdateParty, member.Opcodes);
        }
        finally
        {
            await plaza.Executor.StopAsync();
        }
    }

    [Fact]
    public async Task MissingDialectCannotSilentlyReplaceAGroupWithASoloSnapshot()
    {
        var member = new RoomPushTests.RecordingSession(10);
        using var membership = member.RegisterFeature<IPartyMembership>(new Membership(Group));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PartyScenePush.SendAsync(member, 11, "member", 300001, [], null, default));
        Assert.Empty(member.Opcodes);
    }

    [Fact]
    public void TableProjectionRetainsTheEstablishedSoloAppearanceBytes()
    {
        var tables = PacketTables.Load(FindTables());
        int[] items = [300017, 200100, 207000];
        var actual = ModernPartyRecords.Write(tables, "kr-1256",
            new(new([new(11, "member")]), new(11, 300017, items)));
        Assert.Equal(RoomSessionBlob.UpdatePartySolo(11, "member", 300017, items), actual);
    }

    private static string FindTables()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return Path.Combine(directory.FullName, "packets");
        throw new InvalidOperationException("Repository packet tables not found.");
    }

    private sealed record Membership(PartySnapshot Party) : IPartyMembership;
}
