using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed class PresencePeak
{
    // The fixed checkpoint contains one fingerprint and one nonnegative Int32, never player data.
    private const int MaxBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly string _path;
    private readonly string _fingerprint;
    private int _value;

    internal PresencePeak(PresenceGroupOptions group)
    {
        _path = Path.GetFullPath(group.PeakStatePath);
        _fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            group.Id + "\n" + group.PersistentDomainId.ToLowerInvariant() + "\n" + string.Join("\n", group.Nodes.Order(StringComparer.Ordinal)))));
        if (!File.Exists(_path)) return;
        using var file = File.OpenRead(_path);
        if (file.Length > MaxBytes) throw new InvalidDataException("Presence peak checkpoint exceeds its format limit.");
        var saved = JsonSerializer.Deserialize<Checkpoint>(file, Json)
            ?? throw new InvalidDataException("Presence peak checkpoint is empty.");
        if (saved.Schema != 1 || saved.Fingerprint != _fingerprint || saved.Peak < 0)
            throw new InvalidDataException("Presence peak checkpoint belongs to another membership or has an invalid value.");
        _value = saved.Peak;
    }

    internal int Observe(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count <= _value) return _value;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var pending = _path + ".pending";
        try
        {
            using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new Checkpoint(1, _fingerprint, count), Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, _path, overwrite: true);
            _value = count;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return _value;
    }

    private sealed record Checkpoint(int Schema, string Fingerprint, int Peak);
}
