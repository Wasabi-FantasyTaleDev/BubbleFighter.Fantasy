using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Native creation accepts exactly the character types the build's creation stage offers.</summary>
public sealed class CharacterCreationTypesTests
{
    /// <summary>The Indonesian list (Leo 11, Lavi 12) creates item 300011; a type the stage does not offer is refused.</summary>
    [Theory]
    [InlineData(new[] { 1, 2 }, 1, 0, 300001)]
    [InlineData(new[] { 1, 2 }, 2, 0, 300002)]
    [InlineData(new[] { 1, 2 }, 11, 4, 0)]
    [InlineData(new[] { 11, 12 }, 11, 0, 300011)]
    [InlineData(new[] { 11, 12 }, 12, 0, 300012)]
    [InlineData(new[] { 11, 12 }, 1, 4, 0)]
    public async Task CreationAcceptsOnlyTheOfferedTypes(int[] offered, int requested, int result, int worn)
    {
        var players = new InMemoryPlayerRepository();
        var kit = new StarterKit([300001, 202001], [300001, 202001]);
        using var creation = new CharacterCreationPreview(16, players, kit, inventory: null,
            persistent: true, atomicInventory: true, characterTypes: offered);
        var session = new Session();
        var replies = new Replies();
        creation.Open(session, "account");

        await creation.CreateAsync(session, new CharacterCreateRequested("Nick", requested), replies, default);

        Assert.Equal(result, Assert.Single(replies.Sent).Result);
        if (result == 0)
            Assert.Contains(worn, creation.KitFor("account").WornItemIds);
    }

    /// <summary>Each refused name gets the code whose native dialog explains it (cn-717 sub_8A0470 / sub_89FF30).</summary>
    [Theory]
    [InlineData("", CharacterCreationResult.NameUnusable)]
    [InlineData("Ni\tck", CharacterCreationResult.NameUnusable)]
    [InlineData("ThirteenChars", CharacterCreationResult.NameLength)]
    [InlineData("TwelveChars1", CharacterCreationResult.Accepted)]
    public async Task NameRefusalsNameTheirReason(string nickname, int expected)
    {
        using var creation = new CharacterCreationPreview(16, new InMemoryPlayerRepository(),
            new StarterKit([300001], [300001]), inventory: null, persistent: true, atomicInventory: true);
        var session = new Session();
        var names = new NameReplies();
        var created = new Replies();
        creation.Open(session, "account");

        await creation.CheckNameAsync(session, new CharacterNameRequested(nickname), names, default);
        await creation.CreateAsync(session, new CharacterCreateRequested(nickname, 1), created, default);

        Assert.Equal(expected, Assert.Single(names.Sent).Result);
        Assert.Equal(expected, Assert.Single(created.Sent).Result);
    }

    /// <summary>A name another account holds is answered as taken, at the check and at the submission.</summary>
    [Fact]
    public async Task ATakenNameIsAnsweredAsTaken()
    {
        var players = new InMemoryPlayerRepository();
        var kit = new StarterKit([300001], [300001]);
        using var creation = new CharacterCreationPreview(16, players, kit, inventory: null,
            persistent: true, atomicInventory: true);
        var first = new Session(1);
        var second = new Session(2);
        creation.Open(first, "first");
        creation.Open(second, "second");
        var created = new Replies();
        await creation.CreateAsync(first, new CharacterCreateRequested("Nick", 1), created, default);

        var names = new NameReplies();
        await creation.CheckNameAsync(second, new CharacterNameRequested("Nick"), names, default);
        await creation.CreateAsync(second, new CharacterCreateRequested("Nick", 1), created, default);

        Assert.Equal(CharacterCreationResult.NameTaken, Assert.Single(names.Sent).Result);
        Assert.Equal([CharacterCreationResult.Accepted, CharacterCreationResult.NameTaken], created.Sent.Select(r => r.Result));
    }

    /// <summary>The lobby row refuses an empty, duplicated or out-of-range type list at load time.</summary>
    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 1 })]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 32 })]
    public void LobbyOptionsRejectInvalidCreationTypes(int[] types)
    {
        var options = new LobbyOptions { CreationCharacterTypes = types };
        Assert.Contains(options.Validate(), error => error.MemberNames.Contains(nameof(LobbyOptions.CreationCharacterTypes)));
    }

    private sealed class Replies : IMessageSender<CharacterCreateReplied>
    {
        internal List<CharacterCreateReplied> Sent { get; } = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, CharacterCreateReplied message, CancellationToken ct)
        { Sent.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class NameReplies : IMessageSender<CharacterNameReplied>
    {
        internal List<CharacterNameReplied> Sent { get; } = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, CharacterNameReplied message, CancellationToken ct)
        { Sent.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id = 1) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => id;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => new("test", "id", 400, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
