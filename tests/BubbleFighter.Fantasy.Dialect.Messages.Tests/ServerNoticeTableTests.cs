using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the notice body and capability across every audited legacy build.</summary>
public sealed class ServerNoticeTableTests
{
    [Theory]
    [InlineData("kr-812", 14)]
    [InlineData("cn-717", 14)]
    [InlineData("cn-324", 14)]
    [InlineData("cn-279", 13)]
    [InlineData("id-400", 14)]
    [InlineData("id-1809", 14)]
    [InlineData("cn-706", 14)]
    public void AnAuditedBuildCarriesTextAfterItsNativeStyleFields(string build, int length)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "server_notice"));
        var bytes = PacketCodec.Write(TestBuilds.Packets, build, "ServerNotice",
            new Dictionary<string, object?> { ["text"] = "ab" });
        Assert.Equal(length, bytes.Length);
        var textOffset = length - 8;
        Assert.All(bytes[..textOffset], value => Assert.Equal((byte)0, value));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(textOffset)));
        Assert.Equal(new byte[] { 97, 0, 98, 0 }, bytes[^4..]);
    }

    [Fact]
    public void Kr1256CarriesStyleTextAndTheChatAndPopupFlags()
    {
        Assert.True(TestBuilds.Packets.CapabilityOf("kr-1256", "server_notice"));
        var bytes = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "ServerNotice",
            new Dictionary<string, object?> { ["text"] = "ab" });
        // u8 style, wstr, bool32 shown in every stage (kr-1256 sub_99C8A0 +28), bool message box (+29).
        Assert.Equal("00" + "02000000" + "61006200" + "01000000" + "00000000", Convert.ToHexString(bytes));
    }

    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TheModernBuildsWhoseNoticeHandlerIsEmptyHaveNoNotices(string build)
    {
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "server_notice"));
    }

    [Fact]
    public void AnUnauditedFamilyDoesNotInheritTheEnabledCapability()
    {
        Assert.False(TestBuilds.Packets.CapabilityOf("cn-735", "server_notice"));
    }

    /// <summary>A table that retypes the notice text is refused when the sender is created, before it can register.</summary>
    [Fact]
    public void AFieldWithTheWrongTypeIsRefusedWhenTheBindingIsCreated()
    {
        var directory = PacketTableEdits.WithFieldType("ServerNotice", "text", "wstr", "u32");
        var error = Assert.Throws<ConfigException>(() => new ServerNoticeBinding("kr-812/game",
            TestBuilds.Opcodes("kr-812"), PacketTables.Load(directory.FullName), "kr-812"));
        Assert.Contains("text", error.Message, StringComparison.Ordinal);
        Assert.Contains("Server notice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCapabilityFailsInsteadOfSilentlyDisablingPackets()
    {
        Assert.Throws<ConfigException>(() => TestBuilds.Packets.CapabilityOf("kr-812", "missing"));
    }

    [Theory]
    [InlineData("server_notice: yes")]
    [InlineData("server_notice: []")]
    public void InvalidCapabilityValuesFailWhileLoadingTables(string definition)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "builds.yaml");
        try
        {
            File.WriteAllText(file, "version: 1\nbuilds:\n  kr-812:\n    capabilities:\n      " + definition + "\n");
            Assert.Throws<ConfigException>(() => PacketTables.Load(directory));
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
