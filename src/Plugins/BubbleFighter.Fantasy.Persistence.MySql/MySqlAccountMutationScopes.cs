using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Keeps each asynchronous caller attached to its original game session rather than the account's latest owner.</summary>
internal sealed class MySqlAccountMutationScopes(Func<IAccountOwnership, AccountWriteProof?> read) : IAccountMutationScopes
{
    private readonly AsyncLocal<Frame?> _current = new();

    public IDisposable Bind(ISession session, IAccountOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownership);
        var proof = read(ownership) ?? throw new InvalidOperationException("The account hold does not belong to this live authority provider.");
        if (session.Connection.IsClosed) throw new InvalidOperationException("A closed game session cannot acquire mutation authority.");
        var binding = new Binding(this, ownership, proof);
        var registration = session.RegisterFeature(binding);
        // Login enters dispatch before admission; that flow may capture its first binding, never a replacement.
        if (_current.Value is { } frame && ReferenceEquals(frame.Session, session)) frame.Admit(binding);
        return registration;
    }

    public IDisposable Enter(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TryFeature<Binding>(out var binding);
        var frame = new Frame(session, _current.Value, binding);
        _current.Value = frame;
        return new Scope(this, frame);
    }

    internal AccountWriteProof? Read(long userNum)
    {
        var frame = _current.Value;
        var session = frame?.Session;
        var binding = frame?.Binding;
        return session is not null && binding is not null && !session.Connection.IsClosed
            && session.TryFeature<Binding>(out var registered) && ReferenceEquals(registered, binding)
            && ReferenceEquals(binding.Provider, this) && binding.Proof.UserNum == userNum
            && ReferenceEquals(read(binding.Ownership), binding.Proof) ? binding.Proof : null;
    }

    private sealed record Binding(MySqlAccountMutationScopes Provider, IAccountOwnership Ownership, AccountWriteProof Proof);

    private sealed class Frame(ISession session, Frame? previous, Binding? binding)
    {
        private Binding? _binding = binding;
        internal ISession Session { get; } = session;
        internal Frame? Previous { get; } = previous;
        internal Binding? Binding => Volatile.Read(ref _binding);
        internal void Admit(Binding admitted) => Interlocked.CompareExchange(ref _binding, admitted, null);
    }

    private sealed class Scope(MySqlAccountMutationScopes owner, Frame frame) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            if (!ReferenceEquals(owner._current.Value, frame))
                throw new InvalidOperationException("Account mutation scopes must be removed in their original asynchronous flow and nesting order.");
            owner._current.Value = frame.Previous;
            _disposed = true;
        }
    }
}
