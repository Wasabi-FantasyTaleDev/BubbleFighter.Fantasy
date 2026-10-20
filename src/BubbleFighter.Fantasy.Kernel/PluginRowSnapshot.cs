using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Detaches an admitted row from caller-owned configuration and lineage collections.</summary>
internal static class PluginRowSnapshot
{
    internal static PluginRow Capture(PluginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        PluginContext.ValidateLifecycle(row);
        return new CaptureState(row).Capture();
    }

    private sealed class CaptureState(PluginRow row)
    {
        private readonly HashSet<object> _ancestors = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _path = [];
        private int _nodes;
        private int _text;

        internal PluginRow Capture()
        {
            Metadata(row.Id, "id", required: true);
            Metadata(row.Plugin, "plugin", required: true);
            Metadata(row.Source, "source", required: true);
            Metadata(row.Version, "version");
            Metadata(row.ParentId, "parent_id");
            if (row.ClientBuild is { } build) Metadata(build.Region, "client_build.region", required: true);

            _path.Add("lineage");
            var lineage = CopyStrings(row.Lineage, unique: false);
            _path.Clear();
            _path.Add("isolated_services");
            var isolation = CopyStrings(row.IsolatedServices, unique: true);
            _path.Clear();
            _path.Add("config");
            var config = row.Config is null ? null : (IReadOnlyDictionary<string, object?>)Copy(row.Config, 1)!;
            return row with { Config = config, Lineage = lineage, IsolatedServices = isolation };
        }

        private void Metadata(string? value, string path, bool required = false)
        {
            _path.Add(path);
            if (value is null && required) throw Failure("must not be null");
            if (value is not null) Text(value.Length);
            _path.Clear();
        }

        private ReadOnlyCollection<string> CopyStrings(IReadOnlyList<string>? source, bool unique)
        {
            if (source is null) throw Failure("must not be null");
            Node(1);
            var count = CollectionCount(source.Count);
            var copy = new List<string>();
            var seen = unique ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var entry in source)
            {
                NextItem(copy.Count, count);
                _path.Add($"[{copy.Count.ToString(CultureInfo.InvariantCulture)}]");
                if (entry is null) throw Failure("entries must not be null");
                if (seen is not null && (!seen.Add(entry) || string.IsNullOrWhiteSpace(entry)))
                    throw Failure("isolated services must contain unique nonblank names");
                copy.Add((string)Copy(entry, 2)!);
                _path.RemoveAt(_path.Count - 1);
            }
            CompleteCollection(copy.Count, count);
            if (unique) copy.Sort(StringComparer.Ordinal);
            return Array.AsReadOnly(copy.ToArray());
        }

        private object? Copy(object? value, int depth)
        {
            Node(depth);
            switch (value)
            {
                case null:
                    return null;
                case string text:
                    Text(text.Length);
                    return text;
                case char:
                    Text(1);
                    return value;
                case bool or sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint
                    or Int128 or UInt128 or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid:
                    return value;
                case IReadOnlyDictionary<string, object?> map:
                    return CopyMapping(map, depth);
                case IList list:
                    return CopySequence(list, depth);
                default:
                    throw Failure("unsupported configuration value type");
            }
        }

        private ReadOnlyDictionary<string, object?> CopyMapping(IReadOnlyDictionary<string, object?> source, int depth)
        {
            if (!_ancestors.Add(source)) throw Failure("configuration contains a reference cycle");
            try
            {
                var count = CollectionCount(source.Count);
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in source)
                {
                    NextItem(copy.Count, count);
                    if (pair.Key is null) throw Failure("mapping keys must not be null");
                    Text(pair.Key.Length);
                    _path.Add(pair.Key);
                    if (copy.ContainsKey(pair.Key)) throw Failure("mapping contains a duplicate key");
                    copy.Add(pair.Key, Copy(pair.Value, depth + 1));
                    _path.RemoveAt(_path.Count - 1);
                }
                CompleteCollection(copy.Count, count);
                return new ReadOnlyDictionary<string, object?>(copy);
            }
            finally { _ancestors.Remove(source); }
        }

        private ReadOnlyCollection<object?> CopySequence(IList source, int depth)
        {
            if (!_ancestors.Add(source)) throw Failure("configuration contains a reference cycle");
            try
            {
                var count = CollectionCount(source.Count);
                var copy = new List<object?>();
                foreach (var item in source)
                {
                    NextItem(copy.Count, count);
                    _path.Add($"[{copy.Count.ToString(CultureInfo.InvariantCulture)}]");
                    copy.Add(Copy(item, depth + 1));
                    _path.RemoveAt(_path.Count - 1);
                }
                CompleteCollection(copy.Count, count);
                return Array.AsReadOnly(copy.ToArray());
            }
            finally { _ancestors.Remove(source); }
        }

        private void Node(int depth)
        {
            if (depth > row.Lifecycle.ConfigurationDepthLimit) throw Failure("configuration depth limit exceeded");
            if (_nodes == row.Lifecycle.ConfigurationNodeLimit) throw Failure("configuration node limit exceeded");
            _nodes++;
        }

        private void Text(int length)
        {
            if (length > row.Lifecycle.ConfigurationTextLimit - _text) throw Failure("configuration text limit exceeded");
            _text += length;
        }

        private int CollectionCount(int count)
        {
            if (count < 0) throw Failure("collection reports a negative count");
            if (count > row.Lifecycle.ConfigurationCollectionLimit) throw Failure("configuration collection limit exceeded");
            return count;
        }

        private void NextItem(int copied, int expected)
        {
            if (copied >= row.Lifecycle.ConfigurationCollectionLimit) throw Failure("configuration collection limit exceeded");
            if (copied >= expected) throw Failure("collection enumeration exceeds its reported count");
        }

        private void CompleteCollection(int copied, int expected)
        {
            if (copied != expected) throw Failure("collection enumeration differs from its reported count");
        }

        private ConfigException Failure(string message)
        {
            var id = row.Id;
            if (id is not null && id.Length > row.Lifecycle.ConfigurationTextLimit)
                id = id[..row.Lifecycle.ConfigurationTextLimit] + "...";
            return new ConfigException(id ?? "<null>", [$"{string.Join(".", _path)}: {message}."]);
        }
    }
}
