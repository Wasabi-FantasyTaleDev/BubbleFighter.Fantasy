using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.Companion;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>The server-wide row's side shared by every build's <c>banpick.room</c> row.</summary>
internal sealed class BanPickHub(BanPickSettings settings, IBanPickPolicyStore store, ICompanionHub? companion) : IBanPickHub
{
    private readonly Lock _gate = new();
    private IBanPickScope[] _scopes = [];

    public BanPickSettings Settings => settings;

    public IBanPickPolicyStore Store => store;

    public ICompanionHub? Companion => companion;

    public IDisposable Register(IBanPickScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            if (_scopes.Any(current => current.ClientBuild == scope.ClientBuild))
                throw new InvalidOperationException($"A ban-pick room row for '{scope.ClientBuild}' is already registered.");
            Volatile.Write(ref _scopes, [.. _scopes, scope]);
        }
        return new Registration(() =>
        {
            lock (_gate) Volatile.Write(ref _scopes, [.. _scopes.Where(current => !ReferenceEquals(current, scope))]);
        });
    }

    /// <summary>Finds the build whose room seats the game session.</summary>
    internal (IBanPickScope Scope, ISession Session)? Seated(long sessionId)
    {
        foreach (var scope in Volatile.Read(ref _scopes))
            if (scope.FindSeated(sessionId) is { } session) return (scope, session);
        return null;
    }

    /// <summary>Finds a build's rules.</summary>
    internal IBanPickScope? ForBuild(string build) => Volatile.Read(ref _scopes).FirstOrDefault(scope => scope.ClientBuild == build);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
