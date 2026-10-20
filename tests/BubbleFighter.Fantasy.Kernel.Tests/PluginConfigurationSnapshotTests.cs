using System.Collections;
using System.Collections.ObjectModel;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks configuration admission, mutation isolation, and binding through public entry points.</summary>
public sealed class PluginConfigurationSnapshotTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Captures caller-owned maps, lists, and lineage before activation and reuses that snapshot on reload.</summary>
    [Fact]
    public async Task HostConstructionDetachesConfigurationBeforeActivationAndReload()
    {
        var input = new MutableInput();
        var row = Row(input.Config) with { Lineage = input.Lineage };
        var observations = new List<(PluginRow Row, SnapshotOptions Options)>();
        var host = Host(row, context => observations.Add((context.Row, context.Config.Bind<SnapshotOptions>())));
        try
        {
            input.Mutate();
            await host.LoadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            Assert.Single(observations);
            AssertOriginal(observations[0].Row, observations[0].Options);

            input.Numbers.Clear();
            input.Entries.Clear();
            input.Lineage.Clear();
            input.Config.Clear();
            await host.ReloadAsync(row.Id, default).AsTask().WaitAsync(Limit);

            Assert.Equal(2, observations.Count);
            AssertOriginal(observations[1].Row, observations[1].Options);
            Assert.Same(observations[0].Row, observations[1].Row);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Standalone contexts detach their exposed row and binder from caller-owned configuration.</summary>
    [Fact]
    public void StandaloneContextCapturesConfigurationAndLineageOnConstruction()
    {
        var input = new MutableInput();
        var row = Row(input.Config) with { Lineage = input.Lineage };
        var context = Context(row);
        try
        {
            input.Mutate();

            Assert.NotSame(row, context.Row);
            AssertOriginal(context.Row, context.Config.Bind<SnapshotOptions>());
            input.Config.Clear();
            AssertOriginal(context.Row, context.Config.Bind<SnapshotOptions>());
        }
        finally { context.Unwind(); }
    }

    /// <summary>Standalone binders accept copied IList sequences and return fresh arrays and lists on each bind.</summary>
    [Fact]
    public void StandaloneBinderCapturesOnceAndBindsEverySupportedSequenceShape()
    {
        var source = new ArrayList { 7, 11 };
        var mapping = new Dictionary<string, object?>
        { ["array"] = source, ["list"] = source, ["read_only"] = source };
        var config = new PluginConfig(Row(mapping));
        source[0] = 99;
        source.Add(101);
        mapping.Clear();

        var first = config.Bind<SequenceOptions>();
        Assert.Equal([7, 11], first.Array);
        Assert.Equal([7, 11], first.List);
        Assert.Equal([7, 11], first.ReadOnly);
        first.Array[0] = 17;
        first.List.Clear();

        var second = config.Bind<SequenceOptions>();
        Assert.Equal([7, 11], second.Array);
        Assert.Equal([7, 11], second.List);
        Assert.Equal([7, 11], second.ReadOnly);
    }

    /// <summary>Rejects mutation through mutable collection interfaces reachable from a context's public row.</summary>
    [Fact]
    public void ContextCollectionsRemainReadOnlyWhenCastToMutableInterfaces()
    {
        var input = new MutableInput();
        var context = Context(Row(input.Config) with { Lineage = input.Lineage });
        try
        {
            var mapping = context.Row.Config!;
            AssertMappingReadOnly(mapping);
            AssertMappingReadOnly(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(mapping["nested"]));
            var numbers = Assert.IsAssignableFrom<IList>(mapping["numbers"]);
            Assert.True(numbers.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => numbers[0] = 101);
            Assert.Throws<NotSupportedException>(() => numbers.Add(103));
            var entries = Assert.IsAssignableFrom<IList>(mapping["entries"]);
            Assert.Throws<NotSupportedException>(() => entries.Clear());
            AssertMappingReadOnly(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entries[0]));
            if (context.Row.Lineage is IList<string> lineage)
            {
                Assert.True(lineage.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => lineage[0] = "changed");
                Assert.Throws<NotSupportedException>(() => lineage.Add("changed"));
            }
            AssertOriginal(context.Row, context.Config.Bind<SnapshotOptions>());
        }
        finally { context.Unwind(); }
    }

    /// <summary>Retains plain immutable scalar types and values inside nested read-only mappings and IList sequences.</summary>
    [Fact]
    public void NestedPlainScalarsKeepTheirTypesAndValues()
    {
        object?[] scalars = [null, false, (byte)2, (sbyte)-3, (short)-4, (ushort)5, -6, 7U, -8L, ulong.MaxValue,
            (nint)(-9), (nuint)10, Int128.MinValue, UInt128.MaxValue,
            1.25F, 2.5D, 3.75M, 'x', "text", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), TimeSpan.FromSeconds(6), Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")];
        var nested = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["values"] = new ArrayList(scalars) });
        var context = Context(Row(new Dictionary<string, object?> { ["nested"] = nested }));
        try
        {
            var copy = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context.Row.Config!["nested"]);
            var values = Assert.IsAssignableFrom<IList>(copy["values"]);
            Assert.Equal(scalars.Length, values.Count);
            for (var index = 0; index < scalars.Length; index++)
            {
                Assert.Equal(scalars[index], values[index]);
                if (scalars[index] is { } scalar) Assert.IsType(scalar.GetType(), values[index]);
            }
        }
        finally { context.Unwind(); }
    }

    /// <summary>Preserves the distinction between an absent configuration mapping and an explicitly empty mapping.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullAndEmptyConfigurationRemainDistinct(bool absent)
    {
        var context = Context(Row(absent ? null : new Dictionary<string, object?>()));
        try
        {
            if (absent) Assert.Null(context.Row.Config);
            else Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context.Row.Config));
            Assert.Empty(context.Config.Bind<SequenceOptions>().Array);
        }
        finally { context.Unwind(); }
    }

    /// <summary>Rejects an absent lineage collection or null lineage entry before a standalone context is admitted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullLineageCollectionsAndEntriesFailWithTheirPath(bool nullEntry)
    {
        var row = Row(null) with { Lineage = nullEntry ? ["profile", null!] : null! };

        var failure = Assert.Throws<ConfigException>(() => Context(row));

        Assert.Contains(row.Id, failure.Message);
        Assert.Contains("lineage", failure.Message);
        if (nullEntry) Assert.Contains("[1]", failure.Message);
        Assert.Contains("must not be null", failure.Message);
    }

    /// <summary>Rejects mutable non-configuration objects during host admission without formatting their contents.</summary>
    [Fact]
    public void UnsupportedMutableValuesFailHostConstructionWithRowAndPathOnly()
    {
        const string content = "private-mutable-content";
        var row = Row(new Dictionary<string, object?>
        { ["nested"] = new Dictionary<string, object?> { ["payload"] = new StringBuilder(content) } });

        var failure = Assert.Throws<ConfigException>(() => Host(row, _ => throw new InvalidOperationException("Must not activate.")));

        Assert.Contains(row.Id, failure.Message);
        Assert.Contains("config.nested.payload", failure.Message);
        Assert.Contains("unsupported", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(content, failure.Message);
    }

    /// <summary>Rejects cycles in both supported container kinds with the path where the cycle reappears.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CyclesAreRejectedWithTheirConfigurationPath(bool sequence)
    {
        var mapping = new Dictionary<string, object?>();
        if (sequence)
        {
            var items = new ArrayList();
            items.Add(items);
            mapping["items"] = items;
        }
        else mapping["again"] = mapping;
        var row = Row(mapping);

        var failure = Assert.Throws<ConfigException>(() => new PluginConfig(row));

        Assert.Contains(row.Id, failure.Message);
        Assert.Contains(sequence ? "config.items" : "config.again", failure.Message);
        if (sequence) Assert.Contains("[0]", failure.Message);
        Assert.Contains("cycle", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies each occurrence of a repeated alias while accepting shared acyclic caller input.</summary>
    [Fact]
    public void RepeatedAliasesBecomeSeparateReadOnlyCopies()
    {
        var source = new ArrayList { 2, 4 };
        var shared = new Dictionary<string, object?> { ["values"] = source };
        var context = Context(Row(new Dictionary<string, object?> { ["left"] = shared, ["right"] = shared }));
        try
        {
            var left = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context.Row.Config!["left"]);
            var right = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context.Row.Config["right"]);
            var leftValues = Assert.IsAssignableFrom<IList>(left["values"]);
            var rightValues = Assert.IsAssignableFrom<IList>(right["values"]);
            Assert.NotSame(shared, left);
            Assert.NotSame(left, right);
            Assert.NotSame(source, leftValues);
            Assert.NotSame(leftValues, rightValues);
            source[0] = 100;
            shared.Clear();
            Assert.Equal(2, leftValues[0]);
            Assert.Equal(2, rightValues[0]);
        }
        finally { context.Unwind(); }
    }

    /// <summary>Stops nested configuration at the configured depth before binding can traverse it.</summary>
    [Fact]
    public void ConfigurationDepthIsBoundedDuringAdmission()
    {
        IReadOnlyDictionary<string, object?> nested = new Dictionary<string, object?> { ["value"] = 1 };
        for (var index = 0; index < 8; index++)
            nested = new Dictionary<string, object?> { ["nested"] = nested };
        var row = Row(nested, new() { ConfigurationDepthLimit = 2 });

        AssertAdmissionFailure(row, "depth limit", "config.nested");
    }

    /// <summary>Bounds each mapping, sequence, and lineage collection independently of the total node budget.</summary>
    [Theory]
    [InlineData("mapping", "config")]
    [InlineData("sequence", "config.items")]
    [InlineData("lineage", "lineage")]
    public void ConfigurationCollectionsAreBounded(string kind, string path)
    {
        var lifecycle = new PluginLifecycleOptions { ConfigurationCollectionLimit = 2 };
        var row = kind switch
        {
            "mapping" => Row(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2, ["c"] = 3 }, lifecycle),
            "sequence" => Row(new Dictionary<string, object?> { ["items"] = new ArrayList { 1, 2, 3 } }, lifecycle),
            _ => Row(null, lifecycle) with { Lineage = ["first", "second", "third"] }
        };

        AssertAdmissionFailure(row, "collection limit", path);
    }

    /// <summary>Accepts a sequence exactly at its per-collection limit without relying on total-node accounting.</summary>
    [Fact]
    public void CollectionLimitAcceptsExactlyTheConfiguredNumberOfItems()
    {
        var context = Context(Row(new Dictionary<string, object?> { ["items"] = new ArrayList { 3, 5 } },
            new() { ConfigurationCollectionLimit = 2 }));
        try
        {
            var items = Assert.IsAssignableFrom<IList>(context.Row.Config!["items"]);
            Assert.Equal(2, items.Count);
            Assert.Equal(3, items[0]);
            Assert.Equal(5, items[1]);
        }
        finally { context.Unwind(); }
    }

    /// <summary>Charges repeated aliases for every copied subtree instead of treating shared input as free.</summary>
    [Theory]
    [InlineData("node")]
    [InlineData("text")]
    public void RepeatedAliasesConsumeTheBudgetOnEveryOccurrence(string budget)
    {
        const string content = "private-text-repeated-for-each-alias-value";
        var shared = budget == "node"
            ? new Dictionary<string, object?> { ["values"] = Enumerable.Range(0, 32).Cast<object?>().ToList() }
            : new Dictionary<string, object?> { ["value"] = content };
        var count = budget == "node" ? 16 : 4;
        var lifecycle = budget == "node"
            ? new PluginLifecycleOptions { ConfigurationNodeLimit = 128 }
            : new PluginLifecycleOptions { ConfigurationTextLimit = 128 };
        var row = Row(new Dictionary<string, object?> { ["aliases"] = Enumerable.Repeat<object?>(shared, count).ToList() }, lifecycle);

        var failure = AssertAdmissionFailure(row, $"{budget} limit", "config.aliases");

        Assert.DoesNotContain(content, failure.Message);
    }

    /// <summary>Includes keys, lineage strings, and row metadata in the aggregate retained-text budget.</summary>
    [Theory]
    [InlineData("keys", "config")]
    [InlineData("lineage", "lineage")]
    [InlineData("metadata", "source")]
    public void TextBudgetIncludesAllRetainedTextSources(string kind, string path)
    {
        var lifecycle = new PluginLifecycleOptions { ConfigurationTextLimit = 128 };
        var text = new string('x', kind == "metadata" ? 130 : 110);
        var row = kind switch
        {
            "keys" => Row(new Dictionary<string, object?> { [text] = null }, lifecycle),
            "lineage" => Row(null, lifecycle) with { Lineage = [text] },
            _ => Row(null, lifecycle) with { Source = text }
        };

        var failure = AssertAdmissionFailure(row, "text limit", path);

        Assert.DoesNotContain(text, failure.Message);
    }

    /// <summary>Counts UTF-16 code units rather than Unicode scalar values when charging configuration text.</summary>
    [Fact]
    public void TextBudgetCountsUtf16CodeUnits()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001D11E", 40));
        var row = new PluginRow
        {
            Id = "r", Plugin = "p", Source = "s",
            Config = new Dictionary<string, object?> { ["value"] = text },
            Lifecycle = new() { ConfigurationTextLimit = 64 }
        };

        var failure = AssertAdmissionFailure(row, "text limit", "config.value");

        Assert.DoesNotContain(text, failure.Message);
    }

    /// <summary>Validates configuration budget settings before a standalone binder captures its input.</summary>
    [Theory]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationDepthLimit), 0)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationDepthLimit), 257)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationNodeLimit), 0)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationNodeLimit), 1000001)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationCollectionLimit), 0)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationCollectionLimit), 1000001)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationTextLimit), 0)]
    [InlineData(nameof(PluginLifecycleOptions.ConfigurationTextLimit), 67108865)]
    public void InvalidBudgetSettingsFailBeforeBinding(string property, int value)
    {
        var lifecycle = new PluginLifecycleOptions();
        typeof(PluginLifecycleOptions).GetProperty(property)!.SetValue(lifecycle, value);
        var row = Row(null, lifecycle);

        var failure = Assert.Throws<ConfigException>(() => new PluginConfig(row));

        Assert.Contains(row.Id, failure.Message);
        Assert.Contains(property, failure.Message);
    }

    private static ConfigException AssertAdmissionFailure(PluginRow row, string reason, string path)
    {
        var failure = Assert.Throws<ConfigException>(() => new PluginConfig(row));
        Assert.Contains(row.Id, failure.Message);
        Assert.Contains(path, failure.Message);
        Assert.Contains(reason, failure.Message, StringComparison.OrdinalIgnoreCase);
        return failure;
    }

    private static PluginRow Row(IReadOnlyDictionary<string, object?>? config, PluginLifecycleOptions? lifecycle = null) => new()
    { Id = "snapshot-row", Plugin = "snapshot", Source = "test", Config = config, Lifecycle = lifecycle ?? new() };

    private static PluginContext Context(PluginRow row) =>
        new(row, new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);

    private static PluginHost Host(PluginRow row, Action<IPluginContext> activate)
    {
        var declaration = new PluginAttribute(row.Plugin);
        var definition = new PluginDescriptor(declaration, new Factory(declaration, activate), "snapshot-test", "1.0.0", Versions);
        return new PluginHost([row], PluginCatalog.FromDefinitions([definition]), NullLoggerFactory.Instance);
    }

    private static void AssertOriginal(PluginRow row, SnapshotOptions options)
    {
        Assert.Equal(["profile", "base"], row.Lineage);
        Assert.Equal("original", options.Nested.Name);
        Assert.Equal([3, 5], options.Numbers);
        Assert.Equal("entry-original", Assert.Single(options.Entries).Name);
    }

    private static void AssertMappingReadOnly(IReadOnlyDictionary<string, object?> mapping)
    {
        if (mapping is IDictionary<string, object?> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic.Add("injected", true));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (mapping is IDictionary untyped)
        {
            Assert.True(untyped.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => untyped["injected"] = true);
        }
    }

    private sealed class MutableInput
    {
        internal Dictionary<string, object?> Nested { get; } = new() { ["name"] = "original" };
        internal ArrayList Numbers { get; } = new() { 3, 5 };
        internal Dictionary<string, object?> Entry { get; } = new() { ["name"] = "entry-original" };
        internal List<object?> Entries { get; }
        internal List<string> Lineage { get; } = ["profile", "base"];
        internal Dictionary<string, object?> Config { get; }

        internal MutableInput()
        {
            Entries = [new ReadOnlyDictionary<string, object?>(Entry)];
            Config = new() { ["nested"] = new ReadOnlyDictionary<string, object?>(Nested), ["numbers"] = Numbers, ["entries"] = Entries };
        }

        internal void Mutate()
        {
            Nested["name"] = "changed";
            Numbers[0] = 99;
            Numbers.Add(101);
            Entry["name"] = "changed-entry";
            Lineage[0] = "changed-profile";
            Lineage.Add("extra");
            Config["nested"] = new Dictionary<string, object?> { ["name"] = "replacement" };
            Config["unexpected"] = true;
        }
    }

    private sealed class SnapshotOptions
    {
        /// <summary>Gets or sets the nested mapping bound from the admitted configuration.</summary>
        public NestedOptions Nested { get; set; } = new();
        /// <summary>Gets or sets numeric sequence values bound from the copied IList.</summary>
        public IReadOnlyList<int> Numbers { get; set; } = [];
        /// <summary>Gets or sets a sequence of nested options copied from caller-owned mappings.</summary>
        public IReadOnlyList<NestedOptions> Entries { get; set; } = [];
    }

    private sealed class NestedOptions
    {
        /// <summary>Gets or sets the scalar value used to detect mutations of nested mappings.</summary>
        public string Name { get; set; } = "";
    }

    private sealed class SequenceOptions
    {
        /// <summary>Gets or sets an array produced by binding the captured sequence.</summary>
        public int[] Array { get; set; } = [];
        /// <summary>Gets or sets a mutable list produced by binding the captured sequence.</summary>
        public List<int> List { get; set; } = [];
        /// <summary>Gets or sets a read-only list interface bound from the captured sequence.</summary>
        public IReadOnlyList<int> ReadOnly { get; set; } = [];
    }

    private sealed class Factory(PluginAttribute declaration, Action<IPluginContext> activate) : IPluginExecutionFactory
    {
        /// <summary>Keeps the fixture independent of native module coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Creates a fresh execution for each load or reload of the admitted entry.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IPluginExecution>(new Execution(declaration, activate));
    }

    private sealed class Execution(PluginAttribute declaration, Action<IPluginContext> activate) : IPluginExecution
    {
        /// <summary>Returns the unchanged service declaration for every fixture generation.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the release version used by the static test definition.</summary>
        public string Version => "1.0.0";
        /// <summary>References the abstraction version loaded by this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies the fixture as an execution without a native plugin type.</summary>
        public Type? ImplementationType => null;
        /// <summary>Inspects the admitted row and binds options during real hosted activation.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            activate(context);
            return ValueTask.CompletedTask;
        }
        /// <summary>Completes cleanup for this fixture, which acquires no external resources.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
