using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Lobby.Data;

namespace BubbleFighter.Fantasy.Lobby;

// Recording previews retain bounded choices; persistent creation writes the account and starter kit atomically.
internal sealed class CharacterCreationPreview(int capacity, IPlayerRepository players,
    StarterKit starter, LoginInventoryResolver? inventory, bool persistent = false, bool atomicInventory = false,
    IReadOnlyCollection<int>? characterTypes = null) : IDisposable
{
    // The character types this build's creation stage offers; item 300000 + type is the character.
    private readonly IReadOnlyCollection<int> _characterTypes = characterTypes ?? [1, 2];
    private sealed record Pending(string Account, bool Created);
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly Dictionary<string, (string Nickname, int Character)> _created = new(StringComparer.Ordinal);

    internal void Open(ISession session, string account)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(session.Id)) throw new InvalidOperationException("Creation is already open.");
            if (_pending.Count >= capacity || (!persistent && !_created.ContainsKey(account) && _created.Count >= capacity))
                throw new InvalidOperationException("Character creation preview capacity reached.");
            _created.Remove(account);
            _pending.Add(session.Id, new(account, false));
        }
    }

    internal async ValueTask CheckNameAsync(ISession session, CharacterNameRequested request,
        IMessageSender<CharacterNameReplied> sender, CancellationToken ct)
    {
        int result;
        string account;
        lock (_gate)
        {
            account = RequirePending(session).Account;
            result = IsAvailable(request.Nickname) ? 0 : 1;
        }
        if (persistent && result == 0 && await players.NicknameExistsAsync(request.Nickname, ct, account).ConfigureAwait(false))
            result = 1;
        await sender.SendAsync(session, new(result, request.Nickname), ct).ConfigureAwait(false);
    }

    internal async ValueTask CreateAsync(ISession session, CharacterCreateRequested request,
        IMessageSender<CharacterCreateReplied> sender, CancellationToken ct)
    {
        string account;
        var accepted = false;
        lock (_gate)
        {
            var pending = RequirePending(session);
            account = pending.Account;
            if (!pending.Created && _characterTypes.Contains(request.CharacterType) && IsAvailable(request.Nickname)
                && !_created.ContainsKey(account) && _created.Count < capacity)
            {
                _created.Add(account, (request.Nickname, 300000 + request.CharacterType));
                accepted = true;
            }
        }
        if (!accepted)
        {
            await sender.SendAsync(session, new(1), ct).ConfigureAwait(false);
            return;
        }
        var player = persistent
            ? await players.CreateCharacterAsync(account, request.Nickname, KitFor(account), ct).ConfigureAwait(false)
            : await players.CreateAsync(account, request.Nickname, ct).ConfigureAwait(false);
        if (player is null)
        {
            lock (_gate) _created.Remove(account);
            await sender.SendAsync(session, new(1), ct).ConfigureAwait(false);
            return;
        }
        if (!atomicInventory && inventory is not null)
            await inventory.ResetStarterAsync(player.UserNum, KitFor(account), ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_pending.TryGetValue(session.Id, out var pending))
                _pending[session.Id] = pending with { Created = true };
        }
        await sender.SendAsync(session, new(0), ct).ConfigureAwait(false);
    }

    internal string Finish(ISession session)
    {
        lock (_gate)
        {
            var pending = RequirePending(session);
            if (!pending.Created) throw new InvalidOperationException("Character login arrived before creation.");
            _pending.Remove(session.Id);
            if (persistent) _created.Remove(pending.Account);
            return pending.Account;
        }
    }

    internal StarterKit KitFor(string account)
    {
        int character;
        lock (_gate)
        {
            if (!_created.TryGetValue(account, out var choice)) return starter;
            character = choice.Character;
        }
        return starter.WithInitialCharacter(character);
    }

    internal string? NicknameFor(string account)
    {
        lock (_gate) return _created.TryGetValue(account, out var choice) ? choice.Nickname : null;
    }

    internal void Remove(long sessionId)
    {
        lock (_gate)
        {
            if (_pending.Remove(sessionId, out var pending) && persistent) _created.Remove(pending.Account);
        }
    }
    public void Dispose() { lock (_gate) { _pending.Clear(); _created.Clear(); } }

    private Pending RequirePending(ISession session) => _pending.TryGetValue(session.Id, out var pending)
        ? pending : throw new InvalidOperationException("No character creation is open for this session.");

    // cn-279 sub_6E9850 limits the native name edit control to twelve UTF-16 units.
    private bool IsAvailable(string nickname) => !string.IsNullOrWhiteSpace(nickname)
        && nickname.Length <= 12 && !nickname.Any(char.IsControl)
        && !_created.Values.Any(choice => string.Equals(choice.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
}
