using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FantasyTale.Gateway;

internal sealed class GatewayConfiguration
{
    /// <summary>Gets the required version of the parent control contract.</summary>
    public required int SchemaVersion { get; init; }
    /// <summary>Gets the statically compiled plugin rows enabled for this launch.</summary>
    public required GatewayRow[] Rows { get; init; }
    /// <summary>Gets the deadline for finishing a control message after its first byte arrives.</summary>
    public int ControlFrameTimeoutMs { get; set; } = 5000;

    internal void Validate()
    {
        if (SchemaVersion != GatewayConstants.ControlSchemaVersion)
            throw new GatewayConfigurationException("Unsupported control schema version.");
        if (Rows is null || Rows.Length is < 1 or > 8 || Rows.Any(row => row is null))
            throw new GatewayConfigurationException("A launch requires 1..8 non-null plugin rows.");
        if (ControlFrameTimeoutMs is < 100 or > 30000)
            throw new GatewayConfigurationException("control_frame_timeout_ms must be 100..30000.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (!ValidIdentifier(row.Id) || !ids.Add(row.Id))
                throw new GatewayConfigurationException("Row ids must be unique bounded ASCII identifiers.");
            if (!ValidIdentifier(row.Plugin) || row.Config.ValueKind != JsonValueKind.Object)
                throw new GatewayConfigurationException("Each row requires a plugin identifier and a config object.");
        }
    }

    internal static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

internal sealed class GatewayRow
{
    /// <summary>Gets the unique identity of this capability instance.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the compiled plugin to activate.</summary>
    public required string Plugin { get; init; }
    /// <summary>Gets the plugin's bounded, strictly deserialized configuration.</summary>
    public required JsonElement Config { get; init; }
}

internal sealed class CenterSelectorOptions
{
    /// <summary>Gets the Launcher-selected executable build identity, checked again on the wire.</summary>
    public required string Build { get; init; }
    /// <summary>Gets selector choices and physical destinations; explicit native identity mode requires a parent bridge.</summary>
    public required CenterEntry[] Centers { get; init; }
    /// <summary>Gets the explicit launch-bound native bridge contract for selector labels and aliases.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelectorIdentityOptions? SelectorIdentity { get; init; }
    /// <summary>Gets the opt-in native automatic endpoint and the parent's expiring eligible worker snapshot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutomaticCenterOptions? AutomaticEntry { get; init; }
    /// <summary>Gets the maximum accept wait, or zero to let the parent enforce its finite launch budget.</summary>
    public int ConnectTimeoutMs { get; set; } = 30000;
    /// <summary>Gets the absolute lifetime of an accepted selector connection, or zero to let the parent own its lifetime.</summary>
    public int SelectionTimeoutMs { get; set; }
    /// <summary>Gets the first-connect deadline and the grace period for accepted unavailable auxiliaries after directory release.</summary>
    public int HandshakeTimeoutMs { get; set; } = 10000;
    /// <summary>Gets the receive buffer size for the single bootstrap socket.</summary>
    public int ReceiveBufferBytes { get; set; } = 4096;
    /// <summary>Gets the maximum decoded frame body accepted on the bootstrap socket.</summary>
    public int MaxPayloadBytes { get; set; } = 16384;
    /// <summary>Gets the largest first-connect string accepted, in UTF-16 code units.</summary>
    public int MaxStringCharacters { get; set; } = 1024;
    /// <summary>Gets the bounded operating-system backlog before the first connection is accepted.</summary>
    public int ListenBacklog { get; set; } = 1;
    /// <summary>Gets whether local selector sockets emit endpoint-qualified lifecycle events.</summary>
    public bool ConnectionTrace { get; set; }
    /// <summary>Gets the ASCII stderr byte budget for this row when connection tracing is enabled.</summary>
    public int ConnectionTraceMaxBytes { get; set; } = 65536;
    /// <summary>Gets the shared native status probe budget before freezing endpoints; zero retains parent-only probing.</summary>
    public int NativeProbeTimeoutMs { get; set; }
    /// <summary>Gets the maximum simultaneous native status probes within their shared deadline.</summary>
    public int NativeProbeParallelism { get; set; } = 4;

