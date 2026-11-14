using System.Collections.Frozen;
using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Data;

internal enum CustomGameRuleKind { Slide, Boolean, Selection }

/// <summary>A mode-specific native control, with wire values distinguished from its initial UI values.</summary>
internal sealed record CustomGameRuleDefinition(ushort ValueId, string Name, CustomGameRuleKind Kind,
    uint Minimum, uint Maximum, uint Step, int MaxSelections, IReadOnlyList<uint> AllowedValues,
    IReadOnlyList<uint> InitialValues, IReadOnlyList<uint>? RequiredValues,
    IReadOnlyList<ushort> ExclusiveIds, IReadOnlyList<ushort> DependencyIds, bool AlwaysReturnCurrent);

internal sealed record CustomGameModeRules(int ModeId, IReadOnlyDictionary<ushort, CustomGameRuleDefinition> Rules,
    IReadOnlyList<RoomCustomRuleValue> FirstChanges);

/// <summary>Validates custom options against the selected client's own mode matrix and control definitions.</summary>
internal sealed class CustomGameRulesCatalog
{
    // kr-1256 sub_1138B20 maps these native names; kr-1256 sub_542A00 encodes the three selection controls.
    private static readonly string[] Names = ["SpeedRed", "SpeedBlue", "SpeedAll", "SpeedZombie", "SpeedHuman",
        "HpRed", "HpBlue", "HpAll", "HpZombie", "HpHuman", "OneKill", "PrimaryWeaponRestrict", "WeaponSlotRestrict",
        "CharacterRestrict", "AmmoUnlimit", "GrenadeUnlimit", "GrenadeRestrict", "MagazineReload", "SecondaryJump",
        "BurstPang", "Fever", "Fog", "SuperPang", "RespawnItemRestrict", "PetRestrict", "EnchantRestrict",
        "ModRestrict", "PlayerCountImbalance", "CustomAllPang", "GamePlayTimer", "TopView", "TopViewSpecPreset",
        "BomAreaUnlimit", "BoomRespawnUnlimit", "BrandAvail"];
    private static readonly FrozenDictionary<string, ushort> Ids = Names.Select((name, index) => (name, id: (ushort)(index + 1)))
        .ToFrozenDictionary(pair => pair.name, pair => pair.id, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, int> ModeIds = MapCatalog.ModeWidgets
        .ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private CustomGameRulesCatalog(Dictionary<int, CustomGameModeRules> modes) => Modes = modes.ToFrozenDictionary();

    internal IReadOnlyDictionary<int, CustomGameModeRules> Modes { get; }

    internal static CustomGameRulesCatalog Load(IClientDataStore store, string path, int maxBytes,
        int maxRows, int maxColumns, int maxCellCharacters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCellCharacters, 1);
        var bytes = store.Read(path, maxBytes);
        if (bytes.Length == 0 || bytes.Length > maxBytes) throw new InvalidDataException("The custom room workbook is empty or oversized.");
        var sheets = CustomGameRulesWorkbook.Read(bytes, path, maxRows, maxColumns, maxCellCharacters,
            ["CustomGameCommon", "CustomGameWgt", "WgtInitValues", "Nochangables", "FirstChanges"]);
        var common = ReadCommon(sheets["CustomGameCommon"]);
        var allowed = ReadMatrix("CustomGameWgt", sheets["CustomGameWgt"], common);
        var initial = ReadMatrix("WgtInitValues", sheets["WgtInitValues"], common);
        var fixedValues = ReadMatrix("Nochangables", sheets["Nochangables"], common);
        var first = ReadMatrix("FirstChanges", sheets["FirstChanges"], common);
        foreach (var matrix in new[] { initial, fixedValues, first })
            if (matrix.Keys.Any(mode => !allowed.ContainsKey(mode)))
                throw new InvalidDataException("A custom rule defaults table names a mode absent from CustomGameWgt.");
        var modes = new Dictionary<int, CustomGameModeRules>();
        foreach (var (mode, cells) in allowed)
        {
            var rules = new Dictionary<ushort, CustomGameRuleDefinition>();
            var firstChanges = new List<RoomCustomRuleValue>();
            foreach (var (id, source) in common)
            {
                var enabled = Flag(Cell(cells, id), emptyIsFalse: true);
                var fixedCell = Cell(fixedValues, mode, id);
                var isFixed = Flag(fixedCell, emptyIsFalse: true);
                var firstCell = Cell(first, mode, id);
                if (!source.Enabled || !enabled)
                {
                    if (isFixed || firstCell.Length != 0)
                        throw new InvalidDataException($"Mode {mode} gives fixed values to unavailable rule {source.Definition.Name}.");
                    continue;
                }
                var definition = source.Definition;
                var initialCell = Cell(initial, mode, id);
                if (initialCell.Length != 0) definition = definition with { InitialValues = ParseInitial(definition, initialCell) };
                var firstValues = firstCell.Length == 0 ? null : ParseInitial(definition, firstCell);
                if (firstValues is not null)
                {
                    if (definition.Kind == CustomGameRuleKind.Selection)
                        throw new InvalidDataException("Custom selection FirstChanges require an audited delta encoding.");
                    firstChanges.Add(new(id, firstValues));
                }
                if (isFixed) definition = definition with { RequiredValues = firstValues ?? definition.InitialValues };
                rules.Add(id, definition);
            }
            if (rules.Count != 0) modes.Add(mode, new(mode, rules.ToFrozenDictionary(), firstChanges.AsReadOnly()));
        }
        if (modes.Count == 0) throw new InvalidDataException("CustomGameWgt enables no supported custom room controls.");
        var catalog = new CustomGameRulesCatalog(modes);
        foreach (var mode in modes.Keys)
        {
            try { catalog.Validate(mode, catalog.InitialValues(mode)); }
            catch (ArgumentException exception) { throw new InvalidDataException($"Mode {mode} has inconsistent native custom rule defaults.", exception); }
        }
        return catalog;
    }

