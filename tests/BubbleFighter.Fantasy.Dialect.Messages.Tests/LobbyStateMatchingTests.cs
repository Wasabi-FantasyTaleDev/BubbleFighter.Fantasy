using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the lobby-state quick-match request: the enter and leave states dispatch, the
/// channel-pop states do not, and the notification body matches the shared table.</summary>
public sealed class LobbyStateMatchingTests
{
    private static PacketTables Tables()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(path, "builds.yaml")))
                return PacketTables.Load(path);
        }
        throw new InvalidOperationException("Repository packet tables were not found.");
    }

    /// <summary>Both builds read the one-u32 state id and reject any other length.</summary>
    [Theory]
    [InlineData("cn-774", 80)]
    [InlineData("cn-774", 60)]
    [InlineData("tw-454", 80)]
    [InlineData("tw-454", 60)]
    public void LobbyStateRequestReadsTheOneWordState(string build, int state)
    {
        var tables = Tables();
        Assert.True(tables.CapabilityOf(build, "quick_matching"));
        var body = BitConverter.GetBytes(state);
        Assert.Equal(state, LobbyStateMatchingBinding.ReadState(tables, build, body));
        Assert.Throws<PacketFormatException>(() => LobbyStateMatchingBinding.ReadState(tables, build, body[..3]));
        Assert.Throws<PacketFormatException>(() => LobbyStateMatchingBinding.ReadState(tables, build, body.Concat(new byte[1]).ToArray()));
    }

    /// <summary>The notification answers through the shared RoomMatchNotification body.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NotificationUsesTheSharedTwelveByteBody(string build)
    {
        var tables = Tables();
        Assert.Equal(new byte[12], LobbyStateMatchingBinding.Write(tables, build, new RoomMatchNotified(true)));
        var refused = LobbyStateMatchingBinding.Write(tables, build, new RoomMatchNotified(false));
        Assert.Equal(12, refused.Length);
        Assert.Equal(1, BitConverter.ToInt32(refused));
        Assert.All(refused[4..], value => Assert.Equal(0, value));
    }
}
