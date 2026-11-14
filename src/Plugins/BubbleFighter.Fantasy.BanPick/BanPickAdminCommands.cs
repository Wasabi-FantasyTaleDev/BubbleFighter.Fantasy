using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>
/// The administrators' <c>gbanpick</c> command. Every change edits the whole document through the store, so it is
/// validated like a configured document and saved under the revision it started from.
/// </summary>
internal sealed class BanPickAdminCommands(BanPickHub hub, IGameText texts)
{
    internal async ValueTask<CommandResult> ExecuteAsync(CommandInvocation invocation, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var verb = tokens.Count > 0 ? tokens[0].ToLowerInvariant() : "list";
        var state = hub.Store.Current;
        var policy = state.Policy;
        try
        {
            BanPickPolicy? next = verb switch
            {
                "list" => null,
                "groups" => null,
                "add" when tokens.Count >= 4 => Add(policy, tokens[1], tokens[2], Target(invocation, policy, Rest(tokens, 3))),
                "remove" or "rm" when tokens.Count >= 3 => Remove(policy, tokens[1], Target(invocation, policy, Rest(tokens, 2))),
                "delete" when tokens.Count == 2 => policy with { Policies = [.. policy.Policies.Where(entry => entry.Id != tokens[1])] },
                "scope" when tokens.Count >= 3 => Scope(policy, tokens[1], tokens.Skip(2)),
                "group" when tokens.Count >= 3 => Group(policy, tokens[1], tokens.Skip(2)),
                "ungroup" when tokens.Count == 2 => Ungroup(policy, tokens[1]),
                _ => throw new FormatException(),
            };
            if (verb == "list") return new(CommandStatus.Success, Describe(policy));
            if (verb == "groups")
                return new(CommandStatus.Success, policy.Groups.Count == 0 ? texts.Format("", "banpick.admin.no_groups")
                    : string.Join("; ", policy.Groups.Select(DescribeGroup)));
            if (next is null || ReferenceEquals(next, policy)) return new(CommandStatus.Success, texts.Format("", "banpick.admin.unchanged"));
            var saved = await hub.Store.SaveAsync(state.Revision, next, ct).ConfigureAwait(false);
            return new(CommandStatus.Success, texts.Format("", "banpick.admin.saved",
                new Dictionary<string, string> { ["revision"] = saved.Revision.ToString(CultureInfo.InvariantCulture) }));
        }
        catch (FormatException)
        {
            return new(CommandStatus.InvalidArguments, texts.Format("", "banpick.admin.usage"));
        }
        catch (BanPickPolicyException rejected)
        {
            return new(CommandStatus.InvalidState, texts.Format("", "banpick.admin.rejected", new Dictionary<string, string>
            {
                ["code"] = rejected.Code,
                ["where"] = string.Join(" ", new[] { rejected.Policy, rejected.Rule, rejected.Other }.OfType<string>()),
            }));
        }
        catch (NotSupportedException)
        {
            return new(CommandStatus.InvalidState, texts.Format("", "banpick.admin.read_only"));
        }
    }

    private static string Rest(IReadOnlyList<string> tokens, int from) => string.Join(' ', tokens.Skip(from));

    // A declared group, an item id, or (for a player in a room) the one item of the player's build with that name.
    private (int? Item, string? Group) Target(CommandInvocation invocation, BanPickPolicy policy, string text)
    {
        if (policy.Groups.FirstOrDefault(group => string.Equals(group.Name, text, StringComparison.OrdinalIgnoreCase)) is { } named)
            return (null, named.Name);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return (id, null);
        if (long.TryParse(invocation.Caller.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var session)
            && hub.Seated(session) is { } seated)
        {
            var matches = seated.Scope.Search(text, 2);
            var exact = matches.Where(item => string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) return (exact[0].Id, null);
            if (matches.Count == 1) return (matches[0].Id, null);
        }
        throw new BanPickPolicyException("ban_pick_unknown_target", rule: text);
    }

    // The target goes into the policy's rule of that type and leaves its other rules, so one target has one strength.
    private static BanPickPolicy Add(BanPickPolicy policy, string id, string type, (int? Item, string? Group) target)
    {
        if (!BanPickRuleTypes.TryGet(type.ToLowerInvariant(), out var ruleType)) throw new BanPickPolicyException("ban_pick_unknown_type", id);
        var entry = policy.Policies.FirstOrDefault(candidate => candidate.Id == id) ?? new BanPickPolicyEntry(id, null, null, BanPickScope.Everywhere, []);
        var rules = entry.Rules.Select(rule => Without(rule, target)).ToList();
        var index = rules.FindIndex(rule => rule.Type == ruleType.Name);
        var current = index >= 0 ? rules[index] : new BanPickRule(ruleType.Name, ruleType.Name, [], []);
        current = target.Item is { } item ? current with { Items = [.. current.Items, item] } : current with { Groups = [.. current.Groups, target.Group!] };
        if (index >= 0) rules[index] = current; else rules.Add(current);
        return Replace(policy, entry with { Rules = [.. rules.Where(rule => rule.Items.Count + rule.Groups.Count > 0)] });
    }

