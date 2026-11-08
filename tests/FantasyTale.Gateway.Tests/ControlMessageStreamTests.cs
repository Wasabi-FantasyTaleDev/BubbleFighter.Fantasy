using System.Text;
using FantasyTale.Gateway;

namespace FantasyTale.Gateway.Tests;

/// <summary>Pins the parent pipe framing and its clean-close, truncation and size-limit behavior.</summary>
public sealed class ControlMessageStreamTests
{
    /// <summary>A launch-sized byte budget carries larger catalogs without retaining the former control-buffer ceiling.</summary>
    [Fact]
    public async Task Explicit_control_budget_carries_a_body_larger_than_the_compatibility_default()
    {
        var body = new byte[GatewayConstants.MaxControlMessageBytes + 1];
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(body.Length));
        await stream.WriteAsync(body);
        stream.Position = 0;
        Assert.Equal(body, await ControlMessageStream.ReadAsync(stream, CancellationToken.None, maxMessageBytes: body.Length));
        Assert.Equal(body.Length, Program.ParseControlLimit(["--pipe", "fixture", "--control-max-bytes", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        Assert.Throws<GatewayConfigurationException>(() => Program.ParseControlLimit(["--pipe", "fixture", "--control-max-bytes", "0"]));
    }

    /// <summary>A READY body survives the four-byte length-prefixed control exchange unchanged.</summary>
    [Fact]
    public async Task Control_messages_use_a_little_endian_length_prefix()
    {
        await using var stream = new MemoryStream();
        await ControlMessageStream.WriteJsonAsync(stream, new GatewayReady { Rows = [new("selector", "bootstrap", "127.0.0.1", 1234, "kr-812")] }, GatewayJsonContext.Strict.GatewayReady, CancellationToken.None);
        Assert.Equal(new byte[] { 129, 0, 0, 0 }, stream.ToArray()[..4]);
        stream.Position = 0;

        var body = await ControlMessageStream.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(body);
        Assert.Equal("{\"schema_version\":1,\"type\":\"ready\",\"rows\":[{\"id\":\"selector\",\"mode\":\"bootstrap\",\"host\":\"127.0.0.1\",\"port\":1234,\"build\":\"kr-812\"}]}", Encoding.UTF8.GetString(body!));
    }

    /// <summary>An excessive declared length fails before a message body is allocated.</summary>
    [Fact]
    public async Task Control_messages_reject_oversized_length_before_allocating_body()
    {
        await using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(GatewayConstants.MaxControlMessageBytes + 1));
        stream.Position = 0;

        var error = await Assert.ThrowsAsync<GatewayConfigurationException>(() => ControlMessageStream.ReadAsync(stream, CancellationToken.None).AsTask());
        Assert.Contains("outside", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Closing the pipe between messages is a normal parent stop signal.</summary>
    [Fact]
    public async Task Clean_parent_eof_returns_null()
    {
        await using var stream = new MemoryStream();
        Assert.Null(await ControlMessageStream.ReadAsync(stream, CancellationToken.None));
    }

    /// <summary>Closing the pipe inside either a header or body reports a truncated message.</summary>
    [Theory]
    [InlineData("01")]
    [InlineData("020000007B")]
    public async Task Partial_control_messages_are_errors(string hex)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        await Assert.ThrowsAsync<EndOfStreamException>(() => ControlMessageStream.ReadAsync(stream, CancellationToken.None).AsTask());
    }
}
