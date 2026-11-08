using System.Text.Json;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks strict parent configuration, native endpoint constraints and bounded diagnostics.</summary>
public sealed class GatewayConfigurationTests
{
    /// <summary>Omitted optional JSON fields retain the resource limits used by native AOT launches.</summary>
    [Fact]
    public void Minimal_parent_json_preserves_validated_resource_defaults()
    {
        const string json = """
            {"schema_version":1,"rows":[{"id":"selector","plugin":"bf-center-selector","config":{
                "build":"kr-812","centers":[{"id":"one","name":"One","host":"192.0.2.1","port":7110}]}}]}
            """;
        var config = JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.GatewayConfiguration)!;
        config.Validate();
        var options = config.Rows[0].Config.Deserialize(GatewayJsonContext.Strict.CenterSelectorOptions)!;
        options.Validate();
        Assert.Equal(5000, config.ControlFrameTimeoutMs);
        Assert.Equal(0, options.SelectionTimeoutMs);
    }

    /// <summary>Invalid property shapes are rejected during deserialization rather than activation.</summary>
    [Theory]
    [InlineData("{\"schema_version\":1,\"rows\":[],\"typo\":1}")]
    [InlineData("{\"schema_version\":1,\"schema_version\":1,\"rows\":[]}")]
    [InlineData("{\"schema_version\":1}")]
    [InlineData("{\"schema_version\":1,\"rows\":null}")]
    public void Control_schema_rejects_unknown_duplicate_missing_and_null_properties(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.GatewayConfiguration));

    /// <summary>A null row cannot reach plugin composition.</summary>
    [Fact]
    public void Null_array_elements_are_rejected_before_activation()
    {
        var config = JsonSerializer.Deserialize("{\"schema_version\":1,\"rows\":[null]}", GatewayJsonContext.Strict.GatewayConfiguration)!;
        Assert.Throws<GatewayConfigurationException>(config.Validate);
    }

    /// <summary>The native directory accepts only resolved, canonical unicast IPv4 literals.</summary>
    [Theory]
    [InlineData("center.example")]
    [InlineData("::1")]
    [InlineData("127.1")]
    [InlineData("127.000.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("239.1.1.1")]
    public void Directory_requires_resolved_canonical_unicast_ipv4(string host) =>
        Assert.Throws<GatewayConfigurationException>(() => new CenterEntry { Id = "one", Name = "One", Host = host, Port = 7110 }.Validate());

    /// <summary>Different catalog identities cannot publish the same native destination twice.</summary>
    [Fact]
    public void Duplicate_destinations_are_rejected()
    {
        var options = Options();
        options.Centers[1] = new CenterEntry { Id = "another", Name = "Another", Host = options.Centers[0].Host, Port = options.Centers[0].Port };
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Short names are optional metadata and become the automatic label anchor when supplied.</summary>
    [Fact]
    public void Optional_short_name_round_trips_and_labels_automatic_selection()
    {
        var options = AutomaticCenterTests.Options("cn-324");
        Assert.Equal("自动选择（First）", options.ResolveAutomaticName(options.AutomaticEntry!));
        options.Centers[0] = new CenterEntry
        {
            Id = "first", Name = "Beijing production cluster", ShortName = "Beijing",
            BackendId = "physical-first", NativeCenterId = 2, Host = "192.0.2.10", Port = 7110,
        };

        options.Validate();
        Assert.Equal("自动选择（Beijing）", options.ResolveAutomaticName(options.AutomaticEntry!));
        var json = JsonSerializer.Serialize(options, GatewayJsonContext.Strict.CenterSelectorOptions);
        var parsed = JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Equal("Beijing", parsed.Centers[0].ShortName);
        Assert.Equal("自动选择（Beijing）", parsed.ResolveAutomaticName(parsed.AutomaticEntry!));
    }

    /// <summary>The automatic entry accepts the parent's optional short name through strict JSON.</summary>
    [Theory]
    [InlineData("Central", "自动选择（Central）")]
    [InlineData(null, "自动选择（First）")]
    public void Automatic_entry_short_name_overrides_anchor_and_falls_back_to_full_name(string? shortName, string expected)
    {
        var options = AutomaticCenterTests.Options("cn-406");
        var json = JsonSerializer.Serialize(options, GatewayJsonContext.Strict.CenterSelectorOptions);
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        document["automatic_entry"]!["short_name"] = shortName;
        var parsed = JsonSerializer.Deserialize(document.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions)!;
        parsed.Validate();
        Assert.Equal(shortName, parsed.AutomaticEntry!.ShortName);
        Assert.Equal(expected, parsed.ResolveAutomaticName(parsed.AutomaticEntry));
    }

    /// <summary>Malformed short names fail at the same configuration boundary as full labels.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bad\nName")]
    public void Invalid_short_name_is_rejected(string shortName)
    {
        var options = Options();
        options.Centers[0] = new CenterEntry
        {
            Id = "one", Name = "Catalog one", ShortName = shortName, Host = "192.0.2.10", Port = 7110,
        };
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Unsupported builds and future capabilities fail without selecting an unrelated adapter.</summary>
    [Fact]
    public void Unknown_build_and_unimplemented_messenger_do_not_fall_back()
    {
        Assert.Throws<GatewayConfigurationException>(() => CompiledBuildCatalog.Resolve("unknown-1"));
        Assert.Throws<GatewayConfigurationException>(() => GatewayPluginCatalog.Create("nmco"));
    }

    /// <summary>Repeated diagnostics cannot exceed the process-wide stderr budget.</summary>
    [Fact]
    public void Diagnostics_are_bounded_and_single_line()
    {
        var output = new StringWriter();
        var log = new GatewayLog(output);
        for (var i = 0; i < 1000; i++) log.Write(new string('x', 1000) + "\n");
        Assert.InRange(output.ToString().Length, 1, 2048);
        Assert.Contains("Fantasy Tale Gateway log budget exhausted.", output.ToString());
    }

    /// <summary>Diagnostic row scopes retain late metadata and cannot consume the final process-error budget.</summary>
    [Fact]
    public void Diagnostic_row_budget_preserves_process_errors_after_trace_exhaustion()
    {
        var output = new StringWriter();
        var log = new GatewayLog(output);
        var trace = log.WithBudget(8192);
        for (var i = 0; i < 200; i++) trace.Write("bf-center-selector: connection role=unavailable port=65535 event=status-sent.");
        var traceLength = output.ToString().Length;
        Assert.InRange(traceLength, 2049, 8192);
        log.Write("Fantasy Tale Gateway failed: TimeoutException: Unavailable Center retirement deadline expired.");
        Assert.Contains("Unavailable Center retirement deadline expired.", output.ToString()[traceLength..]);
        Assert.InRange(output.ToString().Length, traceLength, traceLength + 2048);
    }

    /// <summary>A row cannot request an unbounded or unusably small connection trace.</summary>
    [Theory]
    [InlineData(2047)]
    [InlineData(1048577)]
    public void Connection_trace_budget_is_validated_before_activation(int maximum)
    {
        var options = Options();
        options.ConnectionTrace = true;
        options.ConnectionTraceMaxBytes = maximum;
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    internal static CenterSelectorOptions Options(string build = "kr-812") => new()
    {
        Build = build,
        Centers = [
            new CenterEntry { Id = "one", Name = "Catalog one", Host = "192.0.2.10", Port = 7110 },
            new CenterEntry { Id = "two", Name = "Catalog two", Host = "198.51.100.20", Port = 7210 },
        ],
    };
}
