using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Handlers;

// The heartbeat reads published rooms, so rooms cannot acquire their migration source as a startup dependency.
internal sealed class RoomMigrationEndpoint(string clientBuild) : IRemoteRoomMigrationEndpoint, IRemoteRoomMigration, IDisposable
{
    private readonly Lock _gate = new();
    private Binding? _binding;
    private bool _disposed;
    public string ClientBuild => clientBuild;

    public IAsyncDisposable Bind(IRemoteRoomMigration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (source.ClientBuild != ClientBuild) throw new InvalidOperationException("Migration source belongs to another client build.");
            if (_binding is not null) throw new InvalidOperationException("The room migration source is already bound or draining.");
            return _binding = new Binding(this, source);
        }
    }

    public async ValueTask<RoomJoinRefusal?> BeginAsync(ISession session, RoomBrowserSelection selection, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Acquire() is not { } binding) return RoomJoinRefusal.NoRoom;
        try { return await binding.Source.BeginAsync(session, selection, password, cancellationToken).ConfigureAwait(false); }
        finally { Release(binding); }
    }

    public async ValueTask ReturnHomeAsync(ISession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Acquire() is not { } binding) return;
        try { await binding.Source.ReturnHomeAsync(session, cancellationToken).ConfigureAwait(false); }
        finally { Release(binding); }
    }

    private Binding? Acquire()
    {
        lock (_gate)
        {
            if (_disposed || _binding is not { Closing: false } binding) return null;
            binding.Calls = checked(binding.Calls + 1);
            return binding;
        }
    }

    private void Release(Binding binding)
    {
        lock (_gate)
        {
            binding.Calls--;
            Complete(binding);
        }
    }

    private ValueTask Unbind(Binding binding)
    {
        lock (_gate)
        {
            binding.Closing = true;
            Complete(binding);
            return new(binding.Drained.Task);
        }
    }

    private void Complete(Binding binding)
    {
        if (!binding.Closing || binding.Calls != 0) return;
        if (ReferenceEquals(_binding, binding)) _binding = null;
        binding.Drained.TrySetResult();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_binding is not null) throw new InvalidOperationException("Migration source must drain before its rooms unload.");
            _disposed = true;
        }
    }

    private sealed class Binding(RoomMigrationEndpoint owner, IRemoteRoomMigration source) : IAsyncDisposable
    {
        internal IRemoteRoomMigration Source { get; } = source;
        internal int Calls { get; set; }
        internal bool Closing { get; set; }
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync() => owner.Unbind(this);
    }
}
