namespace FantasyTale.Gateway;

internal interface IGatewayPlugin
{
    ValueTask<GatewayActivation> ActivateAsync(GatewayRow row, GatewayContext context);
}

internal sealed record GatewayActivation(GatewayEndpoint Endpoint, Task Completion);

internal interface IGatewayControlHandler
{
    GatewayCommandResult Handle(GatewayCommand command);
}

/// <summary>Owns one row's effects and interface services for a statically linked AOT plugin.</summary>
internal sealed class GatewayContext(IGatewayLog log, CancellationToken parent) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent);
    private readonly List<IDisposable> _effects = [];
    private readonly Dictionary<Type, object> _services = new() { [typeof(IGatewayLog)] = log };
    private bool _disposed;
    internal CancellationToken Lifetime => _lifetime.Token;

    internal T Get<T>() where T : class => typeof(T).IsInterface && _services.TryGetValue(typeof(T), out var value)
        ? (T)value : throw new InvalidOperationException("Required gateway interface service is missing.");

    internal T? Find<T>() where T : class => typeof(T).IsInterface && _services.TryGetValue(typeof(T), out var value) ? (T)value : null;

    internal IDisposable Register<T>(T service) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!typeof(T).IsInterface || _services.Count >= 16 || !_services.TryAdd(typeof(T), service))
            throw new InvalidOperationException("Gateway service publication requires a unique interface within the row budget.");
        return new Removal(() => _services.Remove(typeof(T)));
    }

    internal void Effect(IDisposable effect)
    {
        if (_disposed || _effects.Count >= 32)
        {
            effect.Dispose();
            throw new InvalidOperationException("Gateway row effect budget exhausted or row already closed.");
        }
        _effects.Add(effect);
    }

    /// <summary>Cancels the row and releases registrations and sockets in reverse activation order.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        for (var i = _effects.Count - 1; i >= 0; i--)
            _effects[i].Dispose();
        _effects.Clear();
        _services.Clear();
        _lifetime.Dispose();
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

internal static class GatewayPluginCatalog
{
    internal static IGatewayPlugin Create(string id) => id switch
    {
        GatewayConstants.BubbleFighterCenterSelector => new BfCenterSelectorPlugin(),
        _ => throw new GatewayConfigurationException("Requested gateway plugin is not compiled into this executable."),
    };
}

internal interface IGatewayLog
{
    void Write(string message);
    IGatewayLog WithBudget(int maximumBytes);
}

/// <summary>Serializes ASCII stderr with a two-KiB default and separately bounded diagnostic row scopes.</summary>
internal sealed class GatewayLog(TextWriter writer) : IGatewayLog
{
    private readonly object _gate = new();
    private int _remaining = 2048;

    private GatewayLog(TextWriter writer, object gate, int maximumBytes) : this(writer)
    {
        _gate = gate;
        _remaining = maximumBytes;
    }

    /// <summary>Shares serialized stderr writes while reserving a separate validated row budget.</summary>
    public IGatewayLog WithBudget(int maximumBytes)
    {
        if (maximumBytes is < 2048 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        return new GatewayLog(writer, _gate, maximumBytes);
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            if (_remaining < 2) return;
            var count = Math.Min(Math.Min(message.Length, 254), _remaining - 1);
            const string exhausted = "Fantasy Tale Gateway log budget exhausted.\n";
            if (count + 1 + exhausted.Length > _remaining)
            {
                if (exhausted.Length <= _remaining) writer.Write(exhausted);
                _remaining = 0;
                writer.Flush();
                return;
            }
            Span<char> line = stackalloc char[count + 1];
            for (var i = 0; i < count; i++)
                line[i] = message[i] is >= ' ' and <= '~' ? message[i] : '?';
            line[count] = '\n';
            _remaining -= line.Length;
            writer.Write(line);
            writer.Flush();
        }
    }
}
