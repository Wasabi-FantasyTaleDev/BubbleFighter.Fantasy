using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Adapts native legacy synthesis independently of modern Article laboratory operations.</summary>
internal sealed class LaboratoryBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build,
    InventoryBaselineScope baselines) : IInboundMessageBinding<LaboratoryRequested>, IMessageSender<LaboratoryRefused>,
    IMessageSender<LaboratoryCompleted>
{
    /// <summary>Gets the audited game connection served by this binding.</summary>
    public string Protocol => protocol;

    // The reply field that carries the duration outcome, and whether a third result slot exists, read from this build's table.
    private readonly Lazy<(string OutcomeField, bool ThirdSlot)> _reply = new(() =>
    {
        var reply = tables.Resolve(build, "SynthesisProcessReply");
        return (reply.Any(field => field.Name == "duration_outcome") ? "duration_outcome" : "result",
            reply.Any(field => field.Name == "third_reference"));
    });

    private string OutcomeField => _reply.Value.OutcomeField;

    private bool ThirdSlot => _reply.Value.ThirdSlot;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.ValuesOf(build).TryGetValue("legacy_laboratory_builds", out var value) || value is not string[] builds
            || builds.Distinct(StringComparer.Ordinal).Count() != builds.Length
            || builds.Any(name => !tables.Builds.Contains(name, StringComparer.Ordinal))
            || !builds.Contains(build, StringComparer.Ordinal))
            throw new ConfigException($"Legacy laboratory completion has not been audited for '{build}'.");
        // kr-812 sub_9EC860 writes the full request; the older Chinese and Taiwanese writers (cn-324 sub_85ED90,
        // cn-717 sub_A9C5C0) have no password or set flag, and their trailing word sits at object offset 40 or 48.
        RequireLayout("SynthesisProcessRequest",
            "secondary_password:wstr,card_item_id:u32,operation:u32,material_category:u32,grouped:bool,unknown_52:u32,materials:list<u32>,additives:list<u32>",
            "card_item_id:u32,operation:u32,material_category:u32,materials:list<u32>,additives:list<u32>,unknown_48:u32",
            "card_item_id:u32,operation:u32,material_category:u32,materials:list<u32>,additives:list<u32>,unknown_40:u32");
        // kr-812 sub_83C0D0 reads 40 bytes; cn-717 sub_8C4C10 reads 28, with the outcome inside result and one extra slot.
        RequireLayout("SynthesisProcessReply",
            "operation:u32,result:u32,duration_outcome:u32,bonus_promotion:u32,primary_reference:u32,remaining_days:f64,second_reference:u32,third_reference:u32,unknown_40:u32",
            "operation:u32,result:u32,primary_reference:u32,remaining_days:f64,second_reference:u32,unknown_28:u32");
        var request = RequireOpcode(PacketDirection.ClientToServer, "gcpSynthesisProcess");
        _ = RequireOpcode(PacketDirection.ServerToClient, "gspSynthesisProcess");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<LaboratoryRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<LaboratoryRefused>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<LaboratoryCompleted>>(this));
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, request, async (session, packet, ct) =>
        {
            var decoded = Read(packet.Payload.Span, out var operation);
            if (decoded is null || !await messages.DispatchAsync(protocol, session, decoded, ct).ConfigureAwait(false))
                await SendAsync(session, new LaboratoryRefused(operation), ct).ConfigureAwait(false);
        }));
    }

    internal LaboratoryRequested? Read(ReadOnlySpan<byte> body, out LaboratoryOperation operation)
    {
        // kr-812 sub_9DA2F0 admits three selections; native SetItem has ten member columns and each group adds one reference.
        var fields = PacketCodec.Read(tables, build, "SynthesisProcessRequest", body,
            new PacketReadLimits((ushort.MaxValue - 32) / 2, 3 * 11, 8 + 3 * 11 + 2, 2), requireCanonicalBooleans: true);
        var code = (int)fields["operation"]!;
        if (code is not (1 or 2)) throw new PacketFormatException("Unknown native science-room operation.", 8);
        operation = (LaboratoryOperation)code;
        // kr-812 sub_9E29F0 can supply a secondary password; no unaudited verifier may silently discard it. The trailing
        // word is zero on the normal path in both builds (kr-812 sub_9E4E90, cn-717 sub_A94670); 9999 is a repeat probe.
        if (fields.TryGetValue("secondary_password", out var password) && ((string)password!).Length != 0
            || (int)(fields.GetValueOrDefault("unknown_52") ?? fields.GetValueOrDefault("unknown_48")
                ?? fields.GetValueOrDefault("unknown_40") ?? 0) != 0) return null;
        var grouped = fields.TryGetValue("grouped", out var group) && (bool)group!;
        return new((int)fields["card_item_id"]!, operation, (int)fields["material_category"]!, grouped,
            ((object?[])fields["materials"]!).Select(value => (int)value!).ToArray(),
            ((object?[])fields["additives"]!).Select(value => (int)value!).ToArray());
    }

    /// <summary>Uses the native generic signed-negative failure branch without inventing a reason-specific status.</summary>
    public ValueTask SendAsync(ISession session, LaboratoryRefused message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireOperation(message.Operation);
        // kr-812 sub_830A20 tests result < 0; -1 selects that branch without claiming a unique historical code.
        return session.SendAsync(opcodes.S2C("gspSynthesisProcess"), PacketCodec.Write(tables, build, "SynthesisProcessReply",
            new Dictionary<string, object?> { ["operation"] = (int)message.Operation, ["result"] = -1 }), ct);
    }

    /// <summary>Writes native result references after inventory publication established their time baseline.</summary>
    public async ValueTask SendAsync(ISession session, LaboratoryCompleted message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireOperation(message.Operation);
        if (message.PrimaryReference <= 0 || message.SecondReference < 0 || message.ThirdReference < 0
            || message.ThirdReference > 0 && message.SecondReference == 0 || !Enum.IsDefined(message.Outcome)
            || message.Operation == LaboratoryOperation.Decomposition && (message.Outcome != LaboratoryOutcome.Ordinary
                || !Stock(message.PrimaryReference) || message.ThirdReference != 0)
            || message.SecondReference != 0 && !Stock(message.SecondReference) || message.ThirdReference != 0 && !Stock(message.ThirdReference)
            || message.ThirdReference != 0 && !ThirdSlot)
            throw new ArgumentException("Science-room result references or outcome are invalid.", nameof(message));
        using var publication = await baselines.EnterAsync(session.Connection, ct).ConfigureAwait(false);
        // kr-812 sub_9DBED0 reads decomposition amounts from Stock; only synthesis consumes the reply's baseline-relative days.
        var days = message.Operation == LaboratoryOperation.Decomposition ? 0 : message.Lifetime.Kind switch
        {
            InventoryLifetimeKind.Permanent => 2958101.0,
            InventoryLifetimeKind.ExpiresAt => InventoryTime.ExpiringDaysAtBaseline(message.Lifetime, publication.Require()),
            _ => throw new ArgumentException("Science-room result lifetime is not established.", nameof(message)),
        };
        // kr-812 sub_9DBED0 presents synthesis durations >=730 as unlimited; never mislabel finite ownership.
        if (message.Operation == LaboratoryOperation.Synthesis && message.Lifetime.Kind == InventoryLifetimeKind.ExpiresAt && days >= 730)
            throw new ArgumentException("Finite synthesis duration exceeds its native presentation range.", nameof(message));
        // cn-717 sub_8B9120 recognizes only 3 and 4 in its shared result word; the additive outcomes need kr-812's own field.
        if (OutcomeField == "result" && message.Outcome is LaboratoryOutcome.AdditivePermanent or LaboratoryOutcome.AdditiveBonus)
            throw new ArgumentException("This build's science-room reply has no additive outcome.", nameof(message));
        var outcome = message.Outcome switch
        {
            LaboratoryOutcome.Ordinary => 0,
            LaboratoryOutcome.BecamePermanent => 3,
            LaboratoryOutcome.DurationCapped => 4,
            LaboratoryOutcome.AdditivePermanent => 100,
            LaboratoryOutcome.AdditiveBonus => 101,
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
        var values = new Dictionary<string, object?>
        {
            ["operation"] = (int)message.Operation, ["result"] = 0,
            ["primary_reference"] = message.PrimaryReference, ["remaining_days"] = days,
            ["second_reference"] = message.SecondReference,
        };
        values[OutcomeField] = outcome;
        if (ThirdSlot) values["third_reference"] = message.ThirdReference;
        var body = PacketCodec.Write(tables, build, "SynthesisProcessReply", values);
        await publication.SendAsync(session, opcodes.S2C("gspSynthesisProcess"), body).ConfigureAwait(false);
    }

    // kr-812 sub_4FB720 distinguishes ordinary Stock identities from base items and StockSet identities.
    private static bool Stock(int reference) => reference is >= 1000000 and < 2000000;

    private ushort RequireOpcode(PacketDirection direction, string name)
    {
        var entry = opcodes.All.SingleOrDefault(row => row.Direction == direction && row.Name == name);
        return entry is { Evidence: Evidence.Verified } ? entry.Value
            : throw new ConfigException($"Legacy laboratory requires verified {direction} '{name}' for '{build}'.");
    }

    private void RequireLayout(string name, params string[] allowed) =>
        PacketLayoutContract.RequireSignature(tables, build, name, "Legacy laboratory", allowed);

    private static void RequireOperation(LaboratoryOperation operation)
    {
        if (operation is not (LaboratoryOperation.Synthesis or LaboratoryOperation.Decomposition))
            throw new ArgumentOutOfRangeException(nameof(operation));
    }
}
