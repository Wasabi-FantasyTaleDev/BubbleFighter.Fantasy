using BubbleFighter.Fantasy.Contracts.Launcher;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class ClientPolicyRulesTests
{
    private static readonly ClientPolicySource Official = new("official", "alist", "https://store.example.invalid", "store/catalog.toml");

    private static ClientPolicy One(params ClientPolicyRule[] rules) => new([Official], [new("plugin.id", null, null, rules)]);

    private static ClientPolicyRule Rule(string id, string type, string[]? builds = null, string? version = null, string[]? sources = null) =>
        new(id, type, builds ?? [], version, sources);

    [Fact]
    public void EveryTypeWithItsOwnFieldIsAccepted()
    {
        ClientPolicyRules.Validate(One(
            Rule("a", "require"), Rule("b", "recommend", ["cn-774"], sources: ["official"]), Rule("c", "recommend", ["kr-1256"], sources: []),
            Rule("d", "min_version", version: "1.0.0"), Rule("e", "max_version", version: "2.0.0-beta.1")));
        ClientPolicyRules.Validate(One(Rule("a", "latest", ["cn-774"]), Rule("b", "disable", ["kr-1256"]), Rule("c", "require", ["cn-774"])));
        ClientPolicyRules.Validate(ClientPolicy.Empty);
    }

    [Theory]
    [InlineData("disable", "require")]
    [InlineData("disable", "recommend")]
    [InlineData("disable", "min_version")]
    [InlineData("disable", "max_version")]
    [InlineData("disable", "latest")]
    [InlineData("latest", "max_version")]
    [InlineData("require", "require")]
    public void ConflictingTypesOnOverlappingBuildsAreRejected(string first, string second)
    {
        var a = Typed("a", first, ["kr-1256", "cn-774"]);
        var b = Typed("b", second, ["cn-774"]);
        var error = Assert.Throws<ClientPolicyException>(() => ClientPolicyRules.Validate(One(a, b)));
        Assert.Equal(("client_policy_conflict", "plugin.id", "a", "b"), (error.Code, error.Plugin, error.Rule, error.Other));
        // Every build is an overlap too; separate builds are not.
        Assert.Throws<ClientPolicyException>(() => ClientPolicyRules.Validate(One(a with { Builds = [] }, b)));
        ClientPolicyRules.Validate(One(a with { Builds = ["kr-1256"] }, b));
    }

    [Fact]
    public void AMinimumAboveTheMaximumConflictsButEqualBoundsPinOneVersion()
    {
        Assert.Equal("client_policy_conflict", Assert.Throws<ClientPolicyException>(() =>
            ClientPolicyRules.Validate(One(Rule("lo", "min_version", version: "2.0.1"), Rule("hi", "max_version", version: "2.0.0")))).Code);
        Assert.Throws<ClientPolicyException>(() =>
            ClientPolicyRules.Validate(One(Rule("hi", "max_version", version: "2.0.0"), Rule("lo", "min_version", version: "3.0.0+build.1"))));
        ClientPolicyRules.Validate(One(Rule("lo", "min_version", version: "2.0.0"), Rule("hi", "max_version", version: "2.0.0")));
        ClientPolicyRules.Validate(One(Rule("lo", "min_version", version: "2.0.0-rc.1"), Rule("hi", "max_version", version: "2.0.0")));
    }

    [Theory]
    [InlineData("unknown-type", "client_policy_unknown_type")]
    [InlineData("unknown-source", "client_policy_unknown_source")]
    [InlineData("missing-version", "client_policy_invalid")]
    [InlineData("stray-version", "client_policy_invalid")]
    [InlineData("loose-version", "client_policy_invalid")]
    [InlineData("stray-sources", "client_policy_invalid")]
    [InlineData("duplicate-rule", "client_policy_invalid")]
    [InlineData("duplicate-build", "client_policy_invalid")]
    [InlineData("path-build", "client_policy_invalid")]
    [InlineData("bad-rule-id", "client_policy_invalid")]
    [InlineData("duplicate-plugin", "client_policy_invalid")]
    [InlineData("control-name", "client_policy_invalid")]
    [InlineData("source-credentials", "client_policy_invalid")]
    [InlineData("source-traversal", "client_policy_invalid")]
    [InlineData("source-kind", "client_policy_invalid")]
    [InlineData("duplicate-source", "client_policy_invalid")]
    public void MalformedPoliciesNameTheirReason(string scenario, string code)
    {
        var policy = scenario switch
        {
            "unknown-type" => One(Rule("a", "teleport")),
            "unknown-source" => One(Rule("a", "recommend", sources: ["elsewhere"])),
            "missing-version" => One(Rule("a", "min_version")),
            "stray-version" => One(Rule("a", "require", version: "1.0.0")),
            "loose-version" => One(Rule("a", "max_version", version: "1.0")),
            "stray-sources" => One(Rule("a", "latest", sources: ["official"])),
            "duplicate-rule" => One(Rule("a", "require", ["kr-1256"]), Rule("a", "latest", ["cn-774"])),
            "duplicate-build" => One(Rule("a", "require", ["kr-1256", "kr-1256"])),
            "path-build" => One(Rule("a", "require", ["../kr-1256"])),
            "bad-rule-id" => One(Rule("a b", "require")),
            "duplicate-plugin" => new ClientPolicy([], [new("same", null, null, []), new("same", null, null, [])]),
            "control-name" => new ClientPolicy([], [new("plugin.id", "bad\nlabel", null, [])]),
            "source-credentials" => new ClientPolicy([Official with { BaseUrl = "https://user:pass@store.example.invalid" }], []),
            "source-traversal" => new ClientPolicy([Official with { CatalogPath = "../catalog.toml" }], []),
            "source-kind" => new ClientPolicy([Official with { Kind = "ftp" }], []),
            _ => new ClientPolicy([Official, Official], []),
        };
        Assert.Equal(code, Assert.Throws<ClientPolicyException>(() => ClientPolicyRules.Validate(policy)).Code);
    }

    [Fact]
    public void LargePoliciesHaveNoCountLimit()
    {
        var sources = Enumerable.Range(0, 200).Select(i => Official with { Name = $"s{i}" }).ToArray();
        var plugins = Enumerable.Range(0, 1000).Select(i => new ClientPluginRules($"p.{i}", null, null,
            Enumerable.Range(0, 40).Select(r => Rule($"r{r}", "recommend", Enumerable.Range(0, 100).Select(b => $"b-{r}-{b}").ToArray(),
                sources: sources.Select(source => source.Name).ToArray())).ToArray())).ToArray();
        ClientPolicyRules.Validate(new ClientPolicy(sources, plugins));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-2", "1.0.0-10", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void VersionsCompareBySemVerPrecedence(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(ClientPolicyVersion.Compare(left, right)));

    [Fact]
    public void RuleTypesListTheirFieldsAndConflicts()
    {
        Assert.Equal(["require", "disable", "recommend", "min_version", "max_version", "latest"], ClientPolicyRuleTypes.All.Select(t => t.Name));
        Assert.Equal([ClientPolicyRuleField.None, ClientPolicyRuleField.None, ClientPolicyRuleField.Sources, ClientPolicyRuleField.Version,
            ClientPolicyRuleField.Version, ClientPolicyRuleField.None], ClientPolicyRuleTypes.All.Select(t => t.Field));
        Assert.False(ClientPolicyRuleTypes.TryGet("teleport", out _));
    }

    private static ClientPolicyRule Typed(string id, string type, string[] builds) => type switch
    {
        "recommend" => Rule(id, type, builds, sources: []),
        "min_version" or "max_version" => Rule(id, type, builds, version: "1.0.0"),
        _ => Rule(id, type, builds),
    };
}
