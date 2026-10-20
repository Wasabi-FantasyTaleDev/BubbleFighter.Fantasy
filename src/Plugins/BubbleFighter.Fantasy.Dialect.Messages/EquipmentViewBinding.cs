using BubbleFighter.Fantasy.Abstractions;
using System.Linq;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the equipped-view publications. A local publication sends the parts the build's
/// <c>equipment.local_view</c> value lists, in its order: <c>view</c> (<c>gspInventoryViewUpdateView</c>),
/// <c>preset_update</c> (<c>gspReqWeaponPreSetUpdate</c>) and <c>preset_reply</c>
/// (<c>gspReqInventoryChangeWeaponPresetIdx</c> with status zero); a part whose packet the build's table does not
/// name is skipped. kr-812 sends the view, then the preset bar; kr-1256's view handler overwrites the profile record
/// the preset packets fill (kr-1256 sub_9AF2F0 -> sub_81CCE0, sub_9C3880 -> sub_81B820), so its presets go first.
/// Remote observations carry the view alone.
/// </summary>
/// <param name="protocol">The connection protocol whose opcode table names the view.</param>
/// <param name="view">The S2C opcode of <c>gspInventoryViewUpdateView</c>.</param>
/// <param name="presetUpdate">The S2C opcode of the preset bar, when the build's table names it.</param>
/// <param name="presetReply">The S2C opcode of the preset status reply, when the build's table names it.</param>
/// <param name="tables">The packet tables.</param>
/// <param name="build">The build whose layout is written.</param>
internal sealed class EquipmentViewBinding(string protocol, ushort view, ushort? presetUpdate, ushort? presetReply,
    PacketTables tables, string build)
    : IMessageSender<LocalEquipmentViewUpdated>, IMessageSender<RemoteEquipmentViewUpdated>
{
    private readonly EquipmentViewValues _values = EquipmentViewValues.Of(tables, build);

    /// <inheritdoc />
    public string Protocol => protocol;

    /// <summary>Sends the local view and the preset publications the build lists, built from one worn set.</summary>
    public async ValueTask SendAsync(ISession session, LocalEquipmentViewUpdated message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message.EquippedItemIds);
        // Write every part before the first send can yield to another selection.
        var parts = new List<(ushort Opcode, byte[] Body)>(_values.LocalView.Count);
        foreach (var part in _values.LocalView)
        {
            switch (part)
            {
                case "view":
                    parts.Add((view, WriteView(tables, build, _values, message.UserNum, message.CharacterId, message.EquippedItemIds)));
                    break;
                case "preset_update" when presetUpdate is { } update:
                    parts.Add((update, PresetWire.WriteUpdate(tables, build, message.ActiveWeaponPresetSlot, message.UserNum,
                        WeaponPresetSlots.FromWorn(message.EquippedItemIds), message.PrimaryWeaponId, message.SpecialWeaponId,
                        _values.PresetPages)));
                    break;
                case "preset_reply" when presetReply is { } reply:
                    parts.Add((reply, PresetWire.WriteChangeReply(tables, build, message.ActiveWeaponPresetSlot, 0)));
                    break;
            }
        }
        foreach (var (opcode, body) in parts)
            await session.SendAsync(opcode, body, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the observed player's view without touching the recipient's preset bar.</summary>
    public ValueTask SendAsync(ISession session, RemoteEquipmentViewUpdated message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message.EquippedItemIds);
        return session.SendAsync(view, WriteView(tables, build, _values, message.UserNum, message.CharacterId, message.EquippedItemIds), ct);
    }

    /// <summary>
    /// Writes the view body: the build's <c>equipment.view</c> structure with the worn item ids. kr-812's is
    /// <c>EquippedItemList</c> (kr-812 sub_80A4F0, keyed by user number in kr-812 sub_9883E0); kr-1256's is the
    /// profile record <c>RoomProfileEntry</c> (kr-1256 sub_9AF2F0). The trailing vectors are always empty.
    /// </summary>
    internal static byte[] WriteView(PacketTables tables, string build, int userNum, int characterId, IReadOnlyList<int> equippedIds) =>
        WriteView(tables, build, EquipmentViewValues.Of(tables, build), userNum, characterId, equippedIds);

    private static byte[] WriteView(PacketTables tables, string build, EquipmentViewValues values, int userNum, int characterId,
        IReadOnlyList<int> equippedIds)
    {
        ArgumentNullException.ThrowIfNull(equippedIds);
        if (equippedIds.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(equippedIds), "Equipped items exceed the inventory bound.");
        return PacketCodec.Write(tables, build, values.ViewStructure, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = userNum, ["character_item"] = characterId, ["character_id"] = characterId,
            ["equipped_items"] = equippedIds,
        });
    }
}

/// <summary>The build's <c>equipment</c> values that shape the equipped view (<c>packets/builds.yaml</c>).</summary>
/// <param name="ViewStructure">The structure the view body is written as.</param>
/// <param name="LocalView">The parts of a local publication, in order.</param>
/// <param name="PresetPages">How many preset records a preset list with records carries.</param>
internal sealed record EquipmentViewValues(string ViewStructure, IReadOnlyList<string> LocalView, int PresetPages)
{
    private static readonly string[] Parts = ["view", "preset_update", "preset_reply"];

    /// <summary>Reads the values, failing when one is missing or names an unknown part.</summary>
    public static EquipmentViewValues Of(PacketTables tables, string build)
    {
        if (!tables.ValuesOf(build).TryGetValue("equipment", out var raw) || raw is not Dictionary<string, object?> values)
            throw new ConfigException($"Build '{build}' must define equipment values in packets/builds.yaml.");
        var structure = values.TryGetValue("view", out var name) && name is string text && text.Length > 0
            ? text : throw new ConfigException($"Build '{build}' must name its equipment.view structure.");
        var order = values.TryGetValue("local_view", out var list) && list is string[] items && items.Length > 0
            ? items : throw new ConfigException($"Build '{build}' must list its equipment.local_view parts.");
        if (order.FirstOrDefault(part => !Parts.Contains(part)) is { } unknown)
            throw new ConfigException($"Build '{build}' equipment.local_view names the unknown part '{unknown}'.");
        var pages = 0;
        if (values.TryGetValue("preset_pages", out var count)
            && !(count is string number && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pages) && pages is > 0 and <= InventoryLimits.MaxWeaponPresetSlot + 1))
            throw new ConfigException($"Build '{build}' equipment.preset_pages must be 1 to {InventoryLimits.MaxWeaponPresetSlot + 1}.");
        _ = tables.Resolve(build, structure);
        return new(structure, order, pages);
    }
}
