using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.BanPick;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>
/// The server-wide document: the configured one, or the administrators' saved edit when the state file exists. A save
/// is validated, written to a temporary file and moved over the state file, so a crash leaves the old or the new
/// document, never half of one. Readers take an immutable snapshot and never wait for a save.
/// </summary>
internal sealed class BanPickPolicyStore : IBanPickPolicyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly string? _statePath;
    private readonly int _maxBytes;
    private BanPickPolicyState _current;

    internal BanPickPolicyStore(BanPickPolicy configured, string? statePath, int maxBytes)
    {
        _maxBytes = maxBytes;
        _statePath = string.IsNullOrWhiteSpace(statePath) ? null : Path.GetFullPath(statePath);
        BanPickPolicyRules.Validate(configured);
        _current = new(0, configured);
        if (_statePath is null || !File.Exists(_statePath)) return;
        using var input = File.OpenRead(_statePath);
        if (input.Length > _maxBytes) throw new InvalidDataException($"The saved ban-pick document exceeds {_maxBytes} bytes.");
        var saved = JsonSerializer.Deserialize<BanPickPolicyState>(input, Json)
            ?? throw new InvalidDataException("The saved ban-pick document is empty.");
        if (saved.Revision < 1) throw new InvalidDataException("The saved ban-pick document has an invalid revision.");
        BanPickPolicyRules.Validate(saved.Policy);
        _current = saved;
    }

    /// <summary>Raised after a save with the new state; the room rules re-read it on their next check anyway.</summary>
    internal event Action<BanPickPolicyState>? Changed;

    public BanPickPolicyState Current => Volatile.Read(ref _current);

    public ValueTask<BanPickPolicyState> SaveAsync(long expectedRevision, BanPickPolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_statePath is null) throw new NotSupportedException("Ban-pick editing requires a configured state_path.");
        BanPickPolicyRules.Validate(policy);
        BanPickPolicyState next;
        lock (_gate)
        {
            if (_current.Revision != expectedRevision) throw new BanPickPolicyException("ban_pick_revision_conflict");
            next = new(checked(_current.Revision + 1), policy);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            if (bytes.Length > _maxBytes) throw new BanPickPolicyException("ban_pick_too_large");
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                File.Move(temporary, _statePath, overwrite: true);
                Volatile.Write(ref _current, next);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke(next);
        return ValueTask.FromResult(next);
    }
}
