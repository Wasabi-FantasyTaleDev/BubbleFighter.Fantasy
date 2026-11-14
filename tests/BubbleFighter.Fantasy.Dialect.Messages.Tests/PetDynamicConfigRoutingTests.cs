using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises pet settings through mounted native request routes and character creation senders.</summary>
public sealed class PetDynamicConfigRoutingTests
{
    /// <summary>Late providers are protocol scoped, resolved once per request, and removed without changing base content.</summary>
    [Theory]
    [InlineData("kr-1256", 4)]
    [InlineData("cn-774", 2)]
    public async Task LateProviderSuppliesOneCompleteSnapshotPerRequest(string build, int maskBytes)
    {
        var table = Table(build);
        var protocol = build + "/game";
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        {
            ["login"] = true, ["inventory"] = true, ["character_creation"] = true,
        });
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var first = new PetClientConfiguration(317, 12600);
        var second = new PetClientConfiguration(409, 25200);
        var calls = 0;
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.Equal(protocol, Assert.Single(fixture.Services.GetAll<IPetClientConfigurationSink>()).Protocol);
            if (build == "cn-774")
                Assert.Empty(fixture.Services.GetAll<IMessageSender<CharacterCreationOpened>>());
            async Task Request(uint stamp) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpReqDynamicConfig"), PetDynamicConfigTestData.Request(maskBytes, 1u << 14, stamp)), default);

            await Request(0);
            Assert.Equal(table.S2C("gspRepDynamicConfig"), Assert.Single(session.Sent));
            var baseline = Assert.Single(session.Bodies);
            if (build == "cn-774") Assert.Equal(new byte[2], baseline);
            using var unrelated = context.Events.Intercept<PetClientConfigurationRequested, PetClientConfiguration?>(
                (request, next, ct) => request.Protocol == "other/game"
                    ? ValueTask.FromResult<PetClientConfiguration?>(second) : next(ct));
            await Request(0);
            Assert.Equal(baseline, session.Bodies[^1]);

            var current = first;
            using (context.Events.Intercept<PetClientConfigurationRequested, PetClientConfiguration?>((request, next, ct) =>
            {
                if (request.Protocol != protocol) return next(ct);
                calls++;
                var selected = current;
                current = second;
                return ValueTask.FromResult<PetClientConfiguration?>(selected);
            }))
            {
                await Request(0);
                Assert.Equal(1, calls);
                var firstReply = session.Bodies[^1];
                PetDynamicConfigTestData.AssertSettings(firstReply, maskBytes, first);
                var firstStamp = DynamicConfigWire.Checksum(PetDynamicConfigTestData.Compressed(firstReply, maskBytes));

                await Request(firstStamp);
                Assert.Equal(2, calls);
                var secondReply = session.Bodies[^1];
                PetDynamicConfigTestData.AssertSettings(secondReply, maskBytes, second);
                var secondStamp = DynamicConfigWire.Checksum(PetDynamicConfigTestData.Compressed(secondReply, maskBytes));
                Assert.NotEqual(firstStamp, secondStamp);
                await Request(secondStamp);
                Assert.Equal(3, calls);
                Assert.Equal(table.S2C("gspReqPing"), session.Sent[^1]);
                Assert.Empty(session.Bodies[^1]);
            }

            await Request(0);
            Assert.Equal(3, calls);
            Assert.Equal(table.S2C("gspRepDynamicConfig"), session.Sent[^1]);
            Assert.Equal(baseline, session.Bodies[^1]);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IPetClientConfigurationSink>());
        Assert.Empty(fixture.Packets.Handlers);
    }

    /// <summary>Creation sends the exact request generation before its open packet, including providers added after mount.</summary>
    [Fact]
    public async Task KoreanCreationAndRequestsUseTheSameCurrentSettings()
    {
        const string build = "kr-1256";
        const string protocol = "kr-1256/game";
        var table = Table(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        {
            ["login"] = true, ["inventory"] = true, ["character_creation"] = true,
        });
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var current = new PetClientConfiguration(317, 12600);
        var calls = 0;
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            var creation = Assert.Single(fixture.Services.GetAll<IMessageSender<CharacterCreationOpened>>());
            await creation.SendAsync(session, new(), default);
            Assert.Equal(new[] { table.S2C("gspRepDynamicConfig"), table.S2C("gspNotifyCreateUserInfo") }, session.Sent);
            var baseline = session.Bodies[0];
            using (context.Events.Intercept<PetClientConfigurationRequested, PetClientConfiguration?>((request, next, ct) =>
            {
                if (request.Protocol != protocol) return next(ct);
                calls++;
                return ValueTask.FromResult<PetClientConfiguration?>(current);
            }))
            {
                foreach (var settings in new[] { current, new PetClientConfiguration(409, 25200) })
                {
                    current = settings;
                    session.Sent.Clear();
                    session.Bodies.Clear();
                    var before = calls;
                    await fixture.Packets.DispatchAsync(protocol, session,
                        new(table.C2S("gcpReqDynamicConfig"), PetDynamicConfigTestData.Request(4, 1u << 14, 0)), default);
                    Assert.Equal(before + 1, calls);
                    await creation.SendAsync(session, new(), default);
                    Assert.Equal(before + 2, calls);
                    Assert.Equal(new[] { table.S2C("gspRepDynamicConfig"), table.S2C("gspRepDynamicConfig"),
                        table.S2C("gspNotifyCreateUserInfo") }, session.Sent);
                    Assert.Equal(session.Bodies[0], session.Bodies[1]);
                    PetDynamicConfigTestData.AssertSettings(session.Bodies[1], 4, settings);
                    Assert.Empty(session.Bodies[2]);
                }
            }
            session.Sent.Clear();
            session.Bodies.Clear();
            await creation.SendAsync(session, new(), default);
            Assert.Equal(4, calls);
            Assert.Equal(baseline, session.Bodies[0]);
            Assert.Equal(table.S2C("gspNotifyCreateUserInfo"), session.Sent[1]);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>An admitted layout publishes no settings sink unless this row mounts its DynamicConfig request route.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("cn-774", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    public async Task SettingsSinkRequiresAMountedRequestRoute(string build, bool login)
    {
        var full = Table(build);
        var table = login ? new OpcodeTable(full.Profile, full.All.Where(row => row.Name != "gcpReqDynamicConfig")) : full;
        var protocol = build + "/game";
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        {
            ["login"] = login, ["inventory"] = login,
        });
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.Empty(fixture.Services.GetAll<IPetClientConfigurationSink>());
            Assert.False(fixture.Packets.Handlers.ContainsKey((protocol, full.C2S("gcpReqDynamicConfig"))));
        }
        finally { await context.UnwindAsync(); }
    }

    private static IOpcodeTable Table(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        return OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
    }
}
