using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins observation independently of the game-mode adapter that mounts it.</summary>
public sealed class RoundObservationSenderTests
{
    /// <summary>The body is a four-byte scene-object reference, without a seat or user-number conversion.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    public void ObserverPinsFourBytes(string build)
    {
        var body = RoundObservationSender.WriteBody(TestBuilds.Packets, build, new(1_100_000));
        Assert.Equal("E0C81000", Convert.ToHexString(body));
        Assert.Equal(1_100_000, (int)PacketCodec.ReadScalars(TestBuilds.Packets, build, "gspObserver", body)["actor"]);
    }

    /// <summary>Shared packet layouts do not bypass the native mode gate or grant unaudited builds observation.</summary>
    [Theory]
    [InlineData("kr-812", 19, true)]
    [InlineData("kr-812", 1, false)]
    [InlineData("kr-812", 20, false)]
    [InlineData("kr-812", 106, false)]
    [InlineData("kr-1256", 19, true)]
    [InlineData("cn-774", 19, true)]
    [InlineData("tw-454", 19, true)]
    [InlineData("cn-279", 19, false)]
    [InlineData("cn-324", 19, false)]
    [InlineData("cn-404", 19, true)]
    [InlineData("cn-406", 19, false)]
    [InlineData("cn-408", 19, true)]
    [InlineData("cn-705", 19, false)]
    [InlineData("cn-706", 19, true)]
    [InlineData("cn-717", 19, true)]
    [InlineData("cn-717", 1, false)]
    [InlineData("cn-717", 20, false)]
    [InlineData("cn-738", 19, false)]
    [InlineData("cn-742", 19, false)]
    [InlineData("id-400", 19, true)]
    [InlineData("id-1809", 19, true)]
    public void ObservationRequiresAuditedBuildAndMode(string build, int mode, bool supported)
    {
        var profile = new ClientProfile(build, "kr", 812, 0, 0, build);
        var sender = new RoundObservationSender(build + "/game", new OpcodeTable(profile, []), TestBuilds.Packets, build);
        Assert.Equal(supported, sender.SupportsMode(mode));
    }

    /// <summary>An absent opcode cannot publish a usable-looking observer sender.</summary>
    [Fact]
    public async Task MissingObserverOpcodeFailsBeforePublication()
    {
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        var table = new OpcodeTable(profile, []);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.rooms");
        try
        {
            var error = Assert.Throws<ConfigException>(() =>
                new RoundObservationSender("kr-812/game", table, TestBuilds.Packets, "kr-812").Mount(context));
            Assert.Contains("gspObserver", error.Message);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPlayerObserved>>());
            Assert.Empty(fixture.Services.GetAll<IRoundObservationSender>());
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }
}
