using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Protocol;

internal sealed class SessionFeatures : ISessionFeatures, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, Entry> _entries = [];
    private bool _closed;

    public SessionFeatures(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count { get { lock (_gate) return _entries.Count; } }

    public bool IsClosed { get { lock (_gate) return _closed; } }

    public IDisposable Register<T>(T feature) where T : class
    {
        ArgumentNullException.ThrowIfNull(feature);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var type = typeof(T);
            if (_entries.ContainsKey(type))
                throw new InvalidOperationException($"Session feature '{type.FullName}' is already registered.");
            if (_entries.Count >= Capacity)
                throw new InvalidOperationException($"Session feature capacity ({Capacity}) is exhausted.");
            var entry = new Entry(type, feature);
            var registration = new Registration(this, entry);
            _entries.Add(type, entry);
            return registration;
        }
    }

    public T Get<T>() where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _entries.TryGetValue(typeof(T), out var entry)
                ? (T)entry.Value!
                : throw new InvalidOperationException($"Session feature '{typeof(T).FullName}' is not registered.");
        }
    }

    public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
    {
        lock (_gate)
        {
            feature = _entries.TryGetValue(typeof(T), out var entry) ? (T)entry.Value! : null;
            return feature is not null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            foreach (var entry in _entries.Values)
                entry.Value = null;
            _entries.Clear();
        }
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Type, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.Type);
            entry.Value = null;
        }
    }

    private sealed class Entry(Type type, object value)
    {
        public Type Type { get; } = type;
        public object? Value { get; set; } = value;
    }

    private sealed class Registration(SessionFeatures owner, Entry entry) : IDisposable
    {
        private SessionFeatures? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(entry);
    }
}
