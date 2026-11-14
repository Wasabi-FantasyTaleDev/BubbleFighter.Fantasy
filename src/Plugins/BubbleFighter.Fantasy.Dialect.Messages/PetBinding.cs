using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Maps the build's audited pet operations and notifications through its packet tables.</summary>
internal sealed class PetBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<PetLevelUpRequested>, IInboundMessageBinding<PetAbilityChangeRequested>,
      IMessageSender<PetLevelUpReplied>, IMessageSender<PetAbilityChangeReplied>, IMessageSender<PetEvolved>
{
    /// <summary>Gets the game protocol whose pet messages this binding adapts.</summary>
    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "pet_evolution"))
            throw new ConfigException($"Build '{build}' has no audited pet evolution capability.");
        RequireEvolution();
        _ = RequireOpcode(PacketDirection.ServerToClient, "gspEvolveResult");
        var levelUp = tables.CapabilityOf(build, "pet_level_up");
        var abilityChange = tables.CapabilityOf(build, "pet_ability_change");
        ushort levelRequest = 0, abilityRequest = 0;
        if (levelUp)
        {
            PacketLayoutContract.RequireFields(tables, build, "PetLevelUpRequest", "Pet level-up request", ("target", "u64"));
            PacketLayoutContract.RequireFields(tables, build, "PetFollowerLevelUp", "Pet level-up reply",
                ("target", "u64"), ("result", "u32"));
            levelRequest = RequireOpcode(PacketDirection.ClientToServer, "gcpReqFollowerLevelup_");
            _ = RequireOpcode(PacketDirection.ServerToClient, "gspRepFollowerLevelup");
        }
        if (abilityChange)
        {
            PacketLayoutContract.RequireFields(tables, build, "PetAbilityChangeRequest", "Pet ability-change request",
                ("target", "u64"), ("old_selector", "u32"), ("new_selector", "u32"), ("consumable_item_id", "u32"));
            PacketLayoutContract.RequireFields(tables, build, "PetFollowerAbilityChange", "Pet ability-change reply",
                ("target", "u64"), ("result", "u32"), ("unknown_12", "u32"), ("selector", "u32"), ("unknown_20", "u32"));
            abilityRequest = RequireOpcode(PacketDirection.ClientToServer, "gcpReqFollowerAbilityChange_");
            _ = RequireOpcode(PacketDirection.ServerToClient, "gspRepFollowerAbilityChange");
        }
        context.Effect(context.Services.RegisterMany<IMessageSender<PetEvolved>>(this));
        var packets = context.Services.Get<IPacketRouter>();
        if (levelUp)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PetLevelUpRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PetLevelUpReplied>>(this));
            context.Effect(packets.Register(protocol, levelRequest, async (session, packet, ct) =>
            {
                if (!await messages.DispatchAsync(protocol, session, ReadLevelUp(packet.Payload.Span), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Pet level-up requests have no typed handler.");
            }));
        }
        if (abilityChange)
        {
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<PetAbilityChangeRequested>>(this));
            context.Effect(context.Services.RegisterMany<IMessageSender<PetAbilityChangeReplied>>(this));
            context.Effect(packets.Register(protocol, abilityRequest, async (session, packet, ct) =>
            {
                if (!await messages.DispatchAsync(protocol, session, ReadAbilityChange(packet.Payload.Span), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Pet ability-change requests have no typed handler.");
            }));
        }
    }

    internal PetLevelUpRequested ReadLevelUp(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "PetLevelUpRequest", body);
        return new(unchecked((ulong)(long)fields["target"]));
    }

    internal PetAbilityChangeRequested ReadAbilityChange(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "PetAbilityChangeRequest", body);
        return new(unchecked((ulong)(long)fields["target"]), (int)fields["old_selector"],
            (int)fields["new_selector"], (int)fields["consumable_item_id"]);
    }

    internal byte[] Write(PetLevelUpReplied message) => PacketCodec.Write(tables, build, "PetFollowerLevelUp",
        new Dictionary<string, object?> { ["target"] = unchecked((long)message.Target), ["result"] = message.Result });

    internal byte[] Write(PetAbilityChangeReplied message) => PacketCodec.Write(tables, build, "PetFollowerAbilityChange",
        new Dictionary<string, object?>
        {
            ["target"] = unchecked((long)message.Target), ["result"] = message.Result,
            ["unknown_12"] = message.Unknown12, ["selector"] = message.Selector, ["unknown_20"] = message.Unknown20,
        });

    internal byte[] Write(PetEvolved message)
    {
        var layout = tables.Resolve(build, "PetEvolveResult");
        var target = layout.Single(field => field.Name == "target");
        var values = new Dictionary<string, object?>
        {
            ["outcome"] = message.Outcome,
            ["target"] = target.Type == "u32" ? checked((uint)message.Target) : unchecked((long)message.Target),
            ["unknown_12"] = message.UnknownLast,
            ["rewards"] = message.Rewards,
        };
        if (layout.Any(field => field.Name == "unknown_16" && field.Type == "u32"))
            values["unknown_16"] = message.UnknownLast;
        else if (message.UnknownOptional is { } optional)
            values["unknown_16"] = new Dictionary<string, object?> { ["unknown_0"] = optional };
        return PacketCodec.Write(tables, build, "PetEvolveResult", values);
    }

    private void RequireEvolution()
    {
        var fields = tables.Resolve(build, "PetEvolveResult");
        var target = fields.SingleOrDefault(field => field.Name == "target");
        if (target is null || target.Type is not ("u32" or "u64"))
            throw new ConfigException($"Build '{build}' has no audited pet evolution target.");
        var optional = fields.Any(field => field.Type == "optional<0x3A,PetEvolveTail>");
        if (optional)
        {
            PacketLayoutContract.RequireFields(tables, build, "PetEvolveResult", "Pet evolution reply",
                ("outcome", "u32"), ("target", "u64"), ("unknown_16", "optional<0x3A,PetEvolveTail>"), ("rewards", "list<u32>"));
            PacketLayoutContract.RequireFields(tables, build, "PetEvolveTail", "Pet evolution optional word", ("unknown_0", "u32"));
        }
        else
            PacketLayoutContract.RequireFields(tables, build, "PetEvolveResult", "Pet evolution reply",
                ("outcome", "u32"), ("target", target.Type), (target.Type == "u32" ? "unknown_12" : "unknown_16", "u32"));
    }

    private ushort RequireOpcode(PacketDirection direction, string name) =>
        opcodes.All.FirstOrDefault(row => row.Direction == direction && row.Name == name) is { Evidence: Evidence.Verified } entry
            ? entry.Value : throw new ConfigException($"Pet messages require verified opcode '{name}' for '{build}'.");

    /// <summary>Writes the native level-up status after the feature publishes the pet's new state.</summary>
    public ValueTask SendAsync(ISession session, PetLevelUpReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepFollowerLevelup"), Write(message), ct);

    /// <summary>Writes the native ability-change status and the selected replacement definition.</summary>
    public ValueTask SendAsync(ISession session, PetAbilityChangeReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepFollowerAbilityChange"), Write(message), ct);

    /// <summary>Writes the growth or regression presentation in the recipient's audited layout.</summary>
    public ValueTask SendAsync(ISession session, PetEvolved message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspEvolveResult"), Write(message), ct);
}
