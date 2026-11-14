using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>The BnB timers and ability limits shipped in the selected client's game specification.</summary>
internal sealed record BnBRoundRules(int BombFuseMilliseconds, int FlameMilliseconds, int BlockDestructionMilliseconds,
    int MaximumBombCount, int MaximumExplosionRange, int MaximumSpeedLevel, float SpeedPerLevel, float BaseMovementSpeed)
{
    internal const string Path = "Config/GameSpec/BnBSpec.yaml";

    internal static BnBRoundRules Load(IClientDataStore store, int maximumFileBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1);
        var bytes = store.Read(Path, maximumFileBytes);
        if (bytes.Length == 0 || bytes.Length > maximumFileBytes) throw new InvalidDataException($"'{Path}' is empty or oversized.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            // The shipped specification is a shallow tree; reject aliases and excessive nesting before materialization.
            var parser = new Parser(new StringReader(text));
            var depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias) throw new InvalidDataException("BnB specification cannot contain YAML aliases.");
                if (parser.Current is MappingStart or SequenceStart && ++depth > 16)
                    throw new InvalidDataException("BnB specification nesting is excessive.");
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("BnB specification requires one mapping document.");
            var grid = Mapping(root, "grid");
            var ability = Mapping(root, "metaAbility");
            var bomber = Mapping(root, "bomber");
            var rules = new BnBRoundRules(Integer(grid, "bombExplodeDelayTime", 1), Integer(grid, "bombExplodeDurationTime", 1),
                Integer(grid, "blockDestroyingTime", 1), Integer(ability, "maxInstallBombCount", 1),
                Integer(ability, "maxExplosionRange", 1), Integer(ability, "maxIncreaseMovementSpeedLevel", 0),
                Single(ability, "increaseMovementSpeedPerLevel"), Single(bomber, "baseMovementSpeed"));
            rules.Validate();
            return rules;
        }
        catch (Exception exception) when (exception is YamlException or DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException("BnB specification is malformed.", exception);
        }
    }

    internal void Validate()
    {
        if (BombFuseMilliseconds <= 0 || FlameMilliseconds <= 0 || BlockDestructionMilliseconds <= 0
            || MaximumBombCount <= 0 || MaximumExplosionRange <= 0 || MaximumSpeedLevel < 0
            || !float.IsFinite(SpeedPerLevel) || SpeedPerLevel < 0 || !float.IsFinite(BaseMovementSpeed)
            || BaseMovementSpeed <= 0 || !float.IsFinite(BaseMovementSpeed + MaximumSpeedLevel * SpeedPerLevel))
            throw new InvalidDataException("BnB specification has invalid timing or ability limits.");
    }

    private static YamlNode Field(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
        ? value : throw new InvalidDataException($"BnB specification is missing '{key}'.");
    private static YamlMappingNode Mapping(YamlMappingNode node, string key) => Field(node, key) as YamlMappingNode
        ?? throw new InvalidDataException($"BnB '{key}' must be a mapping.");
    private static string Scalar(YamlMappingNode node, string key) => Field(node, key) is YamlScalarNode { Value: { } value } ? value
        : throw new InvalidDataException($"BnB '{key}' must be a scalar.");
    private static int Integer(YamlMappingNode node, string key, int minimum) => int.TryParse(Scalar(node, key), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) && value >= minimum ? value
        : throw new InvalidDataException($"BnB '{key}' has an invalid integer.");
    private static float Single(YamlMappingNode node, string key) => float.TryParse(Scalar(node, key), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) ? value
        : throw new InvalidDataException($"BnB '{key}' has an invalid float.");
}
