namespace FantasyTale.Gateway;

/// <summary>Describes a real local automatic Center and the parent-admitted worker snapshot.</summary>
internal sealed class AutomaticCenterOptions
{
    internal const string SupportedCapability = "bf-native-automatic-center-v1";
    /// <summary>Gets the explicit experimental native redirect contract.</summary>
    public required string Capability { get; init; }
    /// <summary>Gets the catalog identity of the local automatic choice.</summary>
    public required string Id { get; init; }
    /// <summary>Gets its native status label, subject to the client's font coverage.</summary>
    public required string Name { get; init; }
    /// <summary>Gets optional parent-supplied region text for the automatic selector label.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ShortName { get; init; }
    /// <summary>Gets the parent-derived pool capacity used solely for a native unselectable status when no healthy worker exists.</summary>
    public int? UnavailableCapacity { get; init; }
    /// <summary>Gets its native identity, distinct from every actual worker identity.</summary>
    public required int NativeCenterId { get; init; }
    /// <summary>Gets the deterministic policy: lowest RTT, then lowest occupancy ratio, then ordinal identity.</summary>
    public required string SelectionPolicy { get; init; }
    /// <summary>Gets the parent launch identity that binds all candidate refreshes.</summary>
    public required string LaunchId { get; init; }
    /// <summary>Gets the immutable issuer grant identity admitted by the parent.</summary>
    public required string GrantId { get; init; }
    /// <summary>Gets the original absolute grant expiry; refreshes cannot extend it.</summary>
    public required DateTimeOffset GrantExpiresAtUtc { get; init; }
    /// <summary>Gets the immutable admitted worker identity pool, independent of current health observations.</summary>
    public required string[] AdmittedCenterIds { get; init; }
    /// <summary>Gets the authority-admitted physical workers, with heartbeat measurements supplied by the parent.</summary>
    public required AutomaticCenterCandidate[] Candidates { get; init; }
    /// <summary>Gets the deadline for the source connection to release after the redirect burst.</summary>
    public int RedirectTimeoutMs { get; set; } = 10000;
    /// <summary>Gets the maximum number of native login adapter words to validate without retaining credentials.</summary>
    public int MaxLoginAdapters { get; set; } = 32;

    internal void Validate(BubbleFighterBuildProfile profile, CenterSelectorOptions selector)
    {
        if (Capability != SupportedCapability || profile.Automatic is null || selector.SelectorIdentity is not null)
            throw new GatewayConfigurationException("Automatic Center requires its audited build and cannot use a selector identity bridge.");
        CenterEntry.ValidateIdentity(Id, Name);
        CenterEntry.ValidateShortName(ShortName);
        var resolvedName = selector.ResolveAutomaticName(this);
        CenterEntry.ValidateIdentity(Id, resolvedName);
        if (UnavailableCapacity is < 1 or > int.MaxValue / 2)
            throw new GatewayConfigurationException("Automatic Center unavailable_capacity must be 1..1073741823 when supplied.");
        if (NativeCenterId < 1 || selector.Centers.Any(entry => entry.Id == Id || entry.Name == resolvedName || entry.NativeCenterId == NativeCenterId))
            throw new GatewayConfigurationException("Automatic Center requires a distinct identity.");
        if (SelectionPolicy != "lowest-rtt-then-load" || RedirectTimeoutMs is < 100 or > 60000 || MaxLoginAdapters is < 1 or > 256)
            throw new GatewayConfigurationException("Automatic Center policy or resource limits are invalid.");
        if (LaunchId is not { Length: 32 } || !LaunchId.All(char.IsAsciiHexDigit)
            || GrantId is not { Length: > 0 and <= 128 } || GrantId.Any(c => c is < '!' or > '~')
            || GrantExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new GatewayConfigurationException("Automatic Center requires a valid launch binding and unexpired grant.");
        if (AdmittedCenterIds is not { Length: > 0 } || AdmittedCenterIds.Length > selector.Centers.Length
            || AdmittedCenterIds.Distinct(StringComparer.Ordinal).Count() != AdmittedCenterIds.Length
            || AdmittedCenterIds.Any(id => !selector.Centers.Any(center => center.Id == id)))
            throw new GatewayConfigurationException("Automatic Center requires a bounded distinct admitted worker pool.");
        if (Candidates is null || Candidates.Length > AdmittedCenterIds.Length || Candidates.Any(entry => entry is null))
            throw new GatewayConfigurationException("Automatic Center requires a bounded non-null candidate snapshot.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in Candidates)
        {
            if (!seen.Add(candidate.CenterId) || !AdmittedCenterIds.Contains(candidate.CenterId, StringComparer.Ordinal)
                || selector.Centers.Any(center => center.Id == candidate.CenterId && center.UnavailableCapacity is not null)
                || !ValidCandidate(candidate))
                throw new GatewayConfigurationException("Automatic Center candidate identity or measurements are invalid.");
        }
    }

    internal bool ValidCandidate(AutomaticCenterCandidate candidate) =>
        candidate.RttUs is >= 0 and <= 60000000 && candidate.Users >= 0 && candidate.Capacity >= 1 && candidate.Users <= candidate.Capacity
        && candidate.StatusRevision is { Length: > 0 and <= 128 } && candidate.StatusRevision.All(c => c is >= '!' and <= '~');

    internal AutomaticCenterCandidate Select(DateTimeOffset now)
        => (now < GrantExpiresAtUtc ? Select(Candidates) : null)
            ?? throw new GatewayConfigurationException("Automatic Center has no healthy worker with capacity under the current grant.");

    internal static AutomaticCenterCandidate? Select(AutomaticCenterCandidate[] candidates)
    {
        AutomaticCenterCandidate? best = null;
        foreach (var candidate in candidates)
        {
            // Heartbeat timestamps come from another clock and must not veto a reported healthy worker.
            if (!candidate.Healthy || candidate.Users >= candidate.Capacity) continue;
            var load = best is null ? 0 : (long)candidate.Users * best.Capacity - (long)best.Users * candidate.Capacity;
            if (best is null || candidate.RttUs < best.RttUs || candidate.RttUs == best.RttUs
                && (load < 0 || load == 0 && string.CompareOrdinal(candidate.CenterId, best.CenterId) < 0))
                best = candidate;
        }
        return best;
    }
}

/// <summary>Contains one heartbeat measurement for a worker already admitted by the parent's authority policy.</summary>
internal sealed class AutomaticCenterCandidate
{
    /// <summary>Gets the identity of an existing physical destination in centers.</summary>
    public required string CenterId { get; init; }
    /// <summary>Gets whether the parent's current health and admission checks passed.</summary>
    public required bool Healthy { get; init; }
    /// <summary>Gets the parent's measured round-trip time in microseconds.</summary>
    public required int RttUs { get; init; }
    /// <summary>Gets the actual measured worker population.</summary>
    public required int Users { get; init; }
    /// <summary>Gets the worker's reported positive admission capacity.</summary>
    public required int Capacity { get; init; }
    /// <summary>Gets the source-reported measurement expiry for diagnostics; it does not gate login.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    /// <summary>Gets the source's actual heartbeat observation time for diagnostics, independent of the local clock.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }
    /// <summary>Gets the bounded opaque source revision; ordering is provided only by the parent's snapshot revision.</summary>
    public required string StatusRevision { get; init; }
}
