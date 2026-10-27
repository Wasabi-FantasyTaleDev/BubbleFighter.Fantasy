using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Coverage for the seat character resolver the room push consults before it writes an S2C 117 map564
/// profile entry. The room row takes the server-side equipped character identity from the published service and
/// falls back to the Dao reference item otherwise. These tests drive the resolution order through the public
/// registry and seat surface, so no live client is needed.
/// </summary>
public sealed class SeatCharacterResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedSelectionRejectsMissingOrAmbiguousCharactersInsteadOfUsingDao(bool ambiguous)
    {
        var session = new RoomRegistryTests.FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);
        InventorySlotRecord[] rows = ambiguous
            ? [new(10, InventoryBag.CharacterBag, 1, 300001, 0, 1),
               new(10, InventoryBag.CharacterBag, 2, 300010, 0, 1)] : [];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SeatCharacter.ResolveAsync(session, 10, new Selection(rows), new StubRoster(300001), default).AsTask());
    }

    [Fact]
    public async Task SeatWithoutAServiceFallsBackToTheDaoItem()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RoomRegistryTests.FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);

        var (_, seat) = registry.CreateFromSetting(
            session, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 10, "host");
        var resolved = await ResolveAsync(session, 10, null);

        Assert.Equal(300001, resolved);
        Assert.Equal(0, seat.CharacterId);
    }

    [Fact]
    public async Task ServiceValueWinsOverTheDaoFallback()
    {
        var session = new RoomRegistryTests.FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);

        var resolved = await ResolveAsync(session, 10, new StubRoster(300009));

        Assert.Equal(300009, resolved);
    }

    [Fact]
    public async Task ServiceValueOutsideTheCharacterWindowIsIgnored()
    {
        var session = new RoomRegistryTests.FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);

        var resolved = await ResolveAsync(session, 10, new StubRoster(999));

        Assert.Equal(300001, resolved);
    }

    [Fact]
    public async Task ZeroServiceValueFallsBackToTheDaoItem()
    {
        var session = new RoomRegistryTests.FakeSession(1);
        session.SetLobbyPlayer(10, "host", pastLoginBurst: false);

        var resolved = await ResolveAsync(session, 10, new StubRoster(0));

        Assert.Equal(300001, resolved);
    }

    private static async ValueTask<int> ResolveAsync(
        RoomRegistryTests.FakeSession session, int userNum, ISeatCharacterResolver? resolver)
    {
        var resolved = SeatCharacter.ResolveEquipmentAsync(session, userNum, null, resolver, CancellationToken.None);
        return (await resolved).CharacterId;
    }

    private sealed class StubRoster(int equipped) : ISeatCharacterResolver
    {
        public ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct) => ValueTask.FromResult(equipped);
    }

    private sealed class Selection(IReadOnlyList<InventorySlotRecord> rows) : IEquipmentSelection
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(rows);
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NullLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
