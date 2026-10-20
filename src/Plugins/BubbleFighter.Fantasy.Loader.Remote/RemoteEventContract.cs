using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>One declared event operation; dispatch mode and result identity are part of its permission.</summary>
internal sealed record RemoteEventContract(Type Event, string Mode, Type Result)
{
    internal string Key => $"{ContractSchema.TypeName(Event)}|{Mode}|{ContractSchema.TypeName(Result)}";

    internal static IReadOnlyList<RemoteEventContract> Parse(JsonElement definition, string field, string where)
    {
        if (!definition.TryGetProperty(field, out var entries)) return [];
        if (entries.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"{where}: '{field}' must be an array of event declarations.");
        var result = new List<RemoteEventContract>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var contract = Resolve(entry, field == "publishes");
            if (!keys.Add(contract.Key)) throw new ConfigException($"{where}: repeated {field} declaration '{contract.Key}'.");
            result.Add(contract);
        }
        return result;
    }

    internal static RemoteEventContract Resolve(JsonElement entry, bool publishing)
    {
        static string Read(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
                ? text.GetString()! : throw new ConfigException($"Event declaration requires a nonempty '{name}'.");
        var name = Read(entry, "type");
        var mode = Read(entry, "mode");
        var allowed = publishing ? new[] { "emit", "serial", "parallel", "waterfall", "bail" }
            : new[] { "sync", "async", "waterfall", "bail" };
        if (!allowed.Contains(mode)) throw new ConfigException($"Unknown event {(publishing ? "publish" : "subscription")} mode '{mode}'.");
        var types = ContractSchema.EventTypes.Where(type => type.Name == name || ContractSchema.TypeName(type) == name).ToArray();
        if (types.Length != 1) throw new ConfigException($"Unknown or ambiguous event type '{name}'.");
        if (new ContractSchema().Unsupported(types[0], []) is { } reason)
            throw new ConfigException($"Event '{name}' requires a dedicated representation: {reason}");
        var result = typeof(void);
        if (mode is "waterfall" or "bail")
        {
            var resultName = Read(entry, "result_type");
            var candidates = ContractSchema.EventResultTypes.Where(type => type.Name == resultName || ContractSchema.TypeName(type) == resultName).ToArray();
            if (candidates.Length != 1) throw new ConfigException($"Unknown or ambiguous event result '{resultName}'; use its full schema name.");
            result = candidates[0];
            if (mode == "bail" && result.IsValueType)
                throw new ConfigException("Bail results must be reference types; null means continue to the next listener.");
        }
        else if (entry.TryGetProperty("result_type", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
            throw new ConfigException($"Event mode '{mode}' does not return a value.");
        return new(types[0], mode, result);
    }

    internal JsonElement Write(object? value, Type type, SessionMap sessions, bool nullable = false)
    {
        var json = JsonSerializer.SerializeToElement(value, type, RemoteServiceInvoker.SessionOptions(sessions));
        _ = Read(json, type, sessions, nullable);
        return json;
    }

    internal object? Read(JsonElement value, Type type, SessionMap sessions, bool nullable = false)
    {
        if (type == typeof(void))
        {
            if (value.ValueKind != JsonValueKind.Null) throw new JsonException("An event notification must return null.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null && !nullable) throw new JsonException("This event value must not be null.");
        if (value.ValueKind != JsonValueKind.Null)
            RemoteServiceInvoker.ValidateNullability(value, type, null, unknownIsNonNull: true);
        return value.Deserialize(type, RemoteServiceInvoker.SessionOptions(sessions));
    }
}
