using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Tests for <see cref="ProfileComposer"/> layering and env interpolation.</summary>
public sealed class ProfileComposerTests : IDisposable
{
    private readonly string _root;

    public ProfileComposerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bundles"));
        Directory.CreateDirectory(Path.Combine(_root, "demo"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteProfile(string profileYaml, Dictionary<string, string>? extra = null)
    {
        Write("demo/profile.yaml", profileYaml);
        if (extra is not null)
            foreach (var (path, content) in extra)
                Write(path, content);
    }

    [Fact]
    public void ProfileNamesRemainSeparateFromConfigurationIdentity()
    {
        WriteProfile("client_build: kr:812:0\ndisplay_names:\n  en: Korea 2015-04-22\n  ko: 한국 2015-04-22\n");
        var profile = Assert.Single(new ProfileComposer(_root).ListProfiles());
        Assert.Equal("demo", profile.Id);
        Assert.Equal("한국 2015-04-22", profile.DisplayNames["ko"]);
        Assert.True(profile.IsClientBuild);
        WriteProfile("display_names: [invalid]\n");
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).ListProfiles());
        WriteProfile("display_names: {en: ''}\n");
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).ListProfiles());
    }

    [Fact]
    public void SelectedBuildsReuseSharedRowsAndResolveIndependentDataRoots()
    {
        Write("bundles/shared.yaml", "- id: storage\n  plugin: p.storage\n");
        Write("bundles/client.yaml", """
            - id: listener
              plugin: p.listener
              config: {root: "${DATA_ROOT}/${BFF_PROFILE_ID}"}
            - id: child
              plugin: p.child
              parent_id: listener
            """);
        WriteProfile("bundles: [shared]\n");
        Write("one/profile.yaml", "client_build: kr:812:0\nbundles: [shared, client]\n");
        Write("two/profile.yaml", "client_build: cn:324:0\nbundles: [shared, client]\n");
        Write("unused/profile.yaml", "client_build: cn:717:0\nbundles: [shared, client]\n");
        var composer = new ProfileComposer(_root);
        var rows = composer.ComposeBuilds("demo", ["one", "two"], [], (build, _) => "/data/" + build);
        Assert.Equal(["storage", "one.listener", "one.child", "two.listener", "two.child"], rows.Select(r => r.Id));
        Assert.Equal("/data/one/one", rows[1].Config!["root"]);
        Assert.Equal("/data/two/two", rows[3].Config!["root"]);
        Assert.Equal("one.listener", rows[2].ParentId);
        Assert.Equal(ClientBuild.Parse("cn:324:0"), rows[3].ClientBuild);
        Assert.Equal(["storage"], composer.ComposeBuilds("demo", [], []).Select(r => r.Id));
        Assert.Throws<ConfigException>(() => composer.ComposeBuilds("demo", ["one", "one"], []));
        Assert.Throws<ConfigException>(() => composer.ComposeBuilds("demo", ["demo"], []));
        Assert.Throws<ConfigException>(() => composer.ComposeBuilds("demo", ["../outside"], []));
    }

    /// <summary>A server profile keeps its shared rows once and renames each build's own rows under that build.</summary>
    [Fact]
    public void BuildsComposeSharedRowsOnceAndPrefixBuildRows()
    {
        Write("bundles/core.yaml", """
            - id: shared
              plugin: p.shared
            """);
        Write("bundles/one.yaml", """
            - id: listener
              plugin: p.listener
              config: {port: 1}
            - id: child
              plugin: p.child
              parent_id: listener
            """);
        Write("bundles/two.yaml", """
            - id: listener
              plugin: p.listener
              config: {port: 2}
            """);
        Write("one/profile.yaml", "client_build: kr:812:0\nbundles: [core, one]\n");
        Write("two/profile.yaml", "client_build: cn:324:0\nbundles: [core, two]\n");
        WriteProfile("bundles: [core]\nbuilds: [one, two]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(["shared", "one.listener", "one.child", "two.listener"], rows.Select(row => row.Id));
        Assert.Null(rows[0].ClientBuild);
        Assert.Equal(ClientBuild.Parse("kr:812:0"), rows[1].ClientBuild);
        Assert.Equal("one.listener", rows[2].ParentId);
        Assert.Equal(ClientBuild.Parse("cn:324:0"), rows[3].ClientBuild);
    }

    /// <summary>One bundle serves two builds: each profile's id, build and vars fill it, and set changes one key.</summary>
    [Fact]
    public void ProfileVariablesFillASharedBundleAndSetChangesSingleKeys()
    {
        Write("bundles/family.yaml", """
            - id: listener
              plugin: p.listener
              config:
                listen: "0.0.0.0:${PORT}"
                protocol: "${BFF_PROFILE_ID}/game"
                major: "${BFF_BUILD_MAJOR}"
                region: "${BFF_BUILD_REGION}"
                seats: "${SEATS:-12}"
            """);
        Write("one/profile.yaml", "client_build: kr:812:0\nbundles: [family]\nvars: {PORT: 7110}\n");
        Write("two/profile.yaml", "client_build: cn:324:0\nbundles: [family]\nvars: {PORT: 7140, SEATS: 8}\npatches: [patch.yaml]\n");
        Write("two/patch.yaml", """
            - set:
                id: listener
                config:
                  region: "${BFF_BUILD_REGION}-own"
                  major: ~
            """);
        WriteProfile("builds: [one, two]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        var one = rows.Single(row => row.Id == "one.listener").Config!;
        var two = rows.Single(row => row.Id == "two.listener").Config!;
        Assert.Equal(("0.0.0.0:7110", "one/game", "812", "kr", "12"), (one["listen"], one["protocol"], one["major"], one["region"], one["seats"]));
        Assert.Equal(("0.0.0.0:7140", "two/game", "cn-own", "8"), (two["listen"], two["protocol"], two["region"], two["seats"]));
        Assert.False(two.ContainsKey("major"));
    }

    /// <summary>Profile port variables resolve environment overrides before bundle interpolation.</summary>
    [Fact]
    public void ProfileVariablesResolveDeploymentEnvironment()
    {
        var name = "BFF_TEST_PORT_" + Guid.NewGuid().ToString("N");
        Write("bundles/family.yaml", "- id: listener\n  plugin: p.listener\n  config: {listen: '0.0.0.0:${PORT}'}\n");
        WriteProfile("bundles: [family]\nvars: {PORT: '${" + name + ":-7140}'}\n");
        var composer = new ProfileComposer(_root);
        Assert.Equal("0.0.0.0:7140", composer.Compose("demo", [])[0].Config!["listen"]);
        try
        {
            Environment.SetEnvironmentVariable(name, "17140");
            Assert.Equal("0.0.0.0:17140", composer.Compose("demo", [])[0].Config!["listen"]);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    /// <summary>A reserved or malformed variable name, a non-scalar value and a set on an unknown row all fail.</summary>
    [Theory]
    [InlineData("vars: {BFF_PROFILE_ID: x}\n", null)]
    [InlineData("vars: {\"a-b\": x}\n", null)]
    [InlineData("vars: {LIST: [1]}\n", null)]
    [InlineData("patches: [patch.yaml]\n", "- set: {id: nowhere, config: {a: 1}}\n")]
    [InlineData("patches: [patch.yaml]\n", "- set: {id: listener, plugin: other}\n")]
    public void MalformedVariablesAndSetOperationsFail(string profileTail, string? patch)
    {
        Write("bundles/family.yaml", "- id: listener\n  plugin: p.listener\n");
        WriteProfile("bundles: [family]\n" + profileTail, patch is null ? null : new() { ["demo/patch.yaml"] = patch });
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
    }

    /// <summary>A build listed by a server profile cannot itself list builds.</summary>
    [Fact]
    public void NestedBuildListsAreRejected()
    {
        Write("inner/profile.yaml", "builds: [demo]\n");
        WriteProfile("builds: [inner]\n");
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("itself lists builds", error.Message);
    }

    /// <summary>Preserves runtime parents and isolation across patches and emits their effective configuration.</summary>
    [Fact]
    public void RuntimeTreeFieldsComposePatchAndAppearInDump()
    {
        Write("bundles/a.yaml", """
            - id: parent
              plugin: p.a
              isolated_services: [Sample.IValue]
            - id: child
              plugin: p.b
              parent_id: parent
            """);
        Write("demo/patch.yaml", "- replace: {id: child, config: {value: 3}}");
        WriteProfile("""
            bundles: [a]
            patches: [patch.yaml]
            """);
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(["Sample.IValue"], rows[0].IsolatedServices);
        Assert.Equal("parent", rows[1].ParentId);
        Assert.Contains("parent_id: \"parent\"", ProfileComposer.Dump(rows));
        Assert.Contains("isolated_services: [\"Sample.IValue\"]", ProfileComposer.Dump(rows));
        Write("demo/patch.yaml", """
            - replace: {id: parent, isolated_services: []}
            - replace: {id: child, parent_id: null}
            """);
        var reset = new ProfileComposer(_root).Compose("demo", []);
        Assert.Empty(reset[0].IsolatedServices);
        Assert.Null(reset[1].ParentId);
    }

    /// <summary>Rejects invalid parent and isolation syntax before a host can admit the composed rows.</summary>
    [Theory]
    [InlineData("parent_id: []")]
    [InlineData("parent_id: ''")]
    [InlineData("isolated_services: null")]
    [InlineData("isolated_services: Sample.IValue")]
    [InlineData("isolated_services: [Sample.IValue, Sample.IValue]")]
    [InlineData("isolated_services: [null]")]
    public void MalformedRuntimeTreeFieldsFailDuringComposition(string field)
    {
        Write("bundles/a.yaml", $"- {{id: row, plugin: p.a, {field}}}");
        WriteProfile("bundles: [a]");
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
    }

    /// <summary>Distinguishes explicit string values from YAML null when reading parent ids and domain configuration.</summary>
    [Theory]
    [InlineData("'null'")]
    [InlineData("\"null\"")]
    [InlineData("!!str null")]
    public void QuotedOrExplicitStringNullRemainsALiteral(string scalar)
    {
        Write("bundles/a.yaml", $"- {{id: child, plugin: p.a, parent_id: {scalar}, config: {{value: {scalar}}}}}");
        WriteProfile("bundles: [a]");
        var row = Assert.Single(new ProfileComposer(_root).Compose("demo", []));
        Assert.Equal("null", row.ParentId);
        Assert.Equal("null", row.Config!["value"]);
    }

    /// <summary>Expands groups depth-first after bundles, stamps group provenance and still applies patches last.</summary>
    [Fact]
    public void GroupsComposeNestedBundlesAndInlineRowsInOrder()
    {
        Write("bundles/a.yaml", "- {id: from-bundle, plugin: p.a}");
        Write("bundles/b.yaml", "- {id: group-bundle, plugin: p.b}");
        Write("groups/outer.yaml", """
            bundles: [b]
            groups: [inner]
            rows:
              - {id: outer-row, plugin: p.outer}
            """);
        Write("groups/inner.yaml", """
            rows:
              - {id: inner-row, plugin: p.inner}
              - {id: inner-extra, plugin: p.inner}
            """);
        Write("demo/patch.yaml", "- replace: {id: inner-row, config: {value: 7}}");
        WriteProfile("""
            bundles: [a]
            groups: [outer]
            patches: [patch.yaml]
            """);

        var rows = new ProfileComposer(_root).Compose("demo", []);

        Assert.Equal(["from-bundle", "group-bundle", "inner-row", "inner-extra", "outer-row"], rows.Select(row => row.Id).ToArray());
        Assert.Equal(["bundle:a", "group:outer", "patch:patch.yaml", "group:inner", "group:outer"], rows.Select(row => row.Source).ToArray());
        Assert.Equal("7", rows[2].Config!["value"]);
        var dump = ProfileComposer.Dump(rows);
        Assert.Contains("source: group:outer", dump);
        Assert.Contains("source: patch:patch.yaml", dump);
        Assert.Contains("source: bundle:a", dump);
    }

    /// <summary>The dump is valid YAML that reads back every string value unchanged, quoting only where needed.</summary>
    [Fact]
    public void DumpQuotesStringsThatPlainYamlWouldMisread()
    {
        Write("bundles/a.yaml", """
            - id: tricky
              plugin: p.tricky
              config:
                colon: "Native weapon 201014: L3 HC"
                hash: "a #b"
                dash: "-starts"
                empty: ""
                tilde: "~"
                quote: 'say "hi" \ back'
                lines: "one\ntwo"
                plain: plain-text
                number: 7110
                flag: true
                list: ["x: y", ok]
                nested: {inner: "k: v"}
            """);
        WriteProfile("bundles: [a]");
        var rows = new ProfileComposer(_root).Compose("demo", []);

        var dump = ProfileComposer.Dump(rows);
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(dump));
        var row = (YamlDotNet.RepresentationModel.YamlMappingNode)((YamlDotNet.RepresentationModel.YamlSequenceNode)stream.Documents[0].RootNode)[0];
        var config = (YamlDotNet.RepresentationModel.YamlMappingNode)row["config"];
        string Text(YamlDotNet.RepresentationModel.YamlNode node) => ((YamlDotNet.RepresentationModel.YamlScalarNode)node).Value!;

        Assert.Equal("Native weapon 201014: L3 HC", Text(config["colon"]));
        Assert.Equal("a #b", Text(config["hash"]));
        Assert.Equal("-starts", Text(config["dash"]));
        Assert.Equal("", Text(config["empty"]));
        Assert.Equal("~", Text(config["tilde"]));
        Assert.Equal("say \"hi\" \\ back", Text(config["quote"]));
        Assert.Equal("one\ntwo", Text(config["lines"]));
        Assert.Equal("x: y", Text(((YamlDotNet.RepresentationModel.YamlSequenceNode)config["list"])[0]));
        Assert.Equal("k: v", Text(((YamlDotNet.RepresentationModel.YamlMappingNode)config["nested"])["inner"]));
        // Values that read back unchanged stay plain, so line-oriented readers of the dump still work.
        Assert.Contains("    plain: plain-text\n", dump);
        Assert.Contains("    number: 7110\n", dump);
        Assert.Contains("    flag: true\n", dump);
    }

    /// <summary>Group cycles and over-deep nesting fail with the offending chain instead of recursing.</summary>
    [Fact]
    public void GroupCyclesAreRejectedWithTheOffendingChain()
    {
        Write("groups/first.yaml", "groups: [second]");
        Write("groups/second.yaml", "groups: [first]");
        Write("groups/self.yaml", "groups: [self]");
        WriteProfile("groups: [first]");
        var composer = new ProfileComposer(_root);

        var cycle = Assert.Throws<ConfigException>(() => composer.Compose("demo", []));
        Assert.Contains("group cycle", cycle.Message);
        Assert.Contains("first -> second -> first", cycle.Message);

        WriteProfile("groups: [self]");
        Assert.Contains("group cycle", Assert.Throws<ConfigException>(() => composer.Compose("demo", [])).Message);
    }

    /// <summary>A legal chain that only exceeds the nesting bound is rejected by depth, not by cycle.</summary>
    [Fact]
    public void OverDeepGroupNestingIsRejectedByDepth()
    {
        for (var index = 0; index <= 17; index++)
            Write($"groups/chain-{index}.yaml", index == 17 ? "rows: []" : $"groups: [chain-{index + 1}]");
        WriteProfile("groups: [chain-0]");

        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("nesting depth 16", error.Message);
        Assert.DoesNotContain("cycle", error.Message);
    }

    /// <summary>Legal acyclic fan-out that re-expands shared groups is bounded by the expansion budget.</summary>
    [Fact]
    public void GroupExpansionBudgetBoundsAcyclicFanOut()
    {
        // A complete binary tree of 511 distinct, acyclic group files: legal per the cycle check,
        // rejected once total expansions cross the composition budget.
        for (var depth = 0; depth < 9; depth++)
            for (var node = 0; node < 1 << depth; node++)
            {
                var left = $"node-{depth + 1}-{2 * node}";
                var right = $"node-{depth + 1}-{2 * node + 1}";
                Write($"groups/node-{depth}-{node}.yaml", depth == 8
                    ? "rows: []"
                    : $"groups: [{left}, {right}]");
            }
        WriteProfile("groups: [node-0-0]");

        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("expansion group budget", error.Message);
        Assert.DoesNotContain("cycle", error.Message);
    }

    /// <summary>Group files reject unknown keys, malformed shapes and missing references before any row is used.</summary>
    [Theory]
    [InlineData("rows: []\nextra: 1", "unknown key 'extra'")]
    [InlineData("- id: x", "must be a YAML mapping")]
    [InlineData("rows: {id: x}", "'rows' must be a list")]
    [InlineData("bundles: [missing]", "references bundle 'missing'")]
    [InlineData("groups: [ghost]", "references group 'ghost'")]
    public void GroupSchemaViolationsFailLoud(string content, string expected)
    {
        Write("groups/broken.yaml", content);
        WriteProfile("groups: [broken]");

        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains(expected, error.Message);
    }

    /// <summary>Every explicitly supplied non-list rows value identifies the exact field and offending nested group file.</summary>
    [Theory]
    [InlineData("rows: null")]
    [InlineData("rows: ~")]
    [InlineData("rows:")]
    [InlineData("rows: 'null'")]
    [InlineData("rows: {id: x}")]
    [InlineData("rows: not-a-list")]
    [InlineData("rows: 7")]
    public void ExplicitGroupRowsRequireAListAndReportTheExactGroupPath(string content)
    {
        Write("groups/outer.yaml", "groups: [shape]");
        Write("groups/shape.yaml", content);
        WriteProfile("groups: [outer]");

        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));

        var groupFile = Path.Combine(_root, "groups", "shape.yaml");
        Assert.Equal($"Group file '{groupFile}': 'rows' must be a list of row mappings.", error.Message);
    }

    /// <summary>Omitted or empty rows preserve composition order and provenance without changing legacy nullable name lists.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("rows: []")]
    [InlineData("bundles: null\ngroups: null")]
    [InlineData("bundles: null\ngroups: null\nrows: []")]
    public void OmittedOrEmptyGroupRowsPreserveOrderingAndProvenance(string content)
    {
        Write("bundles/base.yaml", "- {id: before, plugin: p.before}");
        Write("groups/empty.yaml", content);
        Write("groups/outer.yaml", "groups: [empty]\nrows: [{id: after, plugin: p.after}]");
        WriteProfile("bundles: [base]\ngroups: [outer]");

        var rows = new ProfileComposer(_root).Compose("demo", []);

        Assert.Equal(["before", "after"], rows.Select(row => row.Id).ToArray());
        Assert.Equal(["bundle:base", "group:outer"], rows.Select(row => row.Source).ToArray());
    }

    /// <summary>A missing group file is a load-time error, not a silently skipped entry.</summary>
    [Fact]
    public void MissingGroupFileFailsLoud()
    {
        WriteProfile("groups: [absent]");
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("references group 'absent'", error.Message);
    }

    [Fact]
    public void LifecycleSettingsComposeValidateAndAppearInDump()
    {
        Write("bundles/a.yaml", "- id: row\n  plugin: p.a\n  lifecycle:\n    effect_capacity: 17\n    teardown_timeout_ms: 250\n    reconcile_attempt_limit: 3\n");
        Write("demo/patch.yaml", "- replace:\n    id: row\n    config: {value: 1}\n");
        WriteProfile("bundles: [a]\npatches: [patch.yaml]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(17, rows[0].Lifecycle.EffectCapacity);
        Assert.Equal(250, rows[0].Lifecycle.TeardownTimeoutMs);
        Assert.Equal(3, rows[0].Lifecycle.ReconcileAttemptLimit);
        Assert.Contains("effect_capacity: 17", ProfileComposer.Dump(rows));
        Write("demo/patch.yaml", "- replace:\n    id: row\n    lifecycle: {effect_capacity: 23, teardown_timeout_ms: 600}\n");
        var replaced = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(23, replaced[0].Lifecycle.EffectCapacity);
        Assert.Equal(600, replaced[0].Lifecycle.TeardownTimeoutMs);
        Assert.Equal(32, replaced[0].Lifecycle.ReconcileAttemptLimit);
    }

    [Fact]
    public void ConfigurationSnapshotBudgetsComposeAndAppearInDump()
    {
        Write("bundles/a.yaml", """
            - id: row
              plugin: p.a
              lifecycle:
                configuration_depth_limit: 7
                configuration_node_limit: 120
                configuration_collection_limit: 12
                configuration_text_limit: 800
            """);
        WriteProfile("bundles: [a]");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        var limits = Assert.Single(rows).Lifecycle;
        Assert.Equal(7, limits.ConfigurationDepthLimit);
        Assert.Equal(120, limits.ConfigurationNodeLimit);
        Assert.Equal(12, limits.ConfigurationCollectionLimit);
        Assert.Equal(800, limits.ConfigurationTextLimit);
        var dump = ProfileComposer.Dump(rows);
        Assert.Contains("configuration_depth_limit: 7", dump);
        Assert.Contains("configuration_node_limit: 120", dump);
        Assert.Contains("configuration_collection_limit: 12", dump);
        Assert.Contains("configuration_text_limit: 800", dump);
    }

    /// <summary>Refresh policy uses canonical YAML values and survives row snapshot capture and dump round trips.</summary>
    [Theory]
    [InlineData("allow", PluginCodeRefreshPolicy.Allow)]
    [InlineData("restart_required", PluginCodeRefreshPolicy.RestartRequired)]
    public void CodeRefreshPolicyComposesSnapshotsAndDumps(string value, PluginCodeRefreshPolicy expected)
    {
        Write("bundles/a.yaml", $"- id: row\n  plugin: p.a\n  lifecycle: {{code_refresh_policy: {value}}}\n");
        WriteProfile("bundles: [a]\n");
        var row = Assert.Single(new ProfileComposer(_root).Compose("demo", []));
        var snapshot = PluginRowSnapshot.Capture(row);
        Assert.Equal(expected, snapshot.Lifecycle.CodeRefreshPolicy);
        var dumped = ProfileComposer.Dump([snapshot]);
        Assert.Contains($"code_refresh_policy: {value}", dumped);
        Write("bundles/a.yaml", dumped);
        Assert.Equal(expected, Assert.Single(new ProfileComposer(_root).Compose("demo", [])).Lifecycle.CodeRefreshPolicy);
    }

    /// <summary>Omission defaults to allow, while interpolation and lifecycle patch replacement preserve explicit policy choices.</summary>
    [Fact]
    public void CodeRefreshPolicyDefaultsInterpolatesAndRespectsLifecyclePatches()
    {
        Write("bundles/a.yaml", "- id: row\n  plugin: p.a\n");
        WriteProfile("bundles: [a]\n");
        var composer = new ProfileComposer(_root);
        Assert.Equal(PluginCodeRefreshPolicy.Allow, Assert.Single(composer.Compose("demo", [])).Lifecycle.CodeRefreshPolicy);
        var variable = "BFF_POLICY_" + Guid.NewGuid().ToString("N");
        Write("bundles/a.yaml", "- id: row\n  plugin: p.a\n  lifecycle: {code_refresh_policy: '${" + variable + ":-restart_required}'}\n");
        Write("demo/patch.yaml", "- replace:\n    id: row\n    config: {value: 1}\n");
        WriteProfile("bundles: [a]\npatches: [patch.yaml]\n");
        Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, Assert.Single(composer.Compose("demo", [])).Lifecycle.CodeRefreshPolicy);
        Write("demo/patch.yaml", "- replace:\n    id: row\n    lifecycle: {code_refresh_policy: allow}\n");
        Assert.Equal(PluginCodeRefreshPolicy.Allow, Assert.Single(composer.Compose("demo", [])).Lifecycle.CodeRefreshPolicy);
    }

    /// <summary>Invalid policy values fail with the row, file and lifecycle field identified, including disabled entries.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("'1'")]
    [InlineData("'99'")]
    [InlineData("true")]
    [InlineData("[allow]")]
    [InlineData("{value: allow}")]
    [InlineData("''")]
    [InlineData("RestartRequired")]
    [InlineData("allow_refresh")]
    public void InvalidCodeRefreshPolicyFailsBeforeActivation(string value)
    {
        Write("bundles/a.yaml", $"- id: row\n  plugin: p.a\n  disabled: true\n  lifecycle: {{code_refresh_policy: {value}}}\n");
        WriteProfile("bundles: [a]\n");
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("row", error.Message);
        Assert.Contains("a.yaml", error.Message);
        Assert.Contains("lifecycle.code_refresh_policy", error.Message);
    }

    /// <summary>Programmatic lifecycle values also reject undefined enum members at snapshot admission and dump.</summary>
    [Fact]
    public void UndefinedProgrammaticCodeRefreshPolicyCannotEnterASnapshot()
    {
        var row = new PluginRow
        {
            Id = "invalid-policy", Plugin = "p.a", Source = "test",
            Lifecycle = new() { CodeRefreshPolicy = (PluginCodeRefreshPolicy)99 }
        };
        Assert.Throws<ConfigException>(() => PluginRowSnapshot.Capture(row));
        Assert.Throws<ConfigException>(() => ProfileComposer.Dump([row]));
        var bound = row with
        {
            Lifecycle = new(),
            Config = new Dictionary<string, object?> { ["code_refresh_policy"] = "99" }
        };
        Assert.Throws<ConfigException>(() => new PluginConfig(bound).Bind<PluginLifecycleOptions>());
    }

    /// <summary>Configuration reconciliation carries the accepted policy without requesting a code refresh or mutating older snapshots.</summary>
    [Fact]
    public async Task CodeRefreshPolicySurvivesAcceptedReconciliationSnapshots()
    {
        Write("bundles/a.yaml", "- id: row\n  plugin: p.a\n  lifecycle: {code_refresh_policy: allow}\n");
        WriteProfile("bundles: [a]\n");
        var composer = new ProfileComposer(_root);
        var initial = Assert.Single(composer.Compose("demo", []));
        var factory = new PolicyCaptureFactory();
        var descriptor = new PluginDescriptor(new PluginAttribute("p.a"), factory, "profile-policy-test",
            "1.0.0", new Dictionary<string, string>());
        var host = new PluginHost([initial], PluginCatalog.FromDefinitions([descriptor]), NullLoggerFactory.Instance);
        var limit = TimeSpan.FromSeconds(10);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(limit);
            Write("bundles/a.yaml", "- id: row\n  plugin: p.a\n  lifecycle: {code_refresh_policy: restart_required}\n");
            var candidate = composer.Compose("demo", []).ToArray();
            var accepted = Assert.Single(candidate);
            var changed = await host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(limit);
            Assert.Equal(PluginTransitionOutcome.Converged, changed.Outcome);
            Assert.Equal(2, factory.Rows.Count);
            Assert.Equal(PluginCodeRefreshPolicy.Allow, factory.Rows[0].Lifecycle.CodeRefreshPolicy);
            Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, factory.Rows[1].Lifecycle.CodeRefreshPolicy);
            candidate[0] = accepted with { Lifecycle = accepted.Lifecycle with { CodeRefreshPolicy = PluginCodeRefreshPolicy.Allow } };
            Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, factory.Rows[1].Lifecycle.CodeRefreshPolicy);
            var unchanged = await host.ReconcileConfigurationAsync([accepted]).AsTask().WaitAsync(limit);
            Assert.Equal(changed.Revision, unchanged.Revision);
            Assert.Empty(unchanged.ChangedRowIds);
            Assert.Equal(2, factory.Rows.Count);
            Assert.All(factory.Refreshes, refresh => Assert.False(refresh));
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(limit); }
    }

    /// <summary>Binds explicit startup entry and metadata headroom and includes both values in resolved output.</summary>
    [Fact]
    public void RuntimeAdmissionReservesComposeAndAppearInDump()
    {
        Write("bundles/a.yaml", """
            - id: row
              plugin: p.a
              lifecycle: {entry_reserve: 4, service_metadata_reserve: 20000}
            """);
        WriteProfile("bundles: [a]");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(4, Assert.Single(rows).Lifecycle.EntryReserve);
        Assert.Equal(20000, rows[0].Lifecycle.ServiceMetadataReserve);
        Assert.Contains("entry_reserve: 4", ProfileComposer.Dump(rows));
        Assert.Contains("service_metadata_reserve: 20000", ProfileComposer.Dump(rows));
    }

    private sealed class PolicyCaptureFactory : IPluginExecutionFactory
    {
        internal List<PluginRow> Rows { get; } = [];
        internal List<bool> Refreshes { get; } = [];
        /// <summary>Uses no shared module key so the fixture observes configuration behavior alone.</summary>
        public string? CoordinationKey => null;
        /// <summary>Records whether the host requests a refresh while returning a fresh observer execution.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refreshes.Add(reload);
            return ValueTask.FromResult<IPluginExecution>(new PolicyCaptureExecution(Rows));
        }
    }

    private sealed class PolicyCaptureExecution(List<PluginRow> rows) : IPluginExecution
    {
        /// <summary>Matches the definition configured by the profile fixture.</summary>
        public PluginAttribute Declaration => new("p.a");
        /// <summary>Reports a fixed release because this fixture never changes executable code.</summary>
        public string Version => "1.0.0";
        /// <summary>Declares no additional shared-contract version constraints.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => new Dictionary<string, string>();
        /// <summary>Uses the type-free execution path instead of loading a native plugin class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Captures the host-admitted row to check snapshot policy preservation.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(context.Row);
            return ValueTask.CompletedTask;
        }
        /// <summary>Completes cleanup because the observer owns no resources beyond its recorded snapshot.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("{entry_reserve: -1}")]
    [InlineData("{entry_reserve: 1000001}")]
    [InlineData("{service_metadata_reserve: -1}")]
    [InlineData("{service_metadata_reserve: 10000001}")]
    [InlineData("{configuration_depth_limit: 0}")]
    [InlineData("{configuration_depth_limit: 257}")]
    [InlineData("{configuration_node_limit: 0}")]
    [InlineData("{configuration_node_limit: 1000001}")]
    [InlineData("{configuration_collection_limit: 0}")]
    [InlineData("{configuration_collection_limit: 1000001}")]
    [InlineData("{configuration_text_limit: 0}")]
    [InlineData("{configuration_text_limit: 67108865}")]
    [InlineData("{effect_capacity: 0}")]
    [InlineData("{teardown_timeout_ms: 0}")]
    [InlineData("{reconcile_attempt_limit: 0}")]
    [InlineData("{reconcile_attempt_limit: 10001}")]
    [InlineData("{effect_capacity: null}")]
    [InlineData("{effect_capacity: [2]}")]
    [InlineData("{unknown: 12}")]
    [InlineData("bad")]
    public void InvalidLifecycleConfigurationFailsLoud(string lifecycle)
    {
        Write("bundles/a.yaml", $"- id: row\n  plugin: p.a\n  lifecycle: {lifecycle}\n");
        WriteProfile("bundles: [a]\n");
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
    }

    [Fact]
    public void NullNumericConfigurationCannotSilentlyUseTheDefaultValue()
    {
        var row = new PluginRow
        {
            Id = "null-limit", Plugin = "test", Source = "test",
            Config = new Dictionary<string, object?> { ["effect_capacity"] = null },
        };
        var error = Assert.Throws<ConfigException>(() => new PluginConfig(row).Bind<PluginLifecycleOptions>());
        Assert.Contains("effect_capacity", error.Message);
        Assert.Contains("null", error.Message);
    }

    [Fact]
    public void FamilyComesFromTheNearestDeclaringProfileAndDefaultsToLegacy()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        Write("root/profile.yaml", "id: root\nclient_build: kr:812:812\nbundles: [a]\n");
        Write("head/profile.yaml", "id: head\nclient_build: kr:1256:1304\nparent: root\nfamily: modern\nbundles: [a]\n");
        Write("child/profile.yaml", "id: child\nclient_build: cn:774:774\nparent: head\nbundles: [a]\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var composer = new ProfileComposer(_root);
        Assert.Equal("legacy", composer.Compose("root", [])[0].Family);
        Assert.Equal("modern", composer.Compose("head", [])[0].Family);
        Assert.Equal("modern", composer.Compose("child", [])[0].Family);
        Assert.Null(composer.Compose("demo", [])[0].Family);
        Assert.Contains("family: modern", ProfileComposer.Dump(composer.Compose("child", [])));
        Write("bad/profile.yaml", "id: bad\nclient_build: kr:1:1\nfamily: korean\nbundles: [a]\n");
        Assert.Contains("family must be one of", Assert.Throws<ConfigException>(() => composer.Compose("bad", [])).Message);
    }

    [Fact]
    public void ParentLinksComposeTheLineageNearestFirst()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        Write("base/profile.yaml", "id: base\n");
        Write("mid/profile.yaml", "id: mid\nparent: base\n");
        WriteProfile("id: demo\nparent: mid\nbundles: [a]\n");
        var composer = new ProfileComposer(_root);
        var rows = composer.Compose("demo", []);
        Assert.Equal(["demo", "mid", "base"], rows[0].Lineage);
        Assert.Equal(["base"], composer.Lineage("base"));
        Assert.Contains("lineage: [demo, mid, base]", ProfileComposer.Dump(rows));
    }

    [Theory]
    [InlineData("id: demo\nparent: missing\n", "missing")]
    [InlineData("id: demo\nparent: loop\n", "cycle")]
    public void BrokenParentLinksFail(string profile, string expected)
    {
        Write("loop/profile.yaml", "id: loop\nparent: demo\n");
        WriteProfile(profile);
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void VariantsResolveToTheNearestAncestor()
    {
        var variants = new BuildVariants<string>("TestMessage").Add("base", "v-base").Add("mid", "v-mid");
        Assert.Equal(new VariantChoice<string>("v-mid", "mid", VariantMatch.Inherited), variants.Resolve(["demo", "mid", "base"]));
        Assert.Equal(new VariantChoice<string>("v-base", "base", VariantMatch.Exact), variants.Resolve(["base"]));
        Assert.Null(variants.Resolve(["other"]));
        var error = Assert.Throws<ConfigException>(() => variants.Require(["other"]));
        Assert.Contains("TestMessage", error.Message);
        Assert.Throws<ArgumentException>(() => variants.Add("mid", "again"));
    }

    [Fact]
    public void BundlesComposeInDeclaredOrder()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        Write("bundles/b.yaml", "- id: row-b\n  plugin: p.b\n");
        WriteProfile("id: demo\nbundles: [a, b]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(["row-a", "row-b"], rows.Select(r => r.Id).ToArray());
        Assert.Equal("bundle:a", rows[0].Source);
    }

    [Fact]
    public void ReplaceSwapsWholeConfig()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  config:\n    listen: \"a:1\"\n    other: keep\n");
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    config:\n      listen: \"b:2\"\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        var config = rows[0].Config!;
        Assert.Equal("b:2", config["listen"]);
        Assert.False(config.ContainsKey("other"));
    }

    [Fact]
    public void InsertAndRemoveApplyInOrder()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n- id: row-b\n  plugin: p.b\n");
        Write("demo/patch.yaml", "- remove: row-a\n- insert:\n    - id: row-c\n      plugin: p.c\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(["row-b", "row-c"], rows.Select(r => r.Id).ToArray());
        Assert.Equal("patch:patch.yaml", rows[1].Source);
    }

    [Fact]
    public void ReplaceUnknownRowFails()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        Write("demo/patch.yaml", "- replace:\n    id: ghost\n    config: {}\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var ex = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void EnvInterpolationUsesDefaultWhenUnset()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  config:\n    listen: \"${BFF_TEST_MISSING:-fallback:1}\"\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal("fallback:1", rows[0].Config!["listen"]);
    }

    [Fact]
    public void EnvInterpolationReadsVariable()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  config:\n    value: \"${BFF_TEST_SET}\"\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        Environment.SetEnvironmentVariable("BFF_TEST_SET", "real");
        try
        {
            var rows = new ProfileComposer(_root).Compose("demo", []);
            Assert.Equal("real", rows[0].Config!["value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BFF_TEST_SET", null);
        }
    }

    [Fact]
    public void MissingEnvVarWithoutDefaultFails()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  config:\n    value: \"${BFF_TEST_DEFINITELY_MISSING}\"\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var ex = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("BFF_TEST_DEFINITELY_MISSING", ex.Message);
        Assert.Contains("row-a", ex.Message);
    }

    [Fact]
    public void DisabledRowSkipsEnvInterpolation()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  disabled: true\n  config:\n    value: \"${BFF_TEST_DEFINITELY_MISSING}\"\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Single(rows);
        Assert.True(rows[0].Disabled);
    }

    [Theory]
    [InlineData(null, 600, false)]
    [InlineData(null, 600, true)]
    [InlineData("480", 480, false)]
    [InlineData("480", 480, true)]
    public void EnablingPreservedConfigurationResolvesDefaultsAndOverrides(string? value, int expected, bool setFirst)
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  disabled: true\n  config:\n    value: '${TEST_LIFETIME:-600}'\n");
        const string enable = "- replace:\n    id: row-a\n    disabled: false\n";
        const string set = "- set:\n    id: row-a\n    config:\n      label: '${TEST_LABEL:-catalog}'\n";
        Write("demo/patch.yaml", setFirst ? set + enable : enable + set);
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var row = Assert.Single(new ProfileComposer(_root, key => key == "TEST_LIFETIME" ? value : null).Compose("demo", []));
        Assert.False(row.Disabled);
        var options = new PluginConfig(row).Bind<DeferredOptions>();
        Assert.Equal(expected, options.Value);
        Assert.Equal("catalog", options.Label);
    }

    [Fact]
    public void EnabledDeferredConfigurationResolvesNestedMappingsAndLists()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  disabled: true\n  config:\n    nested:\n      port: '${TEST_PORT:-8090}'\n    values: ['${TEST_FIRST:-one}', '${TEST_SECOND:-two}']\n");
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    disabled: false\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var row = Assert.Single(new ProfileComposer(_root, _ => null).Compose("demo", []));
        Assert.Equal("8090", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(row.Config!["nested"])["port"]);
        Assert.Equal(new object?[] { "one", "two" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(row.Config["values"]));
    }

    [Fact]
    public void DeferredMissingVariableFailsOnlyWhenTheFinalEnabledRowStillNeedsIt()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  disabled: true\n  config:\n    value: '${TEST_REQUIRED}'\n");
        const string enable = "- replace:\n    id: row-a\n    disabled: false\n";
        Write("demo/patch.yaml", enable);
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var composer = new ProfileComposer(_root, _ => null);
        var error = Assert.Throws<ConfigException>(() => composer.Compose("demo", []));
        Assert.Contains("TEST_REQUIRED", error.Message);
        Assert.Contains("row-a", error.Message);
        Write("demo/patch.yaml", enable + "- set:\n    id: row-a\n    config:\n      value: 480\n");
        Assert.Equal(480, new PluginConfig(Assert.Single(composer.Compose("demo", []))).Bind<DeferredOptions>().Value);
        Write("demo/patch.yaml", enable + "- set:\n    id: row-a\n    config:\n      value: null\n");
        Assert.False(Assert.Single(composer.Compose("demo", [])).Config!.ContainsKey("value"));
        Write("demo/patch.yaml", enable + "- replace:\n    id: row-a\n    disabled: true\n");
        Assert.True(Assert.Single(composer.Compose("demo", [])).Disabled);
    }

    [Fact]
    public void DisableSetAndEnableDoNotExpandResolvedEnvironmentTextTwice()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  config:\n    label: '${TEST_LITERAL}'\n");
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    disabled: true\n- set:\n    id: row-a\n    config:\n      value: '${TEST_LIFETIME:-600}'\n- replace:\n    id: row-a\n    disabled: false\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var row = Assert.Single(new ProfileComposer(_root, key => key == "TEST_LITERAL" ? "${KEEP_LITERAL}" : null).Compose("demo", []));
        var options = new PluginConfig(row).Bind<DeferredOptions>();
        Assert.Equal("${KEEP_LITERAL}", options.Label);
        Assert.Equal(600, options.Value);
    }

    [Fact]
    public void ReplacingDisabledConfigurationDiscardsEarlierDeferredKeys()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  disabled: true\n  config:\n    discarded: '${TEST_MISSING}'\n");
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    config:\n      value: '${TEST_LIFETIME:-600}'\n- replace:\n    id: row-a\n    disabled: false\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [patch.yaml]\n");
        var row = Assert.Single(new ProfileComposer(_root, _ => null).Compose("demo", []));
        Assert.False(row.Config!.ContainsKey("discarded"));
        Assert.Equal(600, new PluginConfig(row).Bind<DeferredOptions>().Value);
    }

    private sealed class DeferredOptions
    {
        /// <summary>Gets or sets the integer used to detect unresolved placeholder text at binding.</summary>
        public int Value { get; set; }
        /// <summary>Gets or sets the text used to check preservation of resolved literal placeholders.</summary>
        public string Label { get; set; } = "";
    }

    [Fact]
    public void OptionalLocalPatchIsSkippedWhenAbsent()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [local.patch.yaml]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Single(rows);
    }

    [Fact]
    public void RequiredPatchMissingFails()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        WriteProfile("id: demo\nbundles: [a]\npatches: [nope.patch.yaml]\n");
        Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
    }

    [Fact]
    public void CliPatchAppliesLast()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n");
        var cli = Write("cli.patch.yaml", "- remove: row-a\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var rows = new ProfileComposer(_root).Compose("demo", [cli]);
        Assert.Empty(rows);
    }

    [Fact]
    public void DuplicateRowIdFails()
    {
        Write("bundles/a.yaml", "- id: same\n  plugin: p.a\n- id: same\n  plugin: p.b\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var ex = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("same", ex.Message);
    }

    [Fact]
    public void BuildIdentityIsInheritedAndReplacePreservesOrChangesVersionConstraint()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  version: '>=1.2 <2'\n");
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    config: {}\n");
        WriteProfile("id: demo\nclient_build: kr:1256:1304\nbundles: [a]\npatches: [patch.yaml]\n");
        var rows = new ProfileComposer(_root).Compose("demo", []);
        Assert.Equal(">=1.2 <2", rows[0].Version);
        Assert.Equal(new Abstractions.Protocol.ClientBuild("kr", 1256, 1304), rows[0].ClientBuild);
        Assert.Contains("version: ", ProfileComposer.Dump(rows));
        Assert.Contains("client_build: kr:1256:1304", ProfileComposer.Dump(rows));
        Write("demo/patch.yaml", "- replace:\n    id: row-a\n    version: '>=2 <3'\n");
        Assert.Equal(">=2 <3", new ProfileComposer(_root).Compose("demo", [])[0].Version);
    }

    [Theory]
    [InlineData("kr:0:1304")]
    [InlineData("kr:1256")]
    public void InvalidProfileBuildFailsWithProfileContext(string build)
    {
        WriteProfile($"id: demo\nclient_build: {build}\nbundles: []\n");
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("demo", error.Message);
        Assert.Contains("client_build", error.Message);
    }

    [Fact]
    public void InvalidVersionConstraintFailsDuringComposition()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  version: '^1.2'\n");
        WriteProfile("id: demo\nbundles: [a]\n");
        var error = Assert.Throws<ConfigException>(() => new ProfileComposer(_root).Compose("demo", []));
        Assert.Contains("row-a", error.Message);
        Assert.Contains("a.yaml", error.Message);
    }

    [Fact]
    public void BuildAndVersionSupportEnvironmentDefaults()
    {
        Write("bundles/a.yaml", "- id: row-a\n  plugin: p.a\n  version: '${BFF_TEST_RELEASE_BOUND:-<2}'\n");
        WriteProfile("id: demo\nclient_build: '${BFF_TEST_BUILD_ID:-kr:1256:1304}'\nbundles: [a]\n");
        var row = Assert.Single(new ProfileComposer(_root).Compose("demo", []));
        Assert.Equal("<2", row.Version);
        Assert.Equal(new Abstractions.Protocol.ClientBuild("kr", 1256, 1304), row.ClientBuild);
    }
}
