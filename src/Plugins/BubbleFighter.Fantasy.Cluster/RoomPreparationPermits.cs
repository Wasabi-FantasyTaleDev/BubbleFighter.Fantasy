using System.Security.Cryptography;
using System.Text;

namespace BubbleFighter.Fantasy.Cluster;

internal sealed partial class ServerDirectory
{
    private readonly Lock _preparationGate = new();
    private readonly Dictionary<string, PendingPreparation> _preparations = new(StringComparer.Ordinal);

    internal RoomPreparationPermit? IssueRoomPreparation(string source, RoomPreparationRequest request)
    {
        if (!_options.RoomPreparationPermits || !request.IsValid()) return null;
        lock (_preparationGate)
        {
            ExpirePreparations();
            if (_preparations.Count >= _options.MaxRoomPreparationPermits
                || AuthorizePreparation(source, request) is not { } claim) return null;
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _preparations.Add(PreparationKey(token), new(claim, _time.GetTimestamp()));
            return new(token, _time.GetUtcNow().ToUniversalTime().AddMilliseconds(_options.RoomPreparationPermitLifetimeMs), claim);
        }
    }

    internal RoomPreparationClaim? ClaimRoomPreparation(string destination, RoomPreparationRedemption request)
    {
        if (!_options.RoomPreparationPermits || !request.IsValid()) return null;
        lock (_preparationGate)
        {
            ExpirePreparations();
            var key = PreparationKey(request.Token);
            if (!_preparations.TryGetValue(key, out var pending) || pending.Claim.DestinationNodeId != destination) return null;
            var claim = pending.Claim;
            if (!_nodes.TryGetValue(destination, out var node)) return null;
            lock (node.Gate)
                if (node.Lease is not { } lease || lease.Token != request.LeaseToken || lease.Epoch != claim.DestinationEpoch)
                    return null;
            // Redeeming the directory permit authorizes preparation only. The destination still reserves
            // the actual seat and authenticates the exact native game connection independently.
            _preparations.Remove(key);
            return RevalidatePreparation(claim) ? claim : null;
        }
    }

    internal bool CancelRoomPreparation(string source, RoomPreparationRedemption request)
    {
        if (!_options.RoomPreparationPermits || !request.IsValid()) return false;
        lock (_preparationGate)
        {
            ExpirePreparations();
            var key = PreparationKey(request.Token);
            if (!_preparations.TryGetValue(key, out var pending) || pending.Claim.SourceNodeId != source
                || !_nodes.TryGetValue(source, out var node)) return false;
            lock (node.Gate)
                if (node.Lease is not { } lease || !Fresh(lease) || lease.Token != request.LeaseToken
                    || lease.Epoch != pending.Claim.SourceEpoch) return false;
            return _preparations.Remove(key);
        }
    }

    private RoomPreparationClaim? AuthorizePreparation(string source, RoomPreparationRequest request)
    {
        if (source == request.DestinationNodeId || !_nodes.TryGetValue(source, out var from)
            || !_nodes.TryGetValue(request.DestinationNodeId, out var to)) return null;
        var first = string.CompareOrdinal(source, request.DestinationNodeId) < 0 ? from : to;
        var second = ReferenceEquals(first, from) ? to : from;
        lock (first.Gate)
        lock (second.Gate)
        {
            if (from.Lease is not { } sourceLease || sourceLease.Token != request.SourceLeaseToken
                || sourceLease.Epoch != request.SourceEpoch) return null;
            return ResolvePreparation(from, to, request.SourceEpoch, request.DestinationEpoch, request.Build,
                request.RoomId, request.InstanceId, request.UserNumber);
        }
    }

    private bool RevalidatePreparation(RoomPreparationClaim claim)
    {
        if (!_nodes.TryGetValue(claim.SourceNodeId, out var from) || !_nodes.TryGetValue(claim.DestinationNodeId, out var to)) return false;
        var first = string.CompareOrdinal(claim.SourceNodeId, claim.DestinationNodeId) < 0 ? from : to;
        var second = ReferenceEquals(first, from) ? to : from;
        lock (first.Gate)
        lock (second.Gate)
            return ResolvePreparation(from, to, claim.SourceEpoch, claim.DestinationEpoch, claim.Build,
                claim.RoomId, claim.InstanceId, claim.UserNumber) == claim;
    }

