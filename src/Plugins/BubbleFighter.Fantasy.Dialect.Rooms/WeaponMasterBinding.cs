using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The WeaponMaster round's exchange (round mode 26): the equipment frame <c>EquipmentApply</c> and the empty revival
/// request <c>WeaponMasterRevivalRequest</c> of <c>packets/weapon-master.yaml</c>, numbered by the profile's opcode
/// table. Which weapon a seat receives, when, and whether a revival request is due is the round flow's decision.
/// </summary>
internal sealed class WeaponMasterBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IMessageSender<RoundEquipmentApplied>, IInboundMessageBinding<RoundWeaponMasterRevivalRequested>
{
    private const string OpcodeName = "gspApplyEquipment_";
    private const string RevivalName = "gcpReqWeaponMasterRevival_";

    // kr-1256 sub_6DD900 refuses a seat above 0x23; kr-1256 sub_4ADC80 refuses a slot above 5.
    private const int MaximumSeat = 0x23;
    private const int MaximumSlot = 5;

    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts and registers the sender and the revival request.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayout(packets, build);
        _ = table.S2C(OpcodeName);
        var revival = table.C2S(RevivalName);
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, revival, async (session, packet, ct) =>
        {
            var request = ReadRevival(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, request, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No WeaponMaster revival handler is installed.");
        }));
        context.Effect(services.RegisterMany<IMessageSender<RoundEquipmentApplied>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundWeaponMasterRevivalRequested>>(this));
        logger.LogInformation("dialect.rooms: WeaponMaster exchange of {Build} mounted for {Protocol}.", build, protocol);
    }

    /// <summary>Writes one equipment assignment to the receiving client.</summary>
    public ValueTask SendAsync(ISession session, RoundEquipmentApplied message, CancellationToken ct) =>
        session.SendAsync(table.S2C(OpcodeName), Write(packets, build, message), ct);

    /// <summary>Requires both structures to have exactly the audited layout of the build.</summary>
    internal static void RequireLayout(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireFields(packets, build, "EquipmentApply", "WeaponMaster equipment",
            ("seat", "u32"), ("slots", "list<u32>"), ("items", "list<u32>"), ("reason", "u32"));
        PacketLayoutContract.RequireEmpty(packets, build, "WeaponMasterRevivalRequest", "WeaponMaster revival");
    }

    /// <summary>Reads the revival request, which kr-1256 sub_4A73A0 sends without a field.</summary>
    internal static RoundWeaponMasterRevivalRequested ReadRevival(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        RequireLayout(packets, build);
        if (!payload.IsEmpty)
            throw new PacketFormatException($"WeaponMasterRevivalRequest must be empty; got {payload.Length} byte(s).", 0);
        return new RoundWeaponMasterRevivalRequested();
    }

    /// <summary>
    /// Writes the frame. The client refuses mismatched lists, a seat above 35 and a slot above 5, so each is
    /// rejected here before any byte is written.
    /// </summary>
    internal static byte[] Write(PacketTables packets, string build, RoundEquipmentApplied message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireLayout(packets, build);
        if (message.Seat is < 0 or > MaximumSeat)
            throw new InvalidDataException($"Equipment seat {message.Seat} is outside the client's 36 seats.");
        if (message.Slots.Count != message.Items.Count)
            throw new InvalidDataException("Equipment slot and item lists must have the same length.");
        if (message.Slots.Any(slot => slot is < 0 or > MaximumSlot))
            throw new InvalidDataException($"Equipment slots must be 0 to {MaximumSlot}.");
        if (message.Items.Any(item => item < 0))
            throw new InvalidDataException("Equipment item ids cannot be negative.");
        return PacketCodec.Write(packets, build, "EquipmentApply", new Dictionary<string, object?>
        {
            ["seat"] = message.Seat,
            ["slots"] = message.Slots.Select(slot => (object)(uint)slot).ToArray(),
            ["items"] = message.Items.Select(item => (object)(uint)item).ToArray(),
            ["reason"] = message.Reason,
        });
    }
}
