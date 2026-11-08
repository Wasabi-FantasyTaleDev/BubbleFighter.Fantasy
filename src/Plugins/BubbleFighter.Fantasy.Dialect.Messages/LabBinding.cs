using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Binds the audited science-lab operations to a closed-lab reply. No recipes, currency changes or item
/// mutations are implemented. A client request for publishing laboratory state has not been established.
/// </summary>
internal sealed class LabBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IMessageSender<LabOperationAnswered>
{
    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    private static readonly (string Request, string Reply, LabOperation Operation)[] Operations =
    [
        ("gcpReqScienceLabCompound_", "gspRepCompound", LabOperation.Compound),
        ("gcpReqScienceLabCompoundUnlimited_", "gspRepCompoundUnlimited", LabOperation.CompoundUnlimited),
        ("gcpReqScienceLabEnchant_", "gspRepEnchant", LabOperation.Enchant),
        ("gcpReqScienceLabPetCompound_", "gspRepPetCompound", LabOperation.PetCompound),
        ("gcpReqScienceLabMakeItemAdvanced_", "gspRepMakeItemAdvanced", LabOperation.MakeItemAdvanced),
        ("gcpReqScienceLabChangeUnlimited_", "gspRepChangeUnlimited", LabOperation.ChangeUnlimited),
        ("gcpReqScienceLabModification_", "gspRepModification", LabOperation.ModificationApply),
        ("gcpReqScienceLabModificationDelete_", "gspRepModificationDelete", LabOperation.ModificationDelete),
    ];

    /// <summary>Validates the complete audited operation surface before registering any route.</summary>
    internal void Mount(IPluginContext context)
    {
        foreach (var (request, reply, _) in Operations)
        {
            if (opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ClientToServer && row.Name == request) is not { Evidence: Evidence.Verified }
                || opcodes.All.FirstOrDefault(row => row.Direction == PacketDirection.ServerToClient && row.Name == reply) is not { Evidence: Evidence.Verified })
                throw new ConfigException($"Science lab requires verified game opcodes '{request}' and '{reply}' for '{build}'.");
        }
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabRequest", "Science lab",
            ("keys", "list<u64>"), ("argument", "u64"));
        PacketLayoutContract.RequireFields(tables, build, "ScienceLabReply", "Science lab",
            ("result", "u32"), ("unknown_4", "list<u64>"), ("unknown_16", "list<u64>"),
            ("unknown_28", "list<u64>"), ("unknown_40", "u64"));
        var closedResult = ClosedResult();
        var router = context.Services.Get<IPacketRouter>();
        context.Effect(context.Services.RegisterMany<IMessageSender<LabOperationAnswered>>(this));
        foreach (var (request, _, operation) in Operations)
        {
            var kind = operation;
            context.Effect(router.Register(protocol, opcodes.C2S(request), (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var fields = PacketCodec.Read(tables, build, "ScienceLabRequest", packet.Payload.Span);
                var decoded = new LabOperationRequested(kind,
                    ((object?[])fields["keys"]!).Select(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToArray(),
                    Convert.ToInt64(fields["argument"], CultureInfo.InvariantCulture));
                // Each build states the result its laboratory consumer maps to errorCloseMsg.
                return SendAsync(session, new LabOperationAnswered(decoded.Operation, closedResult), ct);
            }));
        }
        context.Logger.LogInformation("dialect.messages: science-lab requests of {Build} answer closed; no recipe economy is configured.", build);
    }

    /// <summary>Writes a closed reply with the opaque payload defaults retained in the packet table.</summary>
    public ValueTask SendAsync(ISession session, LabOperationAnswered message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = Array.FindIndex(Operations, operation => operation.Operation == message.Operation);
        if (entry < 0)
            throw new ArgumentOutOfRangeException(nameof(message), message.Operation, "Unknown science-lab operation.");
        if (message.Result != ClosedResult())
            throw new ArgumentOutOfRangeException(nameof(message), message.Result, "Only the audited closed-laboratory result is supported.");
        return session.SendAsync(opcodes.S2C(Operations[entry].Reply), PacketCodec.Write(tables, build, "ScienceLabReply",
            new Dictionary<string, object?>
            {
                ["result"] = message.Result,
            }), ct);
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
