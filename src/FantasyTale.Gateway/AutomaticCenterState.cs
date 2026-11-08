namespace FantasyTale.Gateway;

/// <summary>Replaces bounded candidate snapshots under one launch grant and freezes selection atomically.</summary>
internal sealed class AutomaticCenterState(AutomaticCenterOptions options, IEnumerable<string>? unavailableCenterIds = null,
    IEnumerable<string>? nativeProbeFailures = null) : IGatewayControlHandler
{
    private readonly object _gate = new();
    private readonly HashSet<string> _members = options.AdmittedCenterIds.ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _unavailable = (unavailableCenterIds ?? []).ToHashSet(StringComparer.Ordinal);
    // Native endpoint bindings are fixed for this launch; later TCP-only snapshots cannot revive a failed status probe.
    private readonly HashSet<string> _nativeProbeFailures = (nativeProbeFailures ?? []).ToHashSet(StringComparer.Ordinal);
    private AutomaticCenterCandidate[] _candidates = options.Candidates.ToArray();
    private long _revision;
    private bool _finished;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Validates and atomically replaces only measurements belonging to the original launch grant.</summary>
    public GatewayCommandResult Handle(GatewayCommand command)
    {
        lock (_gate)
        {
            var reason = command.LaunchId != options.LaunchId || command.GrantId != options.GrantId ? "binding"
                : command.Revision is not > 0 ? "revision" : null;
            if (reason is not null) return Result("rejected", reason);
            if (_finished) return Result("finished", "retired");
            var revision = command.Revision.GetValueOrDefault();
            if (revision <= _revision) return Result("rejected", "revision");
            _revision = revision;
            if (DateTimeOffset.UtcNow >= options.GrantExpiresAtUtc) return Result("rejected", "grant-expired");
            if (command.Candidates is not { } candidates || candidates.Length > _members.Count)
                return Result("rejected", "candidates");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
                if (candidate is null || !_members.Contains(candidate.CenterId) || _unavailable.Contains(candidate.CenterId)
                    || !seen.Add(candidate.CenterId) || !options.ValidCandidate(candidate))
                    return Result("rejected", "candidates");
            _candidates = candidates.ToArray();
            var previous = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
            return Result("accepted", "updated");
        }

        GatewayCommandResult Result(string status, string reason) => new()
        {
            RowId = command.RowId, Revision = command.Revision, Status = status, Reason = reason,
        };
    }

    internal AutomaticCenterCandidate Select(DateTimeOffset now, bool commit)
    {
        lock (_gate)
        {
            var selected = !_finished && now < options.GrantExpiresAtUtc
                ? AutomaticCenterOptions.Select(_candidates.Where(candidate => !_nativeProbeFailures.Contains(candidate.CenterId)).ToArray()) : null;
            if (selected is null) throw new AutomaticCenterUnavailableException();
            if (commit) _finished = true;
            return selected;
        }
    }

    internal void Finish()
    {
        lock (_gate)
        {
            _finished = true;
            _changed.TrySetResult();
        }
    }

    internal async Task<AutomaticCenterCandidate> WaitSelectAsync(bool commit, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_finished || now >= options.GrantExpiresAtUtc) throw new AutomaticCenterUnavailableException();
                var selected = AutomaticCenterOptions.Select(_candidates);
                if (selected is not null)
                {
                    if (commit) _finished = true;
                    return selected;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class AutomaticCenterUnavailableException : Exception { }
