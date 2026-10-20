using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// Lays a legacy client build's lobby facts from <c>packets/builds.yaml</c> over a row's options, so a build
/// based on another inherits them: the <c>character_creation</c> capability decides whether native creation
/// runs, and the <c>lobby</c> values give the creation stage's character types and the starter bag.
/// </summary>
internal static class LobbyBuildValues
{
    /// <summary>Applies the build's values to <paramref name="options"/> and validates the result.</summary>
    /// <param name="options">The bound row options; <see cref="LobbyOptions.CharacterCreation"/> asks for creation where the build has it.</param>
    /// <param name="lineage">The row's client build lineage, nearest first.</param>
    /// <returns>The packet-table build whose values were applied.</returns>
    /// <exception cref="ConfigException">Thrown when the lineage names no build of the tables, a value is missing or malformed, or the result fails validation.</exception>
    public static string Apply(LobbyOptions options, IReadOnlyList<string> lineage)
    {
        var tables = PacketTables.Load(options.PacketsDirectory);
        var build = tables.NearestBuild(lineage)
            ?? throw new ConfigException($"lobby: no build of lineage [{string.Join(", ", lineage)}] is named in '{options.PacketsDirectory}'.");
        if (!tables.ValuesOf(build).TryGetValue("lobby", out var raw) || raw is not IReadOnlyDictionary<string, object?> lobby)
            throw new ConfigException($"Build '{build}' must define lobby values in packets/builds.yaml.");
        options.CreationCharacterTypes = Ids(lobby, "creation_character_types");
        options.StarterItemIds = Ids(lobby, "starter_item_ids");
        options.StarterWornIds = Ids(lobby, "starter_worn_ids");
        options.CharacterCreation = options.CharacterCreation && tables.CapabilityOf(build, "character_creation");
        if (options.LifeItems && !tables.CapabilityOf(build, "life_items"))
            throw new ConfigException($"life_items requires a build with the life_items capability; '{build}' has none.");
        var errors = options.Validate().Select(error => error.ErrorMessage).ToList();
        if (errors.Count > 0)
            throw new ConfigException($"lobby values of build '{build}': {string.Join(" ", errors)}");
        return build;

        int[] Ids(IReadOnlyDictionary<string, object?> map, string key) =>
            map.TryGetValue(key, out var value) && value is string[] items
                ? items.Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id
                    : throw new ConfigException($"Build '{build}': lobby.{key} must list whole numbers, not '{item}'.")).ToArray()
                : throw new ConfigException($"Build '{build}' must define the list lobby.{key} in packets/builds.yaml.");
    }
}
