namespace BubbleFighter.Fantasy.Cluster;

// The clan relay: nodes of one verified account domain hand each other clan notices for members connected elsewhere.
// Notices are transient (the clan itself lives in the shared database), so the directory keeps no member state and
// a reader that falls behind or meets a restarted directory skips ahead instead of receiving a snapshot.
internal sealed partial class ServerDirectory
{
    internal const int ClanReadLimit = 256;
    private readonly Lock _clanGate = new();
    private readonly List<ClanRecord> _clanLog = [];
    private TaskCompletionSource _clanPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _clanSequence;

    private sealed record ClanRecord(long Sequence, string Scope, string Node, ClanWireNotice Notice);

    internal int PublishClan(string nodeId, string token, long epoch, ClanWirePost post)
    {
        if (!_options.ClanFederation) return 404;
        if (post.Notices is null || post.Notices.Length > _options.MaxClanNotices || !post.Notices.All(ClanTransport.Valid)) return 400;
        lock (_clanGate)
        {
            // The plaza's scope rule: a current lease whose heartbeat proved the database its data scope declares.
            if (PlazaScope(nodeId, token, epoch, out _) is not { } scope) return 409;
            foreach (var notice in post.Notices)
                _clanLog.Add(new(++_clanSequence, scope, nodeId, notice));
            if (_clanLog.Count > _options.MaxClanNotices) _clanLog.RemoveRange(0, _clanLog.Count - _options.MaxClanNotices);
            var pulse = _clanPulse;
            _clanPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pulse.TrySetResult();
            return 204;
        }
    }

    internal async ValueTask<(int Status, ClanWireBatch? Batch)> ReadClanAsync(string nodeId, string token, long epoch,
        string hub, long after, TimeSpan wait, CancellationToken ct)
    {
        if (!_options.ClanFederation) return (404, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        while (true)
        {
            Task pulse;
            lock (_clanGate)
            {
                if (PlazaScope(nodeId, token, epoch, out _) is not { } scope) return (409, null);
                var oldest = _clanLog.Count == 0 ? _clanSequence + 1 : _clanLog[0].Sequence;
                // A new reader, a restarted directory or a reader behind the retained log starts at the newest notice.
                if (hub != _incarnation || after > _clanSequence || after < oldest - 1)
                    return (200, new(_incarnation, _clanSequence, []));
                // One read carries at most ClanReadLimit notices (about 3 MiB at the notice bounds); the cursor then
                // stops at the last one carried and the reader comes straight back for the rest.
                var pending = _clanLog.Where(record => record.Sequence > after).ToArray();
                var carried = pending.Take(ClanReadLimit).ToArray();
                var cursor = carried.Length < pending.Length ? carried[^1].Sequence : _clanSequence;
                var notices = carried.Where(record => record.Scope == scope && record.Node != nodeId).Select(record => record.Notice).ToArray();
                if (notices.Length > 0 || cursor != _clanSequence || deadline.IsCancellationRequested)
                    return (200, new(_incarnation, cursor, notices));
                pulse = _clanPulse.Task;
            }
            try { await pulse.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) { }
        }
    }
}
