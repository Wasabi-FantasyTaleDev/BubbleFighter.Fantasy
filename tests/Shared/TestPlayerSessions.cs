using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Tests.Shared;

/// <summary>Unbounded in-memory session features for test sessions.</summary>
internal sealed class TestSessionFeatures : ISessionFeatures
{
    private readonly Dictionary<Type, object> _features = [];

    /// <summary>Features holding a player past the login burst, or none when <paramref name="userNum"/> is not positive.</summary>
    public static TestSessionFeatures WithPlayer(long userNum)
    {
        var features = new TestSessionFeatures();
        if (userNum > 0)
        {
            var player = new PlayerSession(new PlayerIdentity(checked((int)userNum), "player", "player", 1));
            player.MoveTo(SessionPhase.MainMenu);
            features.Register<IPlayerSession>(player);
        }
        return features;
    }

    public int Capacity => int.MaxValue;

    public int Count => _features.Count;

    public bool IsClosed => false;

    public IDisposable Register<T>(T feature) where T : class
    {
        if (!_features.TryAdd(typeof(T), feature))
            throw new InvalidOperationException($"Session feature '{typeof(T).FullName}' is already registered.");
        return new Removal(() => _features.Remove(typeof(T)));
    }

    public T Get<T>() where T : class =>
        _features.TryGetValue(typeof(T), out var value)
            ? (T)value
            : throw new InvalidOperationException($"Session feature '{typeof(T).FullName}' is not registered.");

    public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
    {
        feature = _features.TryGetValue(typeof(T), out var value) ? (T)value : null;
        return feature is not null;
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}

/// <summary>Plants the player state the lobby row registers on a real login.</summary>
internal static class TestPlayers
{
    /// <summary>
    /// Registers an <see cref="IPlayerSession"/> for <paramref name="userNum"/>. By default the
    /// login burst is finished (main menu), as it is when plaza and room requests arrive.
    /// </summary>
    public static IPlayerSession SetLobbyPlayer(this ISession session, long userNum, string nickname = "player",
        int level = 1, bool pastLoginBurst = true)
    {
        var player = new PlayerSession(new PlayerIdentity(checked((int)userNum), nickname, nickname, level));
        if (pastLoginBurst)
            player.MoveTo(SessionPhase.MainMenu);
        session.RegisterFeature<IPlayerSession>(player);
        return player;
    }
}