    private RoomPreparationClaim? ResolvePreparation(Node from, Node to, long sourceEpoch, long destinationEpoch,
        string build, int roomId, Guid instance, int user)
    {
        if (from.Lease is not { } source || to.Lease is not { } destination
            || source.Epoch != sourceEpoch || destination.Epoch != destinationEpoch
            || !Fresh(source) || !Fresh(destination) || destination.Draining
            || !source.Builds.Any(state => state.Build == build && state.Ready)
            || destination.Builds.FirstOrDefault(state => state.Build == build && state.Ready) is not { } population
            || to.Options.Endpoints.FirstOrDefault(endpoint => endpoint.Build == build) is not { } endpoint
            || population.Players >= endpoint.Capacity
            || from.Options.DataScope.Length == 0 || from.Options.DataScope != to.Options.DataScope
            || from.Options.JoinScope.Length == 0 || from.Options.JoinScope != to.Options.JoinScope
            || !from.Options.RoomDirectories.Intersect(to.Options.RoomDirectories, StringComparer.Ordinal).Any()
            || from.Options.Authentication is not { Mode: "master-issued", PolicyMode: "authority" } authority
            || to.Options.Authentication is not { Mode: "master-issued", PolicyMode: "authority" } targetAuthority
            || authority.AuthorityId != targetAuthority.AuthorityId || authority.ApiUrl != targetAuthority.ApiUrl
            || !Guid.TryParseExact(source.PersistentDomainId, "N", out var sourceDomain) || sourceDomain == Guid.Empty
            || !Guid.TryParseExact(destination.PersistentDomainId, "N", out var destinationDomain) || sourceDomain != destinationDomain
            || _expectedDomains.TryGetValue(from.Options.DataScope, out var expected)
                && !string.Equals(source.PersistentDomainId, expected, StringComparison.OrdinalIgnoreCase)
            || !destination.Rooms.Any(room => room.ClientBuild == build && room.RoomId == roomId
                && room.InstanceId == instance && room.Listing is not null && room.Phase == "Waiting"
                && room.Players > 0 && room.Players < room.Capacity)) return null;
        return new(from.Options.Id, source.Epoch, to.Options.Id, destination.Epoch, build, roomId, instance, user,
            from.Options.DataScope, from.Options.JoinScope, sourceDomain.ToString("N"), authority.AuthorityId,
            authority.ApiUrl, endpoint.Host, endpoint.Port);
    }

    private void ExpirePreparations()
    {
        var lifetime = TimeSpan.FromMilliseconds(_options.RoomPreparationPermitLifetimeMs);
        foreach (var key in _preparations.Where(pair => _time.GetElapsedTime(pair.Value.IssuedAt) >= lifetime)
            .Select(pair => pair.Key).ToArray()) _preparations.Remove(key);
    }

    private static string PreparationKey(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private sealed record PendingPreparation(RoomPreparationClaim Claim, long IssuedAt);
}

internal sealed record RoomPreparationRequest(string SourceLeaseToken, long SourceEpoch, string DestinationNodeId,
    long DestinationEpoch, string Build, int RoomId, Guid InstanceId, int UserNumber)
{
    internal bool IsValid() => SourceLeaseToken is { Length: > 0 and <= 128 } && SourceEpoch > 0 && DestinationEpoch > 0
        && DestinationNodeId is { Length: > 0 and <= 64 } && RoomDirectoryClientOptions.Identifier(Build)
        && RoomId is > 0 and <= ushort.MaxValue && InstanceId != Guid.Empty && UserNumber > 0;
    public override string ToString() => "Room preparation request (credentials omitted)";
}

internal sealed record RoomPreparationRedemption(string LeaseToken, string Token)
{
    internal bool IsValid() => LeaseToken is { Length: > 0 and <= 128 }
        && Token is { Length: 64 } && Token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public override string ToString() => "Room preparation redemption (credentials omitted)";
}

internal sealed record RoomPreparationPermit(string Token, DateTimeOffset ExpiresAtUtc, RoomPreparationClaim Claim)
{
    public override string ToString() => "Room preparation permit (credentials omitted)";
}

internal sealed record RoomPreparationClaim(string SourceNodeId, long SourceEpoch, string DestinationNodeId, long DestinationEpoch,
    string Build, int RoomId, Guid InstanceId, int UserNumber, string DataScope, string JoinScope, string PersistentDomainId,
    string AuthorityId, string AuthorityUrl, string DestinationHost, int DestinationPort);
