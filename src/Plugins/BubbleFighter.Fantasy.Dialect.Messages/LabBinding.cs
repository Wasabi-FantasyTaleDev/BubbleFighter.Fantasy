using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Decodes audited science-lab operations for shared feature handlers and answers closed when none is installed.
/// Packet layouts and the fallback result remain properties of the selected client build.
/// </summary>
internal sealed class LabBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<LabOperationRequested>, IMessageSender<LabOperationAnswered>,
      IMessageSender<LabOperationRefused>, IMessageSender<LabEnchantCompleted>, IMessageSender<LabModificationCompleted>,
      IMessageSender<LabConversionCompleted>, IMessageSender<LabAdvancementCompleted>
{
    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    private static readonly (string Request, string Reply, LabOperation Operation)[] Operations =
    [
        ("gcpReqScienceLabCompound_", "gspRepCompound", LabOperation.Compound),
        ("gcpReqScienceLabCompoundUnlimited_", "gspRepCompoundUnlimited", LabOperation.CompoundUnlimited),
        ("gcpReqEnchant", "gspRepEnchant", LabOperation.Enchant),
        ("gcpReqScienceLabPetCompound_", "gspRepPetCompound", LabOperation.PetCompound),
        ("gcpReqScienceLabMakeItemAdvanced_", "gspRepMakeItemAdvanced", LabOperation.MakeItemAdvanced),
        ("gcpReqChangeUnlimited", "gspRepChangeUnlimited", LabOperation.ChangeUnlimited),
        ("gcpReqScienceLabModification_", "gspRepModification", LabOperation.ModificationApply),
        ("gcpReqScienceLabModificationDelete_", "gspRepModificationDelete", LabOperation.ModificationDelete),
    ];

    /// <summary>Validates the complete audited operation surface before registering any route.</summary>
    internal void Mount(IPluginContext context)
    {
        var operations = EnabledOperations();
        foreach (var (request, reply, _) in operations)
        {
            if (opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ClientToServer && row.Name == request) is not { Evidence: Evidence.Verified }
                || opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ServerToClient && row.Name == reply) is not { Evidence: Evidence.Verified })
                throw new ConfigException($"Science lab requires verified game opcodes '{request}' and '{reply}' for '{build}'.");
        }
        var requestLayout = tables.Resolve(build, "ScienceLabRequest");
        var argumentType = requestLayout.SingleOrDefault(field => field.Name == "argument")?.Type;
        if (requestLayout.Count != 2 || argumentType is not ("u32" or "u64"))
            throw new ConfigException($"Science lab requires a counted unsigned key list and an unsigned argument for '{build}'.");
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabRequest", "Science lab", ("keys", $"list<{argumentType}>"));
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Science lab",
            ("result", "u32"), ("unknown_40", "u64"));
        if (tables.Resolve(build, "ScienceLabReply").Where(field => field.Name is not ("result" or "unknown_40"))
            .Any(field => field.Type != $"list<{argumentType}>"))
            throw new ConfigException($"Science-lab reply references must match the request's unsigned key width for '{build}'.");
        var closedResult = ClosedResult();
        var enchantCompletion = IsEnchantBuildAudited(tables, build);
        if (enchantCompletion) ValidateEnchantCompletion(operations, closedResult);
        var modificationCompletion = IsModificationBuildAudited(tables, build);
        if (modificationCompletion) ValidateModificationCompletion(operations);
        var conversionCompletion = IsConversionBuildAudited(tables, build);
        if (conversionCompletion) ValidateConversionCompletion(operations, closedResult);
        var advancementCompletion = IsAdvancementBuildAudited(tables, build);
        if (advancementCompletion) ValidateAdvancementCompletion(operations, closedResult);
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<LabOperationRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<LabOperationAnswered>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<LabOperationRefused>>(this));
        if (enchantCompletion)
            context.Effect(context.Services.RegisterMany<IMessageSender<LabEnchantCompleted>>(this));
        if (modificationCompletion)
            context.Effect(context.Services.RegisterMany<IMessageSender<LabModificationCompleted>>(this));
        if (conversionCompletion)
            context.Effect(context.Services.RegisterMany<IMessageSender<LabConversionCompleted>>(this));
        if (advancementCompletion)
            context.Effect(context.Services.RegisterMany<IMessageSender<LabAdvancementCompleted>>(this));
        foreach (var (request, _, operation) in operations)
        {
            var kind = operation;
            context.Effect(router.Register(protocol, opcodes.C2S(request), async (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var fields = PacketCodec.Read(tables, build, "ScienceLabRequest", packet.Payload.Span);
                var decoded = new LabOperationRequested(kind,
                    ((object?[])fields["keys"]!).Select(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToArray(),
                    Convert.ToInt64(fields["argument"], CultureInfo.InvariantCulture));
                if (!await messages.DispatchAsync(protocol, session, decoded, ct).ConfigureAwait(false))
                    // Each build states the result its laboratory consumer maps to errorCloseMsg.
                    await SendAsync(session, new LabOperationAnswered(decoded.Operation, closedResult), ct).ConfigureAwait(false);
            }));
        }
        context.Logger.LogInformation("dialect.messages: science-lab requests of {Build} use typed handlers with a closed fallback.", build);
    }

    /// <summary>Writes a closed reply with the opaque payload defaults retained in the packet table.</summary>
    public ValueTask SendAsync(ISession session, LabOperationAnswered message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operations = EnabledOperations();
        var entry = Array.FindIndex(operations, operation => operation.Operation == message.Operation);
        if (entry < 0)
            throw new ArgumentOutOfRangeException(nameof(message), message.Operation, "Unknown science-lab operation.");
        if (message.Result != ClosedResult())
            throw new ArgumentOutOfRangeException(nameof(message), message.Result, "Only the audited closed-laboratory result is supported.");
        return session.SendAsync(opcodes.S2C(operations[entry].Reply), PacketCodec.Write(tables, build, "ScienceLabReply",
            new Dictionary<string, object?>
            {
                ["result"] = message.Result,
            }), ct);
    }

    /// <summary>Uses the client's own unavailable result without exposing numeric results to shared gameplay.</summary>
    public ValueTask SendAsync(ISession session, LabOperationRefused message, CancellationToken ct) =>
        SendAsync(session, new LabOperationAnswered(message.Operation, ClosedResult()), ct);

    /// <summary>Writes the result references resolved by kr-1256 sub_DF9DA0 after inventory publication.</summary>
    public ValueTask SendAsync(ISession session, LabEnchantCompleted message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsEnchantBuildAudited(tables, build)) throw new InvalidOperationException("Enchant completion has not been audited for this build.");
        ValidateEnchantCompletion(EnabledOperations(), ClosedResult());
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Targets);
        if (message.Targets.Count is < 1 or > 128 || message.Targets.Distinct().Count() != message.Targets.Count
            || message.Targets.Any(key => !PetArticleKeys.IsAddressable(key)))
            throw new ArgumentException("Enchant results require bounded distinct native Article references.", nameof(message));
        return session.SendAsync(opcodes.S2C("gspRepEnchant"), PacketCodec.Write(tables, build, "ScienceLabReply",
            new Dictionary<string, object?>
            {
                ["result"] = EnchantResult(message.Succeeded ? "success" : "failure"),
                ["unknown_28"] = message.Targets.Select(key => unchecked((long)key)).ToArray(),
            }), ct);
    }

    /// <summary>Validates exact-build admission without admitting descendants through inherited values.</summary>
    internal static bool IsEnchantBuildAudited(PacketTables tables, string build)
        => IsOperationBuildAudited(tables, build, "laboratory_enchant_builds");

    internal static bool IsModificationBuildAudited(PacketTables tables, string build)
        => IsOperationBuildAudited(tables, build, "laboratory_modification_builds");

    internal static bool IsConversionBuildAudited(PacketTables tables, string build)
        => IsOperationBuildAudited(tables, build, "laboratory_conversion_builds");

    internal static bool IsAdvancementBuildAudited(PacketTables tables, string build)
        => IsOperationBuildAudited(tables, build, "laboratory_advancement_builds");

    /// <summary>
    /// Publishes the references kr-1256 sub_DF8FA0 forwards to kr-1256 sub_DF9DA0 after a permanent conversion: one Article
    /// key for an individual item, or the set identity and one converted member's Article key for a set.
    /// </summary>
    public ValueTask SendAsync(ISession session, LabConversionCompleted message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Keys);
        if (!IsConversionBuildAudited(tables, build))
            throw new InvalidOperationException("Conversion completion has not been audited for this build.");
        ValidateConversionCompletion(EnabledOperations(), ClosedResult());
        // kr-1256 sub_DF9DA0 reads key 0 below PetArticleKeys.First as a set identity that needs the member key after it.
        var keys = message.Keys;
        var valid = keys.Count switch
        {
            1 => PetArticleKeys.IsAddressable(keys[0]),
            2 => keys[0] is > 0 and < PetArticleKeys.First && PetArticleKeys.IsAddressable(keys[1]),
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("Conversion completion requires one Article reference or a set identity and its member Article.", nameof(message));
        return SendKeysAsync(session, "gspRepChangeUnlimited", ConversionSuccessResult(), keys, ct);
    }

    /// <summary>
    /// Publishes the references kr-1256 sub_DF8910 forwards to kr-1256 sub_DF9DA0 after a grade-up: the set identity and one
    /// member's Article key of the owned set, resolved against the complete inventory published first.
    /// </summary>
    public ValueTask SendAsync(ISession session, LabAdvancementCompleted message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        if (!IsAdvancementBuildAudited(tables, build))
            throw new InvalidOperationException("Advancement completion has not been audited for this build.");
        ValidateAdvancementCompletion(EnabledOperations(), ClosedResult());
        if (message.SetId <= 0 || !PetArticleKeys.IsAddressable(message.Member))
            throw new ArgumentException("Advancement completion requires a set identity and a member Article.", nameof(message));
        return SendKeysAsync(session, "gspRepMakeItemAdvanced", AdvancementSuccessResult(), message.Keys, ct);
    }

    // One writer serves every laboratory tab whose success reply is an ordered key list in the third vector.
    private ValueTask SendKeysAsync(ISession session, string reply, int result, IReadOnlyList<ulong> keys, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C(reply), PacketCodec.Write(tables, build, "ScienceLabReply",
            new Dictionary<string, object?>
            {
                ["result"] = result,
                ["unknown_28"] = keys.Select(key => unchecked((long)key)).ToArray(),
            }), ct);

    private void ValidateAdvancementCompletion((string Request, string Reply, LabOperation Operation)[] operations, int closedResult)
    {
        if (!operations.Any(operation => operation.Operation == LabOperation.MakeItemAdvanced))
            throw new ConfigException($"Advancement completion requires the MakeItemAdvanced laboratory operation for '{build}'.");
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Advancement completion",
            ("result", "u32"), ("unknown_28", "list<u64>"));
        if (AdvancementSuccessResult() == closedResult)
            throw new ConfigException($"Advancement success and unavailable results must differ for '{build}'.");
    }

    private int AdvancementSuccessResult()
    {
        if (!tables.ValuesOf(build).TryGetValue("laboratory_advancement_success_result", out var value)
            || value is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0)
            throw new ConfigException($"Advancement requires laboratory_advancement_success_result for '{build}'.");
        return result;
    }

    private void ValidateConversionCompletion((string Request, string Reply, LabOperation Operation)[] operations, int closedResult)
    {
        if (!operations.Any(operation => operation.Operation == LabOperation.ChangeUnlimited))
            throw new ConfigException($"Conversion completion requires the ChangeUnlimited laboratory operation for '{build}'.");
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Conversion completion",
            ("result", "u32"), ("unknown_28", "list<u64>"));
        if (ConversionSuccessResult() == closedResult)
            throw new ConfigException($"Conversion success and unavailable results must differ for '{build}'.");
    }

    private int ConversionSuccessResult()
    {
        if (!tables.ValuesOf(build).TryGetValue("laboratory_conversion_success_result", out var value)
            || value is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0)
            throw new ConfigException($"Conversion requires laboratory_conversion_success_result for '{build}'.");
        return result;
    }

    private static bool IsOperationBuildAudited(PacketTables tables, string build, string admissionKey)
    {
        if (!tables.ValuesOf(build).TryGetValue(admissionKey, out var value)) return false;
        if (value is not string[] admitted || admitted.Distinct(StringComparer.Ordinal).Count() != admitted.Length
            || admitted.Any(name => !tables.Builds.Contains(name, StringComparer.Ordinal)))
            throw new ConfigException($"Science lab requires {admissionKey} to list distinct known builds for '{build}'.");
        return admitted.Contains(build, StringComparer.Ordinal);
    }

    /// <summary>Publishes the three references consumed by the native modification result after the Article update.</summary>
    public ValueTask SendAsync(ISession session, LabModificationCompleted message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        if (!IsModificationBuildAudited(tables, build))
            throw new InvalidOperationException("Modification completion has not been audited for this build.");
        ValidateModificationCompletion(EnabledOperations());
        if (!PetArticleKeys.IsAddressable(message.Target) || message.AttributeId is < 101 or > 156 || message.Group is < 0 or > 2)
            throw new ArgumentException("Modification completion requires an Article, attribute selector and installed group.", nameof(message));
        // kr-1256 sub_9C5920 and kr-1256 sub_9C5A70 consume target, selector and group from the third vector.
        return session.SendAsync(opcodes.S2C(message.Deleted ? "gspRepModificationDelete" : "gspRepModification"),
            PacketCodec.Write(tables, build, "ScienceLabReply", new Dictionary<string, object?>
            {
                ["result"] = ModificationSuccessResult(),
                ["unknown_28"] = new long[] { unchecked((long)message.Target), message.AttributeId, message.Group },
            }), ct);
    }

    private void ValidateModificationCompletion((string Request, string Reply, LabOperation Operation)[] operations)
    {
        if (!operations.Any(operation => operation.Operation == LabOperation.ModificationApply)
            || !operations.Any(operation => operation.Operation == LabOperation.ModificationDelete))
            throw new ConfigException($"Modification completion requires both native operation routes for '{build}'.");
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Modification completion",
            ("result", "u32"), ("unknown_28", "list<u64>"));
        if (ModificationSuccessResult() == ClosedResult())
            throw new ConfigException($"Modification success and unavailable results must differ for '{build}'.");
    }

    private int ModificationSuccessResult()
    {
        if (!tables.ValuesOf(build).TryGetValue("laboratory_modification_success_result", out var value)
            || value is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0)
            throw new ConfigException($"Modification requires laboratory_modification_success_result for '{build}'.");
        return result;
    }

    private void ValidateEnchantCompletion((string Request, string Reply, LabOperation Operation)[] operations, int closedResult)
    {
        if (!operations.Any(operation => operation.Operation == LabOperation.Enchant))
            throw new ConfigException($"Enchant completion requires the Enchant laboratory operation for '{build}'.");
        // kr-1256 sub_9C5F10 passes the third vector to kr-1256 sub_DF7CD0.
        // kr-1256 sub_DF9DA0 resolves those references from the inventory published first.
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Enchant completion",
            ("result", "u32"), ("unknown_28", "list<u64>"));
        var success = EnchantResult("success");
        var failure = EnchantResult("failure");
        if (success == failure || success == closedResult || failure == closedResult)
            throw new ConfigException($"Enchant success, failure and closed results must be distinct for '{build}'.");
    }

    private (string Request, string Reply, LabOperation Operation)[] EnabledOperations()
    {
        if (!tables.ValuesOf(build).TryGetValue("laboratory_operations", out var value)) return Operations;
        if (value is not string[] names || names.Length is < 1 or > 8 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ConfigException($"Science lab requires a nonempty, distinct laboratory_operations list for '{build}'.");
        var enabled = new List<(string Request, string Reply, LabOperation Operation)>();
        foreach (var name in names)
        {
            if (!Enum.TryParse<LabOperation>(name, true, out var operation) || !Enum.IsDefined(operation)
                || !string.Equals(name, operation.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ConfigException($"Unknown science-lab operation '{name}' for '{build}'.");
            enabled.Add(Operations.Single(entry => entry.Operation == operation));
        }
        return enabled.ToArray();
    }

    private int EnchantResult(string outcome)
    {
        var key = "laboratory_enchant_" + outcome + "_result";
        if (!tables.ValuesOf(build).TryGetValue(key, out var value) || value is not string text
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0)
            throw new ConfigException($"Enchant completion requires '{key}' in '{build}' values.");
        return result;
    }
    private int ClosedResult()
    {
        if (!tables.ValuesOf(build).TryGetValue("laboratory_closed_result", out var value)
            || value is not string number
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var closedResult)
            || closedResult <= 0)
            throw new ConfigException($"Science lab requires a positive laboratory_closed_result in '{build}' values.");
        return closedResult;
    }
}
