using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Projects the native recruitment-control gate without changing unrelated seat flags or party membership.</summary>
internal static class NativePlazaRecruitment
{
    private static readonly ConditionalWeakTable<PacketTables, Dictionary<string, string>> Fields = new();
    private static readonly ConditionalWeakTable<PacketTables, Dictionary<string, string>> ActiveFields = new();
    private static readonly ConditionalWeakTable<PacketTables, Dictionary<string, string>> TextFields = new();

    internal static bool Allowed(ISession? recipient, int userNumber) =>
        recipient is null || !recipient.TryFeature<IPartyMembership>(out var membership)
        || !membership.Party.Members.Any(member => member.UserNum == userNumber)
        || membership.Party.Leader.UserNum == userNumber;

    internal static void Project(PacketTables tables, string build, IDictionary<string, object?> values, bool allowed)
    {
        var names = Fields.GetValue(tables, static snapshot => snapshot.Builds.ToDictionary(name => name, name =>
        {
            if (!snapshot.ValuesOf(name).TryGetValue("plaza_recruitment_field", out var raw))
                return string.Empty;
            if (raw is not string field)
                throw new ConfigException($"Build '{name}' plaza_recruitment_field must be a field name or empty.");
            if (field.Length == 0)
                return string.Empty;
            if (!snapshot.Resolve(name, "UserSlot").Any(row => row.Name == field && row.Type == "bool"))
                throw new ConfigException($"Build '{name}' plaza_recruitment_field must name a UserSlot boolean.");
            return field;
        }, StringComparer.Ordinal));
        // kr-812 sub_8F6A20 and cn-324 sub_799F90 disable recruitment unless the local seat's gate is one.
        if (names[build] is { Length: > 0 } field)
            values[field] = allowed;
    }

    internal static void Project(PacketTables tables, string build, IDictionary<string, object?> values,
        PlazaRecruitmentState? state)
    {
        if (state is null) return;
        Project(tables, build, values, state.Allowed);
        if (Field(ActiveFields, tables, build, "plaza_recruitment_active_field", "bool") is { Length: > 0 } active)
            values[active] = state.Active;
        if (Field(TextFields, tables, build, "plaza_recruitment_text_field", "wstr") is { Length: > 0 } text)
            values[text] = state.Active ? state.Text : string.Empty;
    }

    internal static void RequireActiveField(PacketTables tables, string build)
    {
        if (Field(ActiveFields, tables, build, "plaza_recruitment_active_field", "bool").Length == 0)
            throw new ConfigException($"Build '{build}' has no audited plaza recruitment flag.");
    }

    private static string Field(ConditionalWeakTable<PacketTables, Dictionary<string, string>> cache,
        PacketTables tables, string build, string setting, string type) =>
        cache.GetValue(tables, snapshot => snapshot.Builds.ToDictionary(name => name, name =>
        {
            if (!snapshot.ValuesOf(name).TryGetValue(setting, out var raw)) return string.Empty;
            if (raw is not string field || field.Length > 0
                && !snapshot.Resolve(name, "UserSlot").Any(row => row.Name == field && row.Type == type))
                throw new ConfigException($"Build '{name}' {setting} must name a UserSlot {type} field or be empty.");
            return field;
        }, StringComparer.Ordinal))[build];
}
