using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Marks the legacy equipment and preset request bindings and writes their replies. kr-812
/// never needs <c>gspRepInventoryEquip</c>/<c>gspRepInventoryUnequip</c>:
/// its 802 handler repaints the bag cells itself (kr-812 sub_8211F0 → sub_951810), so equipment
/// results are folded into the restated bag (S2C 802), the view (S2C 805) and the preset bar
/// (S2C 833) that follow them. cn-324's 802 and 801 handlers
/// (cn-324 sub_708AC0 / sub_7089E0) apply the slot model without repainting anything, and its
/// 808 handler (cn-324 sub_70B280) switches on the result code to drive the bag UI, so that
/// build states the result reply and the others keep the recorded no-reply shape.
/// </summary>
/// <param name="protocol">The connection protocol whose opcode table names the family.</param>
/// <param name="presetReply">The S2C opcode of the preset status reply, when the build's table names it.</param>
/// <param name="tables">The packet tables for the row.</param>
/// <param name="build">The build whose preset layouts are written.</param>
/// <param name="equipReply">The S2C opcode of the wear result, when the build states the reply.</param>
/// <param name="unequipReply">The S2C opcode of the removal result, when the build states the reply.</param>
internal sealed class EquipmentBinding(string protocol, ushort? presetReply, PacketTables tables, string build,
    ushort? equipReply = null, ushort? unequipReply = null)
    : IInboundMessageBinding<EquipmentChangeRequested>, IInboundMessageBinding<WeaponPresetChangeRequested>,
      IMessageSender<EquipmentChangeReplied>, IMessageSender<WeaponPresetChangeReplied>, IEquipmentReplyPolicy
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    // cn-324 sub_70B280 rebuilds the room inventory immediately; the committed bag must already be installed.
    // Whether the result follows the bag is the build's equipment.result_after_inventory value.
    public bool ReplyAfterInventory => equipReply is not null && EquipmentValues.ResultAfterInventory(tables, build);

    /// <summary>Gets whether the build names the preset status reply and the codec is complete.</summary>
    internal bool HasPresetReply => presetReply is not null;

    /// <summary>
    /// Writes the one-i32 result the equip readers take (kr-812 0x824AC0/0x824F00 recognize zero as
    /// success; cn-324 sub_70B280's and kr-1256 sub_9B34D0's case 0 is the success message and case 3 the no-item one).
    /// </summary>
    public ValueTask SendAsync(ISession session, EquipmentChangeReplied message, CancellationToken ct) =>
        equipReply is { } wear && unequipReply is { } remove
            ? session.SendAsync(message.Request.Worn ? wear : remove,
                EquipmentRequestWire.WriteResult(tables, build, message.Succeeded ? 0 : 3, message.Request.Reference), ct)
            : ValueTask.CompletedTask;

    /// <summary>Writes the preset status reply; only mounted when the build's table names S2C 832.</summary>
    public ValueTask SendAsync(ISession session, WeaponPresetChangeReplied message, CancellationToken ct) =>
        session.SendAsync(presetReply!.Value, PresetWire.WriteChangeReply(tables, build, message.Slot, message.Status), ct);
}

/// <summary>Reads the build's <c>equipment</c> values in <c>packets/builds.yaml</c>.</summary>
internal static class EquipmentValues
{
    /// <summary>Whether the wear and removal result follows the restated bag and view rather than preceding them.</summary>
    /// <param name="tables">The packet tables.</param>
    /// <param name="build">The packet-table build.</param>
    /// <returns>The build's value.</returns>
    /// <exception cref="ConfigException">Thrown when the value is missing or not a boolean.</exception>
    public static bool ResultAfterInventory(PacketTables tables, string build) =>
        tables.ValuesOf(build).TryGetValue("equipment", out var raw) && raw is Dictionary<string, object?> values
        && values.TryGetValue("result_after_inventory", out var value) && value is string text && bool.TryParse(text, out var after)
            ? after
            : throw new ConfigException($"Build '{build}' must define boolean equipment.result_after_inventory in packets/builds.yaml.");
}