    internal BubbleFighterBuildProfile Validate()
    {
        if (ConnectionTraceMaxBytes is < 2048 or > 1048576)
            throw new GatewayConfigurationException("connection_trace_max_bytes must be 2048..1048576.");
        if (!GatewayConfiguration.ValidIdentifier(Build))
            throw new GatewayConfigurationException("build must be a bounded ASCII identifier.");
        var profile = CompiledBuildCatalog.Resolve(Build);
        if (NativeProbeTimeoutMs is < 0 or > 5000 || NativeProbeTimeoutMs is > 0 and < 100
            || NativeProbeParallelism is < 1 or > 16)
            throw new GatewayConfigurationException("Native probe timeout must be zero or 100..5000 ms, with parallelism 1..16.");
        if (Centers is null || Centers.Length < 1 || Centers.Any(center => center is null))
            throw new GatewayConfigurationException("centers requires at least one non-null entry.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new HashSet<EndPoint>();
        SelectorIdentity?.Validate(profile);
        var backends = new Dictionary<string, EndPoint>(StringComparer.Ordinal);
        var endpointBackends = new Dictionary<EndPoint, string>();
        var nativeIds = new HashSet<int>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var hasUnavailable = Centers.Any(center => center.UnavailableCapacity is not null);
        if (NativeProbeTimeoutMs > 0 && (profile.Automatic is null || SelectorIdentity is not null
            || Centers.Any(center => center.StatusCapacity is null || center.NativeCenterId is not > 0)))
            throw new GatewayConfigurationException("Native probing requires status support, supplied capacities and positive identities without an identity bridge.");
        foreach (var center in Centers)
        {
            var endpoint = center.Validate();
            if (center.UnavailableCapacity is not null && (profile.Automatic is null || SelectorIdentity is not null))
                throw new GatewayConfigurationException("Unavailable Centers require native status support without a selector identity bridge.");
            if (AutomaticEntry is not null && !endpoints.Add(endpoint))
                throw new GatewayConfigurationException("Automatic Center requires distinct physical worker endpoints.");
            if (!identities.Add(center.Id))
                throw new GatewayConfigurationException("Center identities must be unique.");
            if (SelectorIdentity is null && AutomaticEntry is null)
            {
                if (hasUnavailable || NativeProbeTimeoutMs > 0)
                {
                    if (center.NativeCenterId is not null && (center.NativeCenterId is not > 0 || !nativeIds.Add(center.NativeCenterId.Value))
                        || center.BackendId is not null && !GatewayConfiguration.ValidIdentifier(center.BackendId))
                        throw new GatewayConfigurationException("Native status directories require distinct positive supplied identities and valid optional backend identities.");
                    if (!endpoints.Add(endpoint))
                        throw new GatewayConfigurationException("Native status directories require distinct physical endpoints.");
                    continue;
                }
                if (center.BackendId is not null || center.NativeCenterId is not null || !endpoints.Add(endpoint))
                    throw new GatewayConfigurationException("Native aliases require an explicit selector-identity bridge contract.");
                continue;
            }
            if (!GatewayConfiguration.ValidIdentifier(center.BackendId) || center.NativeCenterId is not > 0
                || !nativeIds.Add(center.NativeCenterId.Value) || !labels.Add(center.Name))
                throw new GatewayConfigurationException("Selector identities require a backend id, unique positive native id and unique label.");
            var backend = center.BackendId!;
            if ((backends.TryGetValue(backend, out var previous) && !previous.Equals(endpoint))
                || (endpointBackends.TryGetValue(endpoint, out var owner) && owner != backend))
                throw new GatewayConfigurationException("Backend identities and IPv4 endpoints must have a one-to-one mapping per launch.");
            backends[backend] = endpoint;
            endpointBackends[endpoint] = backend;
        }
        SelectorIdentity?.ValidatePresentation(Centers);
        AutomaticEntry?.Validate(profile, this);
        if (SelectorIdentity is { Presentation: null } && Centers.Length < 2)
            throw new GatewayConfigurationException("A native selector-identity directory requires at least two entries.");
        if (ConnectTimeoutMs is < 0 or > 600000 || ConnectTimeoutMs is > 0 and < 100 || SelectionTimeoutMs is < 0 or > 600000
            || HandshakeTimeoutMs is < 100 or > 60000 || SelectionTimeoutMs is > 0 && HandshakeTimeoutMs > SelectionTimeoutMs)
            throw new GatewayConfigurationException("Connection deadlines are outside the supported range.");
        if (ReceiveBufferBytes is < 256 or > 16384 || MaxPayloadBytes is < 96 or > 65525
            || MaxStringCharacters is < 1 or > 4096 || ListenBacklog is < 1 or > 8)
            throw new GatewayConfigurationException("Socket and frame limits are outside the supported range.");
        return profile;
    }

    /// <summary>Resolves the label shown for the automatic selector entry.</summary>
    internal string ResolveAutomaticName(AutomaticCenterOptions automatic)
    {
        if (Centers is null || automatic is null || automatic.AdmittedCenterIds is null)
            throw new GatewayConfigurationException("Automatic Center label resolution requires a complete selector configuration.");

        // The parent's explicit display metadata takes precedence over the ordered pool anchor.
        var anchorId = automatic.AdmittedCenterIds.FirstOrDefault();
        var anchor = anchorId is null ? null : Centers.FirstOrDefault(center => center.Id == anchorId);
        var name = automatic.ShortName ?? anchor?.ShortName ?? anchor?.Name;
        if (name is not null)
            return $"自动选择（{name}）";

        return automatic.Name;
    }
}

internal sealed class CenterEntry
{
    /// <summary>Gets the upstream catalog identity, which is not a native wire identity.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the catalog label, projected into the chooser only through the explicit parent native bridge.</summary>
    public required string Name { get; init; }
    /// <summary>Gets an optional compact label used when the automatic selector names its anchor region.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShortName { get; init; }
    /// <summary>Gets the resolved destination IPv4 literal. The Launcher owns DNS resolution.</summary>
    public required string Host { get; init; }
    /// <summary>Gets the destination game TCP port.</summary>
    public required int Port { get; init; }
    /// <summary>Gets the physical backend identity, shared only by explicit aliases of the same endpoint.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackendId { get; init; }
    /// <summary>Gets the positive chooser identity projected by the native bridge, independent of backend status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NativeCenterId { get; init; }

