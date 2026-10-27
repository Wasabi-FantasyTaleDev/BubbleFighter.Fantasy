using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the waiting room's observer-team request, <c>gcpRoomChangeTeamObserver</c>, stated in
/// <c>packets/observer-team.yaml</c>. Its kr-812 body is empty: the deserialize slot kr-812 sub_5C8FC0 reads
/// nothing and the sender kr-812 sub_5C87A0 writes nothing before it stamps the request. The same file also
/// pins which builds' opcode tables carry the name, because a build without the row must not bind it:
/// kr-1256 has no row (its C2S 2009 is an unnamed packet) and cn-774's C2S 2009 is the level-icon
/// notification.
/// </summary>
public sealed class ObserverTeamTablePins
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The kr-812 request carries no field: its body is empty both when written and when read.</summary>
    [Fact]
    public void Kr812ObserverTeamRequestIsEmpty()
    {
        var body = PacketCodec.Write(Tables, "kr-812", "RoomObserverTeamChangeRequest", new Dictionary<string, object?>());

        Assert.Empty(body);
        Assert.Empty(PacketCodec.ReadScalars(Tables, "kr-812", "RoomObserverTeamChangeRequest", body));
    }

    /// <summary>The structure has no field row, for kr-812 and for a build that inherits its table.</summary>
    [Fact]
    public void TheObserverTeamRequestHasNoField()
    {
        Assert.Empty(Tables.Resolve("kr-812", "RoomObserverTeamChangeRequest"));
        Assert.Empty(Tables.Resolve("cn-717", "RoomObserverTeamChangeRequest"));
    }

    /// <summary>
    /// The builds whose game tables name the request, and the kr-812 opcode the audited line assigns it.
    /// </summary>
    [Fact]
    public void TheNamedBuildsCarryTheRequest()
    {
        foreach (var build in new[] { "kr-812", "cn-717", "id-400", "id-1809", "cn-404", "cn-408", "cn-706" })
        {
            Assert.Contains(TestBuilds.Opcodes(build).All,
                entry => entry.Direction == PacketDirection.ClientToServer && entry.Name == "gcpRoomChangeTeamObserver");
        }

        Assert.Contains(TestBuilds.Opcodes("kr-812").All,
            entry => entry.Direction == PacketDirection.ClientToServer
                && entry.Name == "gcpRoomChangeTeamObserver" && entry.Value == 2009);
    }

    /// <summary>
    /// The builds that must not resolve the name: both modern builds where C2S 2009 is another packet, and
    /// tw-454 whose table does not carry it.
    /// </summary>
    [Fact]
    public void TheModernBuildsDoNotCarryTheName()
    {
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            Assert.DoesNotContain(TestBuilds.Opcodes(build).All,
                entry => entry.Name == "gcpRoomChangeTeamObserver");
        }
    }
}
