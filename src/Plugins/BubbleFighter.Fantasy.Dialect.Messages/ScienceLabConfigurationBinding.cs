using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Connects the audited native laboratory-open request to the feature's enchantment configuration.</summary>
internal sealed class ScienceLabConfigurationBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<ScienceLabConfigurationRequested>, IMessageSender<ScienceLabConfigurationPublished>
{
    private const string RequestName = "gcpReqSciendLabConfig";
    private const string ReplyName = "gspRepSciendLabConfig";

    /// <summary>Gets the game protocol whose laboratory configuration this binding publishes.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the exact build and both native routes before publishing any registration.</summary>
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        Validate();
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ScienceLabConfigurationRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<ScienceLabConfigurationPublished>>(this));
        context.Effect(router.Register(protocol, opcodes.C2S(RequestName),
            async (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                // kr-1256 sub_DF0990 sends an empty 141 only while kr-1256 sub_DF0FC0 is waiting for configuration.
                if (!packet.Payload.IsEmpty)
                    throw new PacketFormatException("Science-lab configuration request must have an empty body.", 0);
                if (!await messages.DispatchAsync(protocol, session, new ScienceLabConfigurationRequested(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No science-lab configuration handler is installed.");
            }));
    }

    /// <summary>Writes all native configuration fields while supplying only the enabled enchantment material IDs.</summary>
    public ValueTask SendAsync(ISession session, ScienceLabConfigurationPublished message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Validate();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.EnchantMaterials);
        // kr-1256 sub_DFA000 takes the first card; kr-1256 sub_DD63A0 requires four remaining kit counters.
        if (message.EnchantMaterials.Count != 5 || message.EnchantMaterials.Any(id => id <= 0)
            || message.EnchantMaterials.Distinct().Count() != 5)
            throw new ArgumentException("Science-lab configuration requires one card and four distinct positive kit item IDs.", nameof(message));
        if (message.AdditiveItemId <= 0 || message.EnchantMaterials.Contains(message.AdditiveItemId))
            throw new ArgumentException("Science-lab additive must be a separate positive item ID.", nameof(message));
        var values = new Dictionary<string, object?>
        {
            ["enchant_materials"] = message.EnchantMaterials.Cast<object?>().ToArray(),
            ["additive_item_id"] = message.AdditiveItemId,
        };
        if (message.Modification is { } modification)
        {
            if (!LabBinding.IsModificationBuildAudited(tables, build))
                throw new InvalidOperationException("Modification configuration has not been audited for this build.");
            ModificationConfigurationWire.Apply(values, modification);
        }
        if (message.Conversion is { } conversion)
        {
            if (!LabBinding.IsConversionBuildAudited(tables, build))
                throw new InvalidOperationException("Conversion configuration has not been audited for this build.");
            if (conversion.CardItemId <= 0 || conversion.MinimumDays <= 0)
                throw new ArgumentException("Conversion configuration requires a positive card and minimum day count.", nameof(message));
            // kr-1256 sub_DF9F70 shows tab 4 from flag +5; sub_DFA000 copies its card from +56; sub_DCB180 compares +60.
            values["unknown_5"] = true;
            values["unknown_56"] = conversion.CardItemId;
            values["unknown_60"] = conversion.MinimumDays;
        }
        if (message.Advancement is { } advancement)
        {
            if (!LabBinding.IsAdvancementBuildAudited(tables, build))
                throw new InvalidOperationException("Advancement configuration has not been audited for this build.");
            ApplyAdvancement(values, advancement);
        }
        var body = PacketCodec.Write(tables, build, "ScienceLabConfiguration", values);
        return session.SendAsync(opcodes.S2C(ReplyName), body, ct);
    }

    // kr-1256 sub_DF9F70 shows tab 6 from flag +7; sub_DFA000 copies its card from +64, which sub_DD9690 also compares, and
    // installs the rule groups at +364 that kr-1256 sub_114B7F0 evaluates.
    private static void ApplyAdvancement(Dictionary<string, object?> values, ScienceLabAdvancementConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration.Groups);
        if (configuration.TabCardItemId <= 0 || configuration.Groups.Count == 0
            || configuration.Groups.Any(group => group is null || group.Key is < 1 or > 4 || group.Rules is null || group.Rules.Count == 0)
            || configuration.Groups.Select(group => group.Key).Distinct().Count() != configuration.Groups.Count)
            throw new ArgumentException("Advancement configuration requires a positive tab card and distinct nonempty rule groups.", nameof(configuration));
        foreach (var rule in configuration.Groups.SelectMany(group => group.Rules))
        {
            // kr-1256 sub_DD9690 names only the markers 4 and 6; any other marker aborts the confirmation.
            if (rule is null || rule.CardItemId <= 0 || rule.TargetMarker is not (4 or 6)
                || rule.BlockingEffects is null || rule.RequiredEffects is null || rule.SetIds is null
                || rule.BlockingEffects.Concat(rule.RequiredEffects).Concat(rule.SetIds).Any(id => id <= 0)
                || rule.BlockingEffects.Distinct().Count() != rule.BlockingEffects.Count
                || rule.RequiredEffects.Distinct().Count() != rule.RequiredEffects.Count
                || rule.SetIds.Distinct().Count() != rule.SetIds.Count)
                throw new ArgumentException("Advancement rule has an invalid card, marker, effect or set identity.", nameof(configuration));
        }
        values["unknown_7"] = true;
        values["unknown_64"] = configuration.TabCardItemId;
        values["unknown_124"] = configuration.Groups.OrderBy(group => group.Key).Select(group => (object?)new Dictionary<string, object?>
        {
            ["key"] = group.Key,
            ["values"] = group.Rules.Select(rule => (object?)new Dictionary<string, object?>
            {
                ["unknown_0"] = rule.CardItemId,
                ["unknown_4"] = Contents(rule.BlockingEffects),
                ["unknown_16"] = rule.SetIds.Cast<object?>().ToArray(),
                ["unknown_24"] = Array.Empty<object?>(),
                // kr-1256 sub_114B7F0, sub_D3BB40 and sub_DFB780 never read +32; the native rows are zero there.
                ["unknown_32"] = 0,
                ["unknown_36"] = Contents(rule.RequiredEffects),
                ["unknown_48"] = rule.TargetMarker,
            }).ToArray(),
        }).ToArray();
    }

    // kr-1256 sub_114B7F0 compares only the id at content +4 against the set's effects.
    private static object?[] Contents(IReadOnlyList<int> effects) => effects.Select(effect => (object?)new Dictionary<string, object?>
    {
        ["unknown_0"] = 0,
        ["unknown_4"] = effect,
        ["unknown_8"] = Array.Empty<object?>(),
        ["unknown_20"] = 0,
        ["unknown_24"] = "",
    }).ToArray();

    private void Validate()
    {
        if (!LabBinding.IsEnchantBuildAudited(tables, build))
            throw new ConfigException($"Science-lab configuration requires an explicit laboratory_enchant_builds entry for '{build}'.");
        if (opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ClientToServer && row.Name == RequestName)
                is not { Evidence: Evidence.Verified }
            || opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ServerToClient && row.Name == ReplyName)
                is not { Evidence: Evidence.Verified })
            throw new ConfigException($"Science-lab configuration requires verified game opcodes '{RequestName}' and '{ReplyName}' for '{build}'.");
        PacketLayoutContract.RequireEmpty(tables, build, "ScienceLabConfigurationRequest", "Science-lab configuration");
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabConfiguration", "Science-lab configuration",
            ("enchant_materials", "list<u32>"), ("additive_item_id", "u32"));
    }
}
