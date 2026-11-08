using System.Text.Json.Serialization;

namespace FantasyTale.Gateway;

/// <summary>Requires a parent-owned native bridge before publishing selector aliases.</summary>
internal sealed class SelectorIdentityOptions
{
    internal const string SupportedCapability = "bf-native-selector-identity-v1";
    internal const string PresentationCapability = "bf-native-selector-presentation-v2";

    /// <summary>Gets the exact native bridge capability explicitly selected by the Launcher.</summary>
    public required string Capability { get; init; }
    /// <summary>Gets the owning launch's lowercase 128-bit identifier.</summary>
    public required string LaunchId { get; init; }
    /// <summary>Gets the ordered visible choices, including unavailable choices with no network binding.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelectorPresentationEntry[]? Presentation { get; init; }

    internal void Validate(BubbleFighterBuildProfile profile)
    {
        if (Capability is not (SupportedCapability or PresentationCapability) || !profile.SupportsSelectorIdentity)
            throw new GatewayConfigurationException("This build or native selector-identity capability is unsupported.");
        if (LaunchId is not { Length: 32 } || LaunchId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new GatewayConfigurationException("Selector identity requires the owning lowercase 128-bit launch id.");
        if ((Capability == PresentationCapability) != (Presentation is not null))
            throw new GatewayConfigurationException("Only the presentation-v2 capability requires an explicit presentation list.");
    }

    internal void ValidatePresentation(CenterEntry[] centers)
    {
        if (Presentation is null) return;
        if (Presentation.Length < 2 || Presentation.Any(entry => entry is null))
            throw new GatewayConfigurationException("Native presentation requires at least two non-null choices.");
        var choices = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var nativeIds = new HashSet<int>();
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var active = centers.ToDictionary(center => center.Id, StringComparer.Ordinal);
        foreach (var choice in Presentation)
        {
            CenterEntry.ValidateIdentity(choice.Id, choice.Name);
            if (!choices.Add(choice.Id) || !names.Add(choice.Name) || choice.NativeCenterId <= 0 || !nativeIds.Add(choice.NativeCenterId))
                throw new GatewayConfigurationException("Presentation choice identities, names and positive native ids must be unique.");
            if (choice.BindingId is { } binding)
            {
                if (choice.UnavailableReason is not null || !bound.Add(binding) || !active.TryGetValue(binding, out var center)
                    || choice.Id != center.Id || choice.Name != center.Name || choice.NativeCenterId != center.NativeCenterId)
                    throw new GatewayConfigurationException("Available presentation choices must match one unique active binding exactly.");
            }
            else if (!GatewayConfiguration.ValidIdentifier(choice.UnavailableReason) || active.ContainsKey(choice.Id))
            {
                throw new GatewayConfigurationException("Unavailable presentation choices require a reason and cannot own a network binding.");
            }
        }
        if (bound.Count != centers.Length)
            throw new GatewayConfigurationException("Every active binding must occur exactly once in the presentation list.");
    }
}

/// <summary>Describes one native chooser row without creating a connection for an unavailable destination.</summary>
internal sealed class SelectorPresentationEntry
{
    /// <summary>Gets the stable selector choice identity.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the label shown in the native chooser.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the positive identity used only by native presentation.</summary>
    public required int NativeCenterId { get; init; }
    /// <summary>Gets the active entry identity, absent when this choice is unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BindingId { get; init; }
    /// <summary>Gets the bounded reason code for a disabled row, absent for an active choice.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnavailableReason { get; init; }
}

/// <summary>Binds ordered native directory keys to selector presentation and physical destinations.</summary>
internal sealed class SelectorIdentityMapping
{
    /// <summary>Gets the native bridge capability required to consume this mapping.</summary>
    public required string Capability { get; init; }
    /// <summary>Gets the owning launch identifier echoed from the parent contract.</summary>
    public required string LaunchId { get; init; }
    /// <summary>Gets the selected client build for which the connection keys were generated.</summary>
    public required string Build { get; init; }
    /// <summary>Gets one binding per wire directory row in exactly the same order.</summary>
    public required SelectorIdentityEntry[] Entries { get; init; }
    /// <summary>Gets the visible row order independently of the real connection directory in presentation-v2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelectorPresentationEntry[]? Presentation { get; init; }
}

internal sealed record SelectorIdentityEntry(
    string Id, string Name, string BackendId, int NativeCenterId, int Group, int SubKey, string Host, int Port);
