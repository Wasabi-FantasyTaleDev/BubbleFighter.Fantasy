using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Pins the compiled canonical layouts and validates first-connect payload boundaries.</summary>
public sealed class CompiledPacketTests
{
    /// <summary>Only an audited adapter accepts the empty native game-connection keep-alive frame.</summary>
    [Theory]
    [InlineData("cn-324", 117)]
    [InlineData("cn-279", null)]
    [InlineData("kr-812", null)]
    public void Keep_alive_mapping_pins_the_empty_native_frame(string build, int? expectedOpcode)
    {
        var profile = CompiledBuildCatalog.Resolve(build);
        Assert.Equal(expectedOpcode, (int?)profile.KeepAlivePing);
        if (profile.KeepAlivePing is { } opcode)
        {
            var frame = profile.Dialect.EncodeFrame(opcode, []);
            Assert.Equal(5, frame.Length);
            Assert.Equal("0000750000", Convert.ToHexString(frame));
        }
    }

    /// <summary>Each audited adapter writes actual target addresses under fresh native selection keys.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-735")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void Directory_pins_real_endpoints_and_excludes_reserved_bootstrap_group(string build)
    {
        var options = GatewayConfigurationTests.Options(build);
        var profile = options.Validate();
        var body = BfCenterSelectorPlugin.EncodeDirectory(profile, options.Centers);
        Assert.Equal(40, body.Length);
        Assert.Equal("02000000000000000100000000000000C000020AC61B000000000200000000000000C63364142A1C", Convert.ToHexString(body));
        Assert.Equal("0000000000000000", Convert.ToHexString(profile.PatchResponse.Write(new Dictionary<string, object?>())));
    }

    /// <summary>First-connect parsing accepts bounded native strings and rejects version or length mismatches.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-735")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void First_connect_accepts_variable_strings_and_pins_empty_length(string build)
    {
        var profile = CompiledBuildCatalog.Resolve(build);
        var body = profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major });
        Assert.Equal(96, body.Length);
        profile.FirstConnect.ValidateFirstConnect(body, profile.Major, 1024);
        var longer = profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major, ["unknown_8"] = "Unicode \u4e00" });
        Assert.Equal(114, longer.Length);
        profile.FirstConnect.ValidateFirstConnect(longer, profile.Major, 1024);
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(longer, profile.Major, 2));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body, profile.Major + 1, 1024));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body[..^1], profile.Major, 1024));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect([..body, 0], profile.Major, 1024));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body, profile.Major, 1024));
    }

    /// <summary>Modern handshakes retain both scalar words around their counted strings and reject invalid boundaries.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void Modern_first_connect_pins_words_and_variable_string_boundaries(string build)
    {
        var profile = CompiledBuildCatalog.Resolve(build);
        var empty = profile.FirstConnect.Write(new Dictionary<string, object?> { ["client_version"] = profile.Major });
        Assert.Equal(96, empty.Length);
        profile.FirstConnect.ValidateFirstConnect(empty, profile.Major, 1024);
        var body = profile.FirstConnect.Write(new Dictionary<string, object?>
        {
            ["client_version"] = profile.Major, ["unknown_4"] = "Unicode \u4e00",
            ["unknown_172"] = uint.MaxValue, ["unknown_512"] = "tail",
        });
        Assert.Equal(122, body.Length);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(50, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(110, 4)));
        profile.FirstConnect.ValidateFirstConnect(body, profile.Major, 1024);
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body, profile.Major, 2));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body, profile.Major + 1, 1024));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body[..^1], profile.Major, 1024));
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect([..body, 0], profile.Major, 1024));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), uint.MaxValue);
        Assert.Throws<PacketFormatException>(() => profile.FirstConnect.ValidateFirstConnect(body, profile.Major, 1024));
    }
}
