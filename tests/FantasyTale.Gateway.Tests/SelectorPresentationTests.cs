using System.Text.Json;
using System.Text.Json.Nodes;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks that disabled chooser rows cannot create native network connections or hide active choices.</summary>
public sealed class SelectorPresentationTests
{
    /// <summary>Presentation order includes unavailable choices while wire bindings contain real destinations only.</summary>
    [Fact]
    public void Six_visible_choices_with_one_backend_validate_and_round_trip_without_fake_endpoints()
    {
        var options = Options();
        var profile = options.Validate();
        Assert.Equal(22, BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers).Length);
        var json = JsonSerializer.Serialize(options, GatewayJsonContext.Strict.CenterSelectorOptions);
        var restored = JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.CenterSelectorOptions)!;
        restored.Validate();
        var presentation = restored.SelectorIdentity!.Presentation!;
        Assert.Equal(6, presentation.Length);
        Assert.Equal("choice-0", presentation[0].BindingId);
        foreach (var choice in presentation.Skip(1))
        {
            Assert.Null(choice.BindingId);
            Assert.Equal("offline", choice.UnavailableReason);
        }
        var rows = JsonNode.Parse(json)!["selector_identity"]!["presentation"]!.AsArray();
        foreach (var row in rows)
        {
            Assert.Null(row!["host"]);
            Assert.Null(row["port"]);
            Assert.Null(row["group"]);
            Assert.Null(row["sub_key"]);
        }
    }

    /// <summary>A missing, ambiguous or stale row cannot reach native presentation.</summary>
    [Theory]
    [InlineData("missing-active")]
    [InlineData("unknown-binding")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-native-id")]
    [InlineData("zero-native-id")]
    [InlineData("active-reason")]
    [InlineData("unavailable-no-reason")]
    [InlineData("invalid-reason")]
    [InlineData("stale-name")]
    [InlineData("stale-native-id")]
    [InlineData("stale-id")]
    [InlineData("null-row")]
    [InlineData("one-row")]
    [InlineData("too-many-rows")]
    public void Invalid_presentation_fails_before_ready(string mutation)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Options(), GatewayJsonContext.Strict.CenterSelectorOptions))!;
        var rows = json["selector_identity"]!["presentation"]!.AsArray();
        switch (mutation)
        {
            case "missing-active": rows.RemoveAt(0); break;
            case "unknown-binding": rows[0]!["binding_id"] = "missing"; break;
            case "duplicate-id": rows[1]!["id"] = "choice-0"; break;
            case "duplicate-name": rows[1]!["name"] = "Choice 0"; break;
            case "duplicate-native-id": rows[1]!["native_center_id"] = 1; break;
            case "zero-native-id": rows[1]!["native_center_id"] = 0; break;
            case "active-reason": rows[0]!["unavailable_reason"] = "offline"; break;
            case "unavailable-no-reason": rows[1]!.AsObject().Remove("unavailable_reason"); break;
            case "invalid-reason": rows[1]!["unavailable_reason"] = "bad reason"; break;
            case "stale-name": rows[0]!["name"] = "Stale"; break;
            case "stale-native-id": rows[0]!["native_center_id"] = 99; break;
            case "stale-id": rows[0]!["id"] = "stale"; break;
            case "null-row": rows[1] = null; break;
            case "one-row": while (rows.Count > 1) rows.RemoveAt(1); break;
            case "too-many-rows": while (rows.Count < 33) rows.Add(rows[1]!.DeepClone()); break;
        }
        var options = JsonSerializer.Deserialize(json.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Only an explicitly capable parent may request presentation rows.</summary>
    [Theory]
    [InlineData("bf-native-selector-identity-v1", true)]
    [InlineData("bf-native-selector-presentation-v2", false)]
    public void Presentation_requires_exact_capability_and_explicit_rows(string capability, bool includeRows)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Options(), GatewayJsonContext.Strict.CenterSelectorOptions))!;
        json["selector_identity"]!["capability"] = capability;
        if (!includeRows) json["selector_identity"]!.AsObject().Remove("presentation");
        var options = JsonSerializer.Deserialize(json.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Unavailable choices cannot smuggle an endpoint into the presentation contract.</summary>
    [Fact]
    public void Unavailable_row_rejects_network_fields_during_strict_deserialization()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Options(), GatewayJsonContext.Strict.CenterSelectorOptions))!;
        json["selector_identity"]!["presentation"]![1]!["host"] = "127.0.0.1";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions));
    }

    /// <summary>An entirely unavailable catalog fails before starting a game against an empty native directory.</summary>
    [Fact]
    public void No_active_destination_is_a_launch_error()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Options(), GatewayJsonContext.Strict.CenterSelectorOptions))!;
        json["centers"]!.AsArray().Clear();
        var options = JsonSerializer.Deserialize(json.ToJsonString(), GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    internal static CenterSelectorOptions Options(string build = "cn-324") => new()
    {
        Build = build,
        Centers = [new CenterEntry
        {
            Id = "choice-0", Name = "Choice 0", NativeCenterId = 1,
            BackendId = "backend-a", Host = "192.0.2.1", Port = 7140,
        }],
        SelectorIdentity = new SelectorIdentityOptions
        {
            Capability = SelectorIdentityOptions.PresentationCapability,
            LaunchId = "0123456789abcdef0123456789abcdef",
            Presentation = Enumerable.Range(0, 6).Select(i => new SelectorPresentationEntry
            {
                Id = "choice-" + i, Name = "Choice " + i, NativeCenterId = i + 1,
                BindingId = i == 0 ? "choice-0" : null, UnavailableReason = i == 0 ? null : "offline",
            }).ToArray(),
        },
    };
}