    private static BanPickPolicy Remove(BanPickPolicy policy, string id, (int? Item, string? Group) target)
    {
        var entry = policy.Policies.FirstOrDefault(candidate => candidate.Id == id) ?? throw new BanPickPolicyException("ban_pick_unknown_policy", id);
        return Replace(policy, entry with { Rules = [.. entry.Rules.Select(rule => Without(rule, target)).Where(rule => rule.Items.Count + rule.Groups.Count > 0)] });
    }

    private static BanPickRule Without(BanPickRule rule, (int? Item, string? Group) target) => rule with
    {
        Items = [.. rule.Items.Where(item => item != target.Item)],
        Groups = [.. rule.Groups.Where(group => group != target.Group)],
    };

    // scope <policy> modes=1,2|all rooms=all|public|password builds=kr-812,cn-324|all
    private static BanPickPolicy Scope(BanPickPolicy policy, string id, IEnumerable<string> settings)
    {
        var entry = policy.Policies.FirstOrDefault(candidate => candidate.Id == id) ?? new BanPickPolicyEntry(id, null, null, BanPickScope.Everywhere, []);
        var scope = entry.Scope;
        foreach (var (key, value) in Settings(settings))
            scope = key switch
            {
                "modes" => scope with { Modes = value == "all" ? [] : Numbers(value) },
                "rooms" => scope with { Rooms = value },
                "builds" => scope with { Builds = value == "all" ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries) },
                _ => throw new FormatException(),
            };
        return Replace(policy, entry with { Scope = scope });
    }

    // group <name> main=300 sub=304,305 function=10001 items=1,2
    private static BanPickPolicy Group(BanPickPolicy policy, string name, IEnumerable<string> settings)
    {
        var group = new BanPickGroup(name.ToLowerInvariant(), [], [], [], []);
        foreach (var (key, value) in Settings(settings))
            group = key switch
            {
                "main" => group with { MainCategories = Numbers(value) },
                "sub" => group with { SubCategories = Numbers(value) },
                "function" => group with { Functions = Numbers(value) },
                "items" => group with { Items = Numbers(value) },
                _ => throw new FormatException(),
            };
        var groups = policy.Groups.Where(existing => existing.Name != group.Name).Append(group).ToArray();
        return policy with { Groups = groups };
    }

    private static BanPickPolicy Ungroup(BanPickPolicy policy, string name)
    {
        if (policy.Policies.Any(entry => entry.Rules.Any(rule => rule.Groups.Contains(name, StringComparer.Ordinal))))
            throw new BanPickPolicyException("ban_pick_group_in_use", rule: name);
        return policy with { Groups = [.. policy.Groups.Where(group => group.Name != name)] };
    }

    private static BanPickPolicy Replace(BanPickPolicy policy, BanPickPolicyEntry entry)
    {
        var entries = policy.Policies.ToList();
        var index = entries.FindIndex(candidate => candidate.Id == entry.Id);
        if (index >= 0) entries[index] = entry; else entries.Add(entry);
        return policy with { Policies = entries };
    }

    private static IEnumerable<(string Key, string Value)> Settings(IEnumerable<string> settings)
    {
        foreach (var setting in settings)
        {
            var parts = setting.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new FormatException();
            yield return (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant());
        }
    }

    private static int[] Numbers(string value) => [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new FormatException())];

    private string Describe(BanPickPolicy policy)
    {
        if (policy.Policies.Count == 0) return texts.Format("", "banpick.admin.no_policies");
        return string.Join("; ", policy.Policies.Select(entry =>
        {
            var scope = $"builds={(entry.Scope.Builds.Count == 0 ? "all" : string.Join(',', entry.Scope.Builds))} " +
                $"modes={(entry.Scope.Modes.Count == 0 ? "all" : string.Join(',', entry.Scope.Modes))} rooms={entry.Scope.Rooms}";
            var rules = string.Join(" ", entry.Rules.Select(rule => $"{rule.Type}:[{string.Join(',', rule.Groups.Concat(rule.Items.Select(item => item.ToString(CultureInfo.InvariantCulture))))}]"));
            return $"{entry.Id} ({scope}) {rules}";
        }));
    }

    private static string DescribeGroup(BanPickGroup group)
    {
        static string List(IReadOnlyList<int> values) => string.Join(',', values);
        var parts = new List<string>();
        if (group.MainCategories.Count > 0) parts.Add("main=" + List(group.MainCategories));
        if (group.SubCategories.Count > 0) parts.Add("sub=" + List(group.SubCategories));
        if (group.Functions.Count > 0) parts.Add("function=" + List(group.Functions));
        if (group.Items.Count > 0) parts.Add("items=" + List(group.Items));
        return $"{group.Name} {string.Join(' ', parts)}";
    }
}
