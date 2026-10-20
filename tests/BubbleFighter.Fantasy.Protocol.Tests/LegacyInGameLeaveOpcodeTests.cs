using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Pins the native in-game ESC request used to leave a running legacy room.</summary>
public sealed class LegacyInGameLeaveOpcodeTests
{
    [Theory]
    [InlineData("kr-812", 506, "sub_6711B0", "sub_B25EA0")]
    [InlineData("cn-717", 506, "sub_6CACE0", "sub_BF3600")]
    [InlineData("cn-406", 506, "sub_4483E0", "sub_A04C10")]
    [InlineData("id-400", 506, "sub_4AC990", "sub_A9DDB0")]
    [InlineData("id-1809", 506, "sub_6228B0", "sub_ABB1D0")]
    public void NativeEscRequestIsAnEmptyC2S506(string build, int value, string sender, string constructor)
    {
        var table = LoadTable(build);
        Assert.Equal((ushort)value, table.C2S("gcpRoomReqLeaveGame"));
        Assert.True(table.TryGet(PacketDirection.ClientToServer, (ushort)value, out var entry));
        Assert.Equal(Evidence.Verified, entry!.Evidence);
        Assert.Contains(sender, entry.Source, StringComparison.Ordinal);
        Assert.Contains(constructor, entry.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kr-812", "sub_897850", "sub_5C8FC0")]
    [InlineData("cn-717", "sub_926590", "sub_BD9960")]
    [InlineData("id-1809", "sub_83C130", "sub_8BA8E0")]
    public void NativeRelayLeaveCannotResolveInTheGameConnection(string build, string sender, string serializer)
    {
        var game = LoadTable(build);
        var relay = game.ForConnection(OpcodeConnection.Relay);
        Assert.False(game.TryGet(PacketDirection.ClientToServer, 20008, out _));
        Assert.True(relay.TryGet(PacketDirection.ClientToServer, 20008, out var entry));
        Assert.Equal("rcpReqLeaveGame", entry!.Name);
        Assert.Equal(OpcodeConnection.Relay, entry.Connection);
        Assert.Equal(Evidence.Verified, entry.Evidence);
        Assert.Contains(sender, entry.Source, StringComparison.Ordinal);
        Assert.Contains(serializer, entry.Source, StringComparison.Ordinal);
        Assert.Equal((ushort)20008, relay.C2S("rcpReqLeaveGame"));
        Assert.Equal((ushort)404, game.C2S("gcpReqLeaveSession"));
        Assert.Equal((ushort)506, game.C2S("gcpRoomReqLeaveGame"));
        Assert.DoesNotContain(relay.All, e => e.Name is "gcpReqLeaveSession" or "gcpRoomReqLeaveGame");
    }

    private static IOpcodeTable LoadTable(string build)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "profiles", build, "opcodes.yaml");
            if (File.Exists(path))
                return OpcodeTableLoader.Load(path, new ClientProfile(build, build.Split('-')[0], 0, 0, 0, build));
        }
        throw new InvalidOperationException($"The repository opcode table for '{build}' was not found.");
    }
}
