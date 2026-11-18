using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using YamlDotNet.RepresentationModel;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>The three prototype kinds and force-item branch consumed by the supported Zombie G rules.</summary>
internal sealed record ZombieOriginPrototypeRules(int King, int Queen, int Stein, bool IgnoreForceItem, bool QueenEnabled = true)
{
    private const string Path = "Config/GameSpec/Modes/ZombieOrigin.ZombieRule.yaml";

    internal static ZombieOriginPrototypeRules Load(IClientDataStore store)
    {
        var bytes = store.Read(Path, 64 * 1024);
        var yaml = new YamlStream();
        using var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        yaml.Load(text);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root
            || Field(root, "protoSelect") is not YamlMappingNode select
            || Field(select, "randomWeights") is not YamlMappingNode weights
            || Field(select, "ignoreForceItem") is not YamlScalarNode flag
            || !bool.TryParse(flag.Value, out var ignore) || weights.Children.Keys.Any(key =>
                key is not YamlScalarNode { Value: "zombieKing" or "succubusQueen" or "zombieStein" }))
            throw new InvalidDataException($"Unsupported prototype selection rules in {Path}.");
        var rules = new ZombieOriginPrototypeRules(Weight("zombieKing"), Weight("succubusQueen"), Weight("zombieStein"), ignore,
            Field(weights, "succubusQueen") is not null);
        // The native selector multiplies two 32-bit words before dividing by 10000.
        if ((long)rules.King + rules.Queen + rules.Stein > uint.MaxValue / 9999)
            throw new InvalidDataException($"Prototype weight total exceeds the supported client range in {Path}.");
        return rules;

        int Weight(string key)
        {
            if (Field(weights, key) is not { } node) return 0;
            return node is YamlScalarNode scalar && int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value : throw new InvalidDataException($"Invalid prototype weight {key} in {Path}.");
        }
        static YamlNode? Field(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    }

    internal int? Choose(int roll, bool hasForceItem)
    {
        // kr-1256 sub_B524C0: enabled native candidates are indexed in this order, regardless of YAML key order.
        var total = King + Queen + Stein;
        if (total == 0) return null;
        if (!IgnoreForceItem && QueenEnabled && hasForceItem) return 203;
        var index = (long)total * roll / 10000;
        return index < King ? 201 : index < King + Queen ? 203 : 212;
    }
}
