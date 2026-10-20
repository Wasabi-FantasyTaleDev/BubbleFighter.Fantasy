using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Composes dialect providers into a replaceable, bounded deployment registry.</summary>
[Plugin("core.wire-dialects", Description = "Wire dialect registry populated by deployment providers.",
    OptionalMany = [typeof(IWireDialectProvider)], Provides = [typeof(IWireDialectRegistry)])]
public sealed class WireDialectRegistryPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<WireDialectRegistryOptions>();
        var registry = new WireDialectRegistry(options.MaxDialects);
        foreach (var provider in context.Services.GetAll<IWireDialectProvider>())
            foreach (var dialect in provider.Dialects)
                context.Effect(registry.Register(dialect));
        context.Effect(context.Services.Register<IWireDialectRegistry>(registry));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Deployment limits for the wire dialect registry.</summary>
public sealed class WireDialectRegistryOptions
{
    /// <summary>Gets or sets the maximum simultaneously registered dialects.</summary>
    [Range(1, 4096)]
    public int MaxDialects { get; set; } = 64;
}

internal sealed class WireDialectRegistry(int capacity) : IWireDialectRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _entries = new(StringComparer.Ordinal);

    public IDisposable Register(IWireDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        var profile = dialect.Profile;
        var build = new ClientBuild(profile.Region, profile.ClientVersionMajor, profile.ResourceVersion);
        if (string.IsNullOrWhiteSpace(dialect.Id) || dialect.Id.Length > 128 || dialect.Id != profile.Id)
            throw new ConfigException("Dialect id must match its profile id and contain 1..128 characters.");
        if (dialect.MaxFrameOverheadBytes is < 0 or > 65536)
            throw new ConfigException($"Dialect '{dialect.Id}' frame overhead must be in 0..65536 bytes.");
        lock (_gate)
        {
            if (_entries.Count >= capacity)
                throw new ConfigException($"Wire dialect registry reached max_dialects={capacity}.");
            if (_entries.ContainsKey(dialect.Id) || _entries.Values.Any(entry => entry.Build == build))
                throw new ConfigException($"Duplicate wire dialect id '{dialect.Id}' or client build {build}.");
            var entry = new Registration(this, dialect.Id, dialect, build);
            _entries.Add(entry.Id, entry);
            return entry;
        }
    }

    public IReadOnlyList<IWireDialect> All
    {
        get { lock (_gate) return _entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Dialect).ToArray(); }
    }

    public IWireDialect Get(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var entry) ? entry.Dialect
                : throw new ConfigException($"No wire dialect registered under id '{id}'. Enable a dialect provider row.");
    }

    public IWireDialect Resolve(ClientBuild build)
    {
        lock (_gate)
            return _entries.Values.FirstOrDefault(entry => entry.Build == build)?.Dialect
                ?? throw new ConfigException($"No wire dialect registered for client build {build}. Enable a matching dialect provider row.");
    }

    private sealed class Registration(WireDialectRegistry owner, string id, IWireDialect dialect, ClientBuild build) : IDisposable
    {
        public string Id { get; } = id;
        public IWireDialect Dialect { get; } = dialect;
        public ClientBuild Build { get; } = build;
        public void Dispose()
        {
            lock (owner._gate)
                if (owner._entries.TryGetValue(Id, out var current) && ReferenceEquals(current, this))
                    owner._entries.Remove(Id);
        }
    }
}
