using System.Buffers.Binary;
using System.Text.Json;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Checks explicit native alias negotiation, backend ownership and exact directory bindings.</summary>
public sealed class SelectorIdentityTests
{
    /// <summary>Automatic and manual choices keep distinct native keys while sharing a physical destination.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-735")]
    public void Aliases_preserve_distinct_keys_and_pinned_physical_endpoint_bytes(string build)
    {
        var options = Options(build);
        var profile = options.Validate();
        var entries = BfCenterSelectorPlugin.CreateBindings(profile, options.Centers);
        var wire = BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers);
        Assert.Equal(40, wire.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(wire));
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var start = 4 + i * 18;
            Assert.Equal(entry.Group, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(start)));
            Assert.Equal(entry.SubKey, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(start + 4)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(start + 8)));
            Assert.Equal("C000020A", Convert.ToHexString(wire.AsSpan(start + 12, 4)));
            Assert.Equal(entry.Port, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(start + 16)));
            Assert.Equal(options.Centers[i].Id, entry.Id);
            Assert.Equal(options.Centers[i].Name, entry.Name);
            Assert.Equal(options.Centers[i].NativeCenterId, entry.NativeCenterId);
        }
        Assert.NotEqual(entries[0].SubKey, entries[1].SubKey);
        Assert.NotEqual(entries[0].NativeCenterId, entries[1].NativeCenterId);
        Assert.Equal(entries[0].BackendId, entries[1].BackendId);
    }

    /// <summary>A capability assertion is bound to the supported adapter and owning launch identifier.</summary>
    [Theory]
    [InlineData("unknown-1", "bf-native-selector-identity-v1", "0123456789abcdef0123456789abcdef")]
    [InlineData("cn-738", "bf-native-selector-identity-v1", "0123456789abcdef0123456789abcdef")]
    [InlineData("cn-324", "future", "0123456789abcdef0123456789abcdef")]
    [InlineData("cn-324", "bf-native-selector-identity-v1", "")]
    [InlineData("cn-324", "bf-native-selector-identity-v1", "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("cn-324", "bf-native-selector-identity-v1", "0123456789abcdef0123456789abcdeg")]
    public void Invalid_or_unsupported_bridge_contract_fails(string build, string capability, string launchId)
    {
        var options = new CenterSelectorOptions
        {
            Build = build, Centers = Options().Centers,
            SelectorIdentity = new SelectorIdentityOptions { Capability = capability, LaunchId = launchId },
        };
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Native presentation identities occupy the positive signed 32-bit range accepted by the bridge.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void Missing_invalid_or_duplicate_native_identity_fails(int? id)
    {
        var options = Options();
        options.Centers[1] = Entry("manual", "Manual", "cloud-a", id);
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Two selector aliases cannot claim one endpoint through conflicting backend identities.</summary>
    [Fact]
    public void Endpoint_cannot_be_owned_by_two_backend_identities()
    {
        var options = Options();
        options.Centers[1] = Entry("manual", "Manual", "cloud-b", 2);
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>One backend identity cannot silently resolve to different endpoints inside a launch.</summary>
    [Fact]
    public void Backend_cannot_have_two_physical_endpoints()
    {
        var options = Options();
        options.Centers[1] = Entry("manual", "Manual", "cloud-a", 2, "192.0.2.11");
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Selector aliases require distinct identities and labels even when native ids differ.</summary>
    [Theory]
    [InlineData("automatic", "Manual")]
    [InlineData("manual", "Automatic")]
    public void Duplicate_selector_identity_or_label_fails(string id, string name)
    {
        var options = Options();
        options.Centers[1] = Entry(id, name, "cloud-a", 2);
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>Bridge metadata cannot accidentally activate through a legacy configuration.</summary>
    [Fact]
    public void Legacy_mode_rejects_bridge_metadata_even_on_distinct_endpoints()
    {
        var options = GatewayConfigurationTests.Options("cn-324");
        options.Centers[0] = Entry("automatic", "Automatic", "cloud-a", 1);
        Assert.Throws<GatewayConfigurationException>(() => options.Validate());
    }

    /// <summary>UTF-16 names round-trip through the control contract without replacing invalid surrogate halves.</summary>
    [Fact]
    public void Labels_require_well_formed_bounded_utf16_and_accept_positive_native_id_ceiling()
    {
        var options = Options();
        foreach (var name in new[] { new string('x', 129), "Bad\uD800", "Bad\uDC00", "Bad\nName" })
        {
            options.Centers[1] = Entry("manual", name, "cloud-a", 2);
            Assert.Throws<GatewayConfigurationException>(() => options.Validate());
        }
        options.Centers[1] = Entry("manual", new string('x', 126) + "\U00020000", "cloud-a", int.MaxValue);
        options.Validate();
        var json = JsonSerializer.Serialize(options, GatewayJsonContext.Strict.CenterSelectorOptions);
        var parsed = JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.CenterSelectorOptions)!;
        Assert.Equal(options.Centers[1].Name, parsed.Centers[1].Name);
        Assert.Equal(int.MaxValue, parsed.Centers[1].NativeCenterId);
    }

    /// <summary>A single entry cannot bypass the required native chooser through direct mode.</summary>
    [Fact]
    public void Identity_mode_requires_multiple_choices()
    {
        var options = Options();
        var single = new CenterSelectorOptions
        {
            Build = options.Build, Centers = [options.Centers[0]], SelectorIdentity = options.SelectorIdentity,
        };
        Assert.Throws<GatewayConfigurationException>(() => single.Validate());
    }

    /// <summary>READY binds the exact ordered alias directory to its owning launch without changing legacy JSON.</summary>
    [Fact]
    public async Task Ready_mapping_echoes_launch_and_round_trips_with_native_alias_keys()
    {
        var options = Options();
        using var context = new GatewayContext(new GatewayLog(new StringWriter()), CancellationToken.None);
        var row = new GatewayRow
        {
            Id = "selector", Plugin = "bf-center-selector",
            Config = JsonSerializer.SerializeToElement(options, GatewayJsonContext.Strict.CenterSelectorOptions),
        };
        var activation = await new BfCenterSelectorPlugin().ActivateAsync(row, context);
        try
        {
            Assert.Equal("bootstrap", activation.Endpoint.Mode);
            var ready = new GatewayReady { Rows = [activation.Endpoint] };
            var json = JsonSerializer.Serialize(ready, GatewayJsonContext.Strict.GatewayReady);
            var parsed = JsonSerializer.Deserialize(json, GatewayJsonContext.Strict.GatewayReady)!;
            var mapping = Assert.IsType<SelectorIdentityMapping>(parsed.Rows[0].SelectorIdentity);
            Assert.Equal(options.SelectorIdentity!.Capability, mapping.Capability);
            Assert.Equal(options.SelectorIdentity.LaunchId, mapping.LaunchId);
            Assert.Equal(options.Build, mapping.Build);
            Assert.Equal(BfCenterSelectorPlugin.CreateBindings(options.Validate(), options.Centers), mapping.Entries);
            Assert.Contains("native_center_id", json);
            Assert.Contains("sub_key", json);
            var legacy = JsonSerializer.Serialize(new GatewayReady
            {
                Rows = [new GatewayEndpoint("selector", "direct", "192.0.2.10", 7110, "cn-324")],
            }, GatewayJsonContext.Strict.GatewayReady);
            Assert.DoesNotContain("selector_identity", legacy);
        }
        finally
        {
            context.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.Completion);
        }
    }

    internal static CenterSelectorOptions Options(string build = "cn-324") => new()
    {
        Build = build,
        SelectorIdentity = new SelectorIdentityOptions
        {
            Capability = SelectorIdentityOptions.SupportedCapability, LaunchId = "0123456789abcdef0123456789abcdef",
        },
        Centers = [Entry("automatic", "Automatic", "cloud-a", 1), Entry("manual", "Manual", "cloud-a", 2)],
    };

    private static CenterEntry Entry(string id, string name, string backend, int? nativeId, string host = "192.0.2.10") => new()
    {
        Id = id, Name = name, Host = host, Port = 7110, BackendId = backend, NativeCenterId = nativeId,
    };
}