    /// <summary>Gets the actual target capacity when the parent has proven this launch's destination unreachable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UnavailableCapacity { get; init; }
    /// <summary>Gets the catalog capacity used for a local unavailable status if a native readiness probe fails.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCapacity { get; init; }

    internal EndPoint Validate()
    {
        ValidateIdentity(Id, Name);
        ValidateShortName(ShortName);
        if (StatusCapacity is < 1 or > int.MaxValue / 2)
            throw new GatewayConfigurationException("Native status capacity must be 1..1073741823.");
        if (UnavailableCapacity is < 1 or > int.MaxValue / 2 || UnavailableCapacity is not null && NativeCenterId is not > 0)
            throw new GatewayConfigurationException("Unavailable Centers require capacity 1..1073741823 and a positive native identity.");
        if (Port is < 1 or > 65535)
            throw new GatewayConfigurationException("Center port must be 1..65535.");
        // A failed DNS lookup still has an identity; only the local status endpoint reaches the native directory.
        if (UnavailableCapacity is not null && Host is { Length: > 0 and <= 253 }
            && Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && Uri.CheckHostName(Host) == UriHostNameType.Dns)
            return new DnsEndPoint(Host, Port);
        if (Host is null || Host.Length > 15 || Host.Split('.').Length != 4
            || Host.Any(c => c is not (>= '0' and <= '9' or '.'))
            || !IPAddress.TryParse(Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || address.ToString() != Host || address.Equals(IPAddress.Any) || address.GetAddressBytes()[0] >= 224)
            throw new GatewayConfigurationException("Center host must be a canonical unicast IPv4 literal.");
        return new IPEndPoint(address, Port);
    }

    internal static void ValidateIdentity(string id, string name)
    {
        if (id is not { Length: > 0 and <= 128 } || !ValidText(id)
            || string.IsNullOrWhiteSpace(name) || name.Length > GatewayConstants.MaxCenterNameChars || !ValidText(name))
            throw new GatewayConfigurationException("Center identities and catalog names must be bounded and printable.");
    }

    internal static void ValidateShortName(string? value)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value)
            || value.Length > GatewayConstants.MaxCenterNameChars || !ValidText(value)))
            throw new GatewayConfigurationException("Center short_name must be bounded and printable when supplied.");
    }

    private static bool ValidText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i]))
                return false;
        }
        return true;
    }
}

internal sealed class GatewayConfigurationException(string message) : Exception(message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GatewayConfiguration))]
[JsonSerializable(typeof(CenterSelectorOptions))]
[JsonSerializable(typeof(GatewayCommand))]
[JsonSerializable(typeof(GatewayReady))]
[JsonSerializable(typeof(GatewayCommandResult))]
internal partial class GatewayJsonContext : JsonSerializerContext
{
    internal static GatewayJsonContext Strict { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16,
    });
}

internal sealed class GatewayCommand
{
    /// <summary>Gets the command discriminator: stop or refresh-automatic.</summary>
    public required string Type { get; init; }
    /// <summary>Gets the configured plugin row receiving a candidate refresh.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RowId { get; init; }
    /// <summary>Gets the launch identity frozen in the row's initial configuration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LaunchId { get; init; }
    /// <summary>Gets the original authority grant identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GrantId { get; init; }
    /// <summary>Gets the parent's strictly increasing revision, independent of source heartbeat revisions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Revision { get; init; }
    /// <summary>Gets the full replacement active snapshot; an empty array withdraws all automatic candidates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutomaticCenterCandidate[]? Candidates { get; init; }
}

internal sealed class GatewayCommandResult
{
    /// <summary>Gets the candidate refresh acknowledgement discriminator.</summary>
    public string Type { get; init; } = "automatic-refresh-result";
    /// <summary>Gets the requested row identity for correlation.</summary>
    public string? RowId { get; init; }
    /// <summary>Gets the requested parent revision for correlation.</summary>
    public long? Revision { get; init; }
    /// <summary>Gets accepted, rejected or finished without changing an already selected worker.</summary>
    public required string Status { get; init; }
    /// <summary>Gets a bounded diagnostic code without server addresses or credentials.</summary>
    public required string Reason { get; init; }
}

internal sealed class GatewayReady
{
    /// <summary>Gets the control schema version understood by the process.</summary>
    public int SchemaVersion { get; init; } = GatewayConstants.ControlSchemaVersion;
    /// <summary>Gets the readiness discriminator.</summary>
    public string Type { get; init; } = "ready";
    /// <summary>Gets the result for every activated plugin row.</summary>
    public required GatewayEndpoint[] Rows { get; init; }
}

internal sealed record GatewayEndpoint(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("build")] string Build)
{
    /// <summary>Gets the exact native directory bindings that the parent must install before starting the game.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelectorIdentityMapping? SelectorIdentity { get; init; }
}
