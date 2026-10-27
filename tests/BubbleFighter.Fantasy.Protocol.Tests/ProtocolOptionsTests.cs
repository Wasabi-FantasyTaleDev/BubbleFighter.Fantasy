using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Pins <see cref="ProtocolOptions"/> command-line and default handshake bounds.</summary>
public sealed class ProtocolOptionsTests
{
    [Fact]
    public void DefaultsMatchDocumentedHandshakeTunables()
    {
        var options = new ProtocolOptions();
        Assert.Equal(4, options.BootstrapStatus);
        Assert.Equal("", options.BootstrapCommandLine);
        Assert.True(options.EnforceClientVersion);
        Assert.Equal(4 * 1024 * 1024, options.MaxPayloadBytes);
        Assert.Equal(1024 * 1024, options.InboundBufferBytes);
        Assert.Equal(1024, options.MaxTrackedConnections);
        Assert.Equal("gcpReqGamePatch", options.PatchRequestName);
        Assert.Equal(WirePins.GreetingSize, TestWireDialects.Get("kr-1256").BuildServerPreamble((ushort)options.BootstrapStatus, options.BootstrapCommandLine).Length);
    }

    [Fact]
    public void SelectedDialectRejectsCommandLineLongerThan127Chars()
    {
        var options = new ProtocolOptions { BootstrapCommandLine = new string('x', 128) };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => TestWireDialects.Get("kr-1256").BuildServerPreamble(
            (ushort)options.BootstrapStatus, options.BootstrapCommandLine));
        Assert.Contains("127", error.Message);
    }

    [Fact]
    public void SelectedDialectAccepts127CharacterCommandLine()
    {
        var options = new ProtocolOptions { BootstrapCommandLine = new string('x', 127) };
        Assert.Equal(WirePins.GreetingSize, TestWireDialects.Get("kr-1256").BuildServerPreamble((ushort)options.BootstrapStatus, options.BootstrapCommandLine).Length);
    }
}