    /// <summary>Returns only the native FirstChanges overlay; UI initial values are not additional wire rows.</summary>
    internal IReadOnlyList<RoomCustomRuleValue> InitialValues(int mode) => Modes.TryGetValue(mode, out var rules)
        ? rules.FirstChanges : Array.Empty<RoomCustomRuleValue>();

    /// <summary>Checks the native empty-or-mode-default marker without interpreting it as a feature switch.</summary>
    internal bool MatchesModeDefaults(int mode, IReadOnlyList<RoomCustomRuleValue> values)
    {
        // kr-1256 sub_544790 treats an empty vector as default and otherwise compares keyed rows with kr-1256 sub_40F3A0.
        var initial = InitialValues(mode);
        return values.Count == 0 || values.Count == initial.Count && initial.All(expected =>
            values.Any(actual => actual.ValueId == expected.ValueId && actual.Values.SequenceEqual(expected.Values)));
    }

    /// <summary>Rejects options outside this mode, invalid domains, fixed-value changes and native control conflicts.</summary>
    internal void Validate(int mode, IReadOnlyList<RoomCustomRuleValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Modes.TryGetValue(mode, out var modeRules)) throw new ArgumentException($"Mode {mode} has no native custom room controls.", nameof(mode));
        if (values.Count > modeRules.Rules.Count) throw new ArgumentException("The custom rule vector exceeds this mode's controls.", nameof(values));
        var submitted = new Dictionary<ushort, IReadOnlyList<uint>>();
        foreach (var row in values)
        {
            if (row is null || row.Values is null || !modeRules.Rules.TryGetValue(row.ValueId, out var definition)
                || !submitted.TryAdd(row.ValueId, row.Values))
                throw new ArgumentException("Custom rules contain an unavailable or repeated selector.", nameof(values));
            ValidateDomain(definition, row.Values);
            // kr-1256 sub_540510 and kr-1256 sub_542A00 omit unchanged controls; kr-1256 sub_AB66B0 gives their presence a gameplay effect.
            if (row.Values.SequenceEqual(definition.InitialValues)
                && (definition.Kind != CustomGameRuleKind.Slide || !definition.AlwaysReturnCurrent))
                throw new ArgumentException($"Unchanged custom rule {definition.Name} must be omitted.", nameof(values));
        }
        IReadOnlyList<uint> Effective(CustomGameRuleDefinition rule) => submitted.GetValueOrDefault(rule.ValueId) ?? rule.InitialValues;
        bool Active(CustomGameRuleDefinition rule) => rule.Kind switch
        {
            CustomGameRuleKind.Boolean => Effective(rule)[0] == 1,
            CustomGameRuleKind.Selection => Effective(rule).Count != 0,
            _ => !Effective(rule).SequenceEqual(rule.InitialValues),
        };
        foreach (var rule in modeRules.Rules.Values)
        {
            var effective = Effective(rule);
            if (rule.RequiredValues is not null && !effective.SequenceEqual(rule.RequiredValues))
                throw new ArgumentException($"Mode {mode} fixes custom rule {rule.Name}.", nameof(values));
            if (submitted.ContainsKey(rule.ValueId))
                foreach (var dependency in rule.DependencyIds)
                    if (!modeRules.Rules.TryGetValue(dependency, out var target) || !Active(target))
                        throw new ArgumentException($"Custom rule {rule.Name} requires selector {dependency}.", nameof(values));
            if (!Active(rule)) continue;
            foreach (var exclusion in rule.ExclusiveIds)
            {
                if (!modeRules.Rules.TryGetValue(exclusion, out var other) || !Active(other)) continue;
                // kr-1256 sub_541940 disables only the corresponding primary/grenade slot, not every weapon slot.
                var slotRule = rule.ValueId == 13 ? rule : other.ValueId == 13 ? other : null;
                if (slotRule is not null)
                {
                    var opposite = rule.ValueId == 13 ? other.ValueId : rule.ValueId;
                    if (opposite == 12 && !Effective(slotRule).Contains(0u)) continue;
                    if (opposite is 16 or 17 && !Effective(slotRule).Contains(3u)) continue;
                }
                throw new ArgumentException($"Custom rules {rule.Name} and {other.Name} conflict.", nameof(values));
            }
        }
    }

    private static void ValidateDomain(CustomGameRuleDefinition rule, IReadOnlyList<uint> values)
    {
        if (rule.Kind == CustomGameRuleKind.Selection)
        {
            if (values.Count == 0 || values.Count > rule.MaxSelections || values.Distinct().Count() != values.Count
                || values.Any(value => !rule.AllowedValues.Contains(value)))
                throw new ArgumentException($"Custom rule {rule.Name} has invalid selections.");
        }
        else if (values.Count != 1 || values[0] < rule.Minimum || values[0] > rule.Maximum
                 || (values[0] - rule.Minimum) % rule.Step != 0)
            throw new ArgumentException($"Custom rule {rule.Name} is outside its native range or step.");
    }

    private static Dictionary<ushort, SourceRule> ReadCommon(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new CustomGameRulesWorkbookTable("CustomGameCommon", rows, 2, "ValueId", "OnOff", "SetKind", "SetValues",
            "InitValues", "ExclusiveValues", "DependenceValues", "AlwaysReturnCurrent");
        var result = new Dictionary<ushort, SourceRule>();
        foreach (var row in table.Rows)
        {
            var name = table.Cell(row, "ValueId");
            if (name.Length == 0) continue;
            if (!Ids.TryGetValue(name, out var id)) throw new InvalidDataException($"Unknown native custom ValueId '{name}'.");
            var kindText = table.Required(row, "SetKind");
            var kind = kindText == "Slide" ? CustomGameRuleKind.Slide : kindText == "CheckBox"
                ? id is 12 or 13 or 17 ? CustomGameRuleKind.Selection : CustomGameRuleKind.Boolean
                : throw new InvalidDataException($"Unknown custom control kind '{kindText}'.");
            var setValues = Split(table.Required(row, "SetValues"));
            uint minimum = 0, maximum = 1, step = 1;
            uint[] allowed = [];
            var maxSelections = 1;
            if (kind == CustomGameRuleKind.Slide)
            {
                if (setValues.Length != 3) throw new InvalidDataException($"Custom slider {name} has no min,max,step triple.");
                minimum = Number(setValues[0]); maximum = Number(setValues[1]); step = Number(setValues[2]);
                if (maximum < minimum || step == 0 || (maximum - minimum) % step != 0)
                    throw new InvalidDataException($"Custom slider {name} has an invalid range.");
            }
            else if (kind == CustomGameRuleKind.Selection)
            {
                // kr-1256 sub_11393C0 maps primary types; kr-1256 sub_11391F0 maps slots; kr-1256 sub_542490 prevents selecting all.
                allowed = id == 12 ? [2u, 3u, 4u] : id == 13 ? [0u, 1u, 2u, 3u] : [0u, 1u];
                if (setValues.Length != allowed.Length) throw new InvalidDataException($"Custom selector {name} has an unsupported option count.");
                maxSelections = allowed.Length - 1;
            }
            else if (setValues.Length != 1) throw new InvalidDataException($"Custom boolean {name} has multiple options.");
            var definition = new CustomGameRuleDefinition(id, name, kind, minimum, maximum, step, maxSelections,
                Array.AsReadOnly(allowed), Array.Empty<uint>(), null, References(table.Cell(row, "ExclusiveValues")),
                References(table.Cell(row, "DependenceValues")), Flag(table.Required(row, "AlwaysReturnCurrent")));
            definition = definition with { InitialValues = ParseInitial(definition, table.Required(row, "InitValues")) };
            if (!result.TryAdd(id, new(definition, Flag(table.Required(row, "OnOff")))))
                throw new InvalidDataException($"Repeated custom rule {name}.");
        }
        foreach (var source in result.Values)
            if (source.Definition.ExclusiveIds.Concat(source.Definition.DependencyIds).Any(id => !result.ContainsKey(id)))
                throw new InvalidDataException($"Custom rule {source.Definition.Name} references a missing rule.");
        return result;
    }

    private static IReadOnlyList<uint> ParseInitial(CustomGameRuleDefinition definition, string text)
    {
        var parts = Split(text);
        var values = definition.Kind == CustomGameRuleKind.Slide ? parts.Select(Number).ToArray()
            : parts.Select(value => Flag(value) ? 1u : 0u).ToArray();
        if (definition.Kind == CustomGameRuleKind.Selection)
        {
            if (values.Length != definition.AllowedValues.Count || values.Any(value => value != 0))
                throw new InvalidDataException($"Custom selector {definition.Name} has unsupported initial selections.");
            return Array.Empty<uint>();
        }
        try { ValidateDomain(definition, values); }
        catch (ArgumentException exception) { throw new InvalidDataException($"Custom rule {definition.Name} has invalid initial values.", exception); }
        return Array.AsReadOnly(values);
    }

    private static Dictionary<int, Dictionary<ushort, string>> ReadMatrix(string name,
        IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyDictionary<ushort, SourceRule> common)
    {
        var table = new CustomGameRulesWorkbookTable(name, rows, 1, "EntryId", "Display");
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var header in rows[1].Where(value => value.StartsWith("wgt", StringComparison.Ordinal)))
        {
            if (!ModeIds.TryGetValue(header, out var mode)) throw new InvalidDataException($"Custom rule table {name} names unknown mode '{header}'.");
            columns.Add(header, mode);
        }
        var result = columns.Values.ToDictionary(mode => mode, _ => new Dictionary<ushort, string>());
        var seen = new HashSet<ushort>();
        foreach (var row in table.Rows)
        {
            var key = table.Cell(row, "EntryId");
            if (key.Length == 0) continue;
            if (!Ids.TryGetValue(key, out var id) || !common.ContainsKey(id) || !seen.Add(id))
                throw new InvalidDataException($"Custom rule matrix {name} contains an unknown or duplicate selector '{key}'.");
            foreach (var (column, mode) in columns) result[mode].Add(id, table.Cell(row, column));
        }
        return result;
    }

    private static IReadOnlyList<ushort> References(string text) => Array.AsReadOnly(Split(text).Select(name => Ids.TryGetValue(name, out var id)
        ? id : throw new InvalidDataException($"Unknown custom rule reference '{name}'.")).Distinct().ToArray());
    private static string[] Split(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static uint Number(string text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException($"Invalid unsigned custom rule value '{text}'.");
    private static bool Flag(string text, bool emptyIsFalse = false) => text switch
    {
        "1" or "TRUE" or "true" => true,
        "0" or "FALSE" or "false" => false,
        "" when emptyIsFalse => false,
        _ => throw new InvalidDataException($"Invalid custom rule boolean '{text}'."),
    };
    private static string Cell(IReadOnlyDictionary<ushort, string> cells, ushort id) => cells.GetValueOrDefault(id) ?? "";
    private static string Cell(IReadOnlyDictionary<int, Dictionary<ushort, string>> matrix, int mode, ushort id) =>
        matrix.TryGetValue(mode, out var cells) ? Cell(cells, id) : "";
    private sealed record SourceRule(CustomGameRuleDefinition Definition, bool Enabled);
}
