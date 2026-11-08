using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native live-score and result bursts across the fixed-row and masked-row result grammars.</summary>
public sealed class SharedRoundResultBurstTests
{
    /// <summary>Only the audited native build receives the absolute pickup counter; remapped opcodes remain authoritative.</summary>
    [Theory]
    [InlineData("kr-812", 0, true)]
    [InlineData("kr-812", 5000, true)]
    [InlineData("cn-717", 0, false)]
    [InlineData("cn-324", 0, false)]
    [InlineData("kr-1256", 0, false)]
    public async Task CoinPickupPublishesOnlyToAnAuditedCounter(string build, int shift, bool sends)
    {
        var native = TestBuilds.Opcodes(build);
        var table = new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + shift)) }));
        var session = new Session(native.Profile);
        var sender = build == "kr-1256"
            ? new RoundResultSender(build + "/game", TestBuilds.Packets, build, _ => table, [],
                new("slot", new Dictionary<string, object?>()), null)
            : new RoundResultSender(build + "/game", TestBuilds.Packets, build, _ => table, TestBuilds.Of(build).Play.Score);
        await sender.SendAsync(session,
            new RoundScoreUpdated(1, 0, [0, 0], [new(0, 0, 0, 0, 0) { CollectedLucci = 100 }])
                { CollectedLucciOnly = true }, default);
        if (sends)
        {
            var frame = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspUpdateScoreData"), frame.Opcode);
            Assert.Equal("00000000FF010000000D640000000000", Convert.ToHexString(frame.Body));
        }
        else Assert.Empty(session.Sent);
    }

    /// <summary>Pins CN2011's empty pirate outcome before the end gate, including timeout and remapped opcodes.</summary>
    [Theory]
    [InlineData(101, false, false)]
    [InlineData(101, false, true)]
    [InlineData(101, true, false)]
    [InlineData(101, true, true)]
    [InlineData(102, false, false)]
    [InlineData(102, false, true)]
    [InlineData(102, true, false)]
    [InlineData(102, true, true)]
    [InlineData(103, false, false)]
    [InlineData(103, false, true)]
    [InlineData(103, true, false)]
    [InlineData(103, true, true)]
    public async Task Cn279PirateOutcomePrecedesTheEndGate(int mode, bool won, bool remap)
    {
        var native = TestBuilds.Opcodes("cn-279");
        var shift = remap ? 5000 : 0;
        IOpcodeTable table = remap ? new OpcodeTable(native.Profile,
            native.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + shift)) })) : native;
        var (_, play) = TestBuilds.Of("cn-279");
        var sender = new RoundResultSender("cn-279/game", TestBuilds.Packets, "cn-279", _ => table, play.Score);
        var session = new Session(native.Profile);
        await sender.SendAsync(session, new RoundEnded(1, won ? RoundOutcome.Won : RoundOutcome.Lost,
            won ? RoundEndReason.WavesCleared : RoundEndReason.TimeLimit) { ModeId = mode }, default);
        Assert.Collection(session.Sent,
            frame =>
            {
                Assert.Equal((ushort)((won ? 906 : 907) + shift), frame.Opcode);
                Assert.Empty(frame.Body);
            },
            frame =>
            {
                Assert.Equal((ushort)(705 + shift), frame.Opcode);
                Assert.Equal(won ? "0000000003000000" : "0000000002000000", Convert.ToHexString(frame.Body));
            });
    }

    /// <summary>Rejects a missing required pirate notification before a native round can reach settlement.</summary>
    [Theory]
    [InlineData("gspHordeStageCleared_")]
    [InlineData("gspHordeStageFailed_")]
    public async Task Cn279MountRejectsMissingPirateOutcome(string missing)
    {
        var native = TestBuilds.Opcodes("cn-279");
        var table = new OpcodeTable(native.Profile, native.All.Where(entry => entry.Name != missing));
        var services = new ServiceRegistry();
        using var routes = services.Register<IPacketRouter>(new Routes());
        using var messages = services.Register<IMessageRouter>(new Messages());
        var context = new PluginContext(new PluginRow { Id = "pirate-outcome", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of("cn-279");
        var codec = new PlayRoomCodec("cn-279/game", "cn-279", schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        try
        {
            var error = Assert.Throws<ConfigException>(() => codec.Mount(context));
            Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(services.GetAll<IMessageSender<RoundEnded>>());
    }

    /// <summary>Exercises sparse rows, rewards, time-limit and draw presentation with recipient-local opcode mappings.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-406", false)]
    [InlineData("cn-408", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", false)]
    [InlineData("cn-717", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task NativeResultBurstPreservesBytes(string build, bool modern)
    {
        foreach (var variant in new[] { "live", "ordinary", "time", "draw" })
        foreach (var remap in new[] { false, true })
        {
            var native = TestBuilds.Opcodes(build);
            IOpcodeTable table = remap ? new OpcodeTable(native.Profile,
                native.All.Concat(native.ForConnection(OpcodeConnection.Relay).All).Distinct()
                    .Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : native;
            Assert.Equal(native.ForConnection(OpcodeConnection.Relay).All.Count(),
                table.ForConnection(OpcodeConnection.Relay).All.Count());
            var session = new Session(table.Profile);
            var services = new ServiceRegistry();
            using var registration = services.Register<IPacketRouter>(new Routes());
            using var messages = services.Register<IMessageRouter>(new Messages());
            using var texts = services.Register<IGameText>(new Texts());
            var context = new PluginContext(new PluginRow { Id = "result-pin", Plugin = "test", Source = "test" },
                services, new EventBus(NullLogger.Instance), NullLogger.Instance);
            object sender;
            if (modern)
            {
                var binding = new RoundResultBinding(build + "/game", table, TestBuilds.Packets, build, [1], NullLogger.Instance);
                binding.Mount(context);
                sender = Assert.Single(services.GetAll<IMessageSender<RoundScoreboard>>());
            }
            else
            {
                var (schema, play) = TestBuilds.Of(build);
                var binding = new PlayRoomCodec(build + "/game", build, schema, play,
                    new Tables(table), NullLogger.Instance, TestBuilds.Packets);
                binding.Mount(context);
                sender = Assert.Single(services.GetAll<IMessageSender<RoundScoreboard>>());
            }
            try
            {
                Assert.IsType<RoundResultSender>(sender);
                Assert.Same(sender, Assert.Single(services.GetAll<IMessageSender<RoundEnded>>()));
                Assert.Same(sender, Assert.Single(services.GetAll<IMessageSender<RoundScoreUpdated>>()));
                Assert.Same(sender, Assert.Single(services.GetAll<IMessageSender<ResultStageAdvanced>>()));
                RoundSeatStats[] stats = [new(3, 4, 5, 19, 2) { Stars = 7 }, new(1, 1, 2, 8, 4)];
                var draw = variant == "draw";
                int[] totals = draw ? [9, 9] : [11, 22];
                var winner = draw ? 0 : 2;
                var board = Board(false);
                if (variant == "live")
                    await Send(new RoundScoreUpdated(7, 2, totals, stats, board));
                else
                {
                    var policy = Assert.IsAssignableFrom<IRoundResultSequencePolicy>(sender);
                    var timeOver = variant == "time";
                    if (!timeOver && !policy.EndBeforeScoreboard) await Send(board);
                    await Send(new RoundEnded(7, draw ? RoundOutcome.Draw : RoundOutcome.Lost,
                        timeOver ? RoundEndReason.TimeLimit : RoundEndReason.WinCondition) { ModeId = 1, RecipientTeam = 1 });
                    if (timeOver || policy.EndBeforeScoreboard)
                        await Send(Board(policy.SingleScoreboard) with { TimeOver = timeOver });
                    if (!policy.SingleScoreboard) await Send(Board(true));
                    await Send(new ResultStageAdvanced(7, ResultStage.Reward));
                    var beforeIgnored = session.Sent.Count;
                    await Send(new ResultStageAdvanced(7, ResultStage.Scoreboard));
                    Assert.Equal(beforeIgnored, session.Sent.Count);
                }
                Assert.NotEmpty(session.Sent);
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                    string.Join('/', session.Sent.Select(frame => Convert.ToHexString(frame.Body))))));
                var codes = string.Join(',', session.Sent.Select(frame => frame.Opcode - (remap ? 5000 : 0)));
                var lengths = string.Join(',', session.Sent.Select(frame => frame.Body.Length));
                var expected = PinnedBursts[build + ":" + variant];
                Assert.Equal(expected.Codes, codes);
                Assert.Equal(expected.Lengths, lengths);
                Assert.Equal(expected.Digest, digest);

                RoundScoreboard Board(bool rewards) => new(7, winner, totals,
                    [Row(3, "Alpha", 1, 101, stats[0], rewards), Row(1, "Beta", 2, 102, stats[1], rewards)], rewards)
                    { ModeId = 1 };

                RoundScoreboardRow Row(int slot, string nickname, int team, int user, RoundSeatStats seat, bool rewards) =>
                    new(slot, nickname, team, 12, seat, rewards ? new(13, 1000, 1234, 2000, 234, 567, true, []) : null)
                    { UserNumber = user, Outcome = draw ? RoundOutcome.Draw : team == winner ? RoundOutcome.Won : RoundOutcome.Lost,
                        AwardDisplayId = 203001, NicknameKey = slot == 3 ? "ai.alpha" : "" };

                ValueTask Send<T>(T message) where T : IMessage =>
                    ((IMessageSender<T>)sender).SendAsync(session, message, default);
            }
            finally { await context.UnwindAsync(); }
            Assert.Empty(services.GetAll<IMessageSender<RoundScoreboard>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundEnded>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundScoreUpdated>>());
            Assert.Empty(services.GetAll<IMessageSender<ResultStageAdvanced>>());
        }
    }

    private static readonly IReadOnlyDictionary<string, (string Codes, string Lengths, string Digest)> PinnedBursts =
        new Dictionary<string, (string, string, string)>
        {
            ["cn-279:draw"] = ("705,725,722,726", "8,1426,1426,0", "39F936956792076E47D6DF1213F9978D78F06F48B6EF018C62B216DA9E56692B"),
            ["cn-279:live"] = ("722", "1426", "74FC956AB0D331C0DF7FA419E304C076FAA66188531BF101F6B028E926195919"),
            ["cn-279:ordinary"] = ("705,725,722,726", "8,1426,1426,0", "D36EE2ABB2E605749328E404B2FAF59433DEE904A743F2AE996F3B8F19015145"),
            ["cn-279:time"] = ("705,725,722,726", "8,1426,1426,0", "D36EE2ABB2E605749328E404B2FAF59433DEE904A743F2AE996F3B8F19015145"),
            ["cn-324:draw"] = ("705,732,729,733", "8,2014,2014,0", "068BAC4080BEF98B2FB29EE8D748C72E1B2B2F5F4922863A898DDBE6916DC2F5"),
            ["cn-324:live"] = ("729", "2014", "66036F8DD13BF4CBBF23DEAE242E8644501E8B1A631E72F82CF120D2C418BBD2"),
            ["cn-324:ordinary"] = ("705,732,729,733", "8,2014,2014,0", "1386E50F413E4E57098E09E86B0383AF959FC02636B3E3B5647FFD668D31DB32"),
            ["cn-324:time"] = ("705,732,729,733", "8,2014,2014,0", "1386E50F413E4E57098E09E86B0383AF959FC02636B3E3B5647FFD668D31DB32"),
            ["cn-404:draw"] = ("705,732,729,733", "8,3250,3250,0", "F835327BD5968553B265F36DB51193F620B9BF5018299E45748BC50B3B9E705B"),
            ["cn-404:live"] = ("729", "3250", "2064E0A1ADFC29B16E66A83E2DC34413ABEABE47F02B60515990385FE37A9996"),
            ["cn-404:ordinary"] = ("705,732,729,733", "8,3250,3250,0", "866B89FA105F0B91CC3571142AAD3E57F92197F22A580A0FA7F7A75DBE6DFEC9"),
            ["cn-404:time"] = ("705,732,729,733", "8,3250,3250,0", "866B89FA105F0B91CC3571142AAD3E57F92197F22A580A0FA7F7A75DBE6DFEC9"),
            ["cn-406:draw"] = ("705,732,729,733", "8,2094,2094,0", "F8DB17B6A57107A2572D811999B01D81044F07E6D1BB2FDE42F2EDEEC92533CF"),
            ["cn-406:live"] = ("729", "2094", "5EEA317E4C8700E7F5EC5683B0B993E888A307510FDF2E55BACBB27EC1BD0455"),
            ["cn-406:ordinary"] = ("705,732,729,733", "8,2094,2094,0", "8AB8BF8DA2AAE025F87B21FBA2E8B5C203BA1E90C94D492E7ED8CF475F92C140"),
            ["cn-406:time"] = ("705,732,729,733", "8,2094,2094,0", "8AB8BF8DA2AAE025F87B21FBA2E8B5C203BA1E90C94D492E7ED8CF475F92C140"),
            ["cn-408:draw"] = ("705,732,729,733", "8,3250,3250,0", "F835327BD5968553B265F36DB51193F620B9BF5018299E45748BC50B3B9E705B"),
            ["cn-408:live"] = ("729", "3250", "2064E0A1ADFC29B16E66A83E2DC34413ABEABE47F02B60515990385FE37A9996"),
            ["cn-408:ordinary"] = ("705,732,729,733", "8,3250,3250,0", "866B89FA105F0B91CC3571142AAD3E57F92197F22A580A0FA7F7A75DBE6DFEC9"),
            ["cn-408:time"] = ("705,732,729,733", "8,3250,3250,0", "866B89FA105F0B91CC3571142AAD3E57F92197F22A580A0FA7F7A75DBE6DFEC9"),
            ["cn-705:draw"] = ("729,705,729,733", "3346,8,3346,0", "7FA27DA9B01DAE565D4E38F214E03511535E888943F66850C87F52BD564E55C7"),
            ["cn-705:live"] = ("729", "3346", "4E430D36424C3F09385CAD4B5D016FFF5847AB10DBCDEA0ADB9833977B3C630F"),
            ["cn-705:ordinary"] = ("729,705,729,733", "3346,8,3346,0", "CD523201E3973FC568D5D63F089B0CEA11366F4A1DB7A07D77A9C9DD2DA89E72"),
            ["cn-705:time"] = ("705,732,729,733", "8,2,3346,0", "A357E1B5AC7C52D1AE444A23EC25A0371457F50449F5AA8314EA16D706D6B482"),
            ["cn-706:draw"] = ("729,705,729,733", "3346,8,3346,0", "7FA27DA9B01DAE565D4E38F214E03511535E888943F66850C87F52BD564E55C7"),
            ["cn-706:live"] = ("729", "3346", "4E430D36424C3F09385CAD4B5D016FFF5847AB10DBCDEA0ADB9833977B3C630F"),
            ["cn-706:ordinary"] = ("729,705,729,733", "3346,8,3346,0", "CD523201E3973FC568D5D63F089B0CEA11366F4A1DB7A07D77A9C9DD2DA89E72"),
            ["cn-706:time"] = ("705,732,729,733", "8,2,3346,0", "A357E1B5AC7C52D1AE444A23EC25A0371457F50449F5AA8314EA16D706D6B482"),
            ["cn-717:draw"] = ("729,705,729,733", "4510,8,4510,0", "E40A54B032FB981B18E8B5D8083ADD72F12D1A2627229EEA60CF4D45DE2AA162"),
            ["cn-717:live"] = ("986", "40", "8E6C9B628DFCA7A7714F59DC090962370BDAC5497381E3F4DC61BB5948BF7EA5"),
            ["cn-717:ordinary"] = ("729,705,729,733", "4510,8,4510,0", "60FF8FF44F9EAB5131D5FEC50C5616FB00BA203A8EA013AE6C155A3C933C9809"),
            ["cn-717:time"] = ("705,732,729,733", "8,2,4510,0", "9BB61026FF4597AD248BEB4C84D665A72C867D171C048134D793A4926D9BB3F2"),
            ["cn-774:draw"] = ("603,627,631", "8,620,1", "7C5FD1177EBCE569D85BB87BBD59E951C4C9756F273A7E55E7B03A9BEFF99E99"),
            ["cn-774:live"] = ("985,985", "17,17", "E1B9BC916421F8BCE71DCCFBB5CED57568F91A9EBE55666485DBDCFE6B035337"),
            ["cn-774:ordinary"] = ("603,627,631", "8,620,1", "6F3021108B3B5389B4577C1AC343D7A2648247CB75B2F896DA23EADB7C4E82D1"),
            ["cn-774:time"] = ("603,627,631", "8,620,1", "6F3021108B3B5389B4577C1AC343D7A2648247CB75B2F896DA23EADB7C4E82D1"),
            ["id-1809:draw"] = ("729,705,729,733", "4294,8,4294,0", "08B0004852A960B0B157331410AA310B41820D724E1F43D06143FAEB5D307B03"),
            ["id-1809:live"] = ("729", "4294", "9213C4A4D12E2688C2C8B034743C872F73B600FA6FD00E527CBC496D4C911CAD"),
            ["id-1809:ordinary"] = ("729,705,729,733", "4294,8,4294,0", "C5BEA74A76879B4084F6A0529488C7A57E9DA5C3564C55157F97686FC73E28E0"),
            ["id-1809:time"] = ("705,732,729,733", "8,2,4294,0", "A5F615E980FEAE7F7032E8FEB00DC4E233C97F37309C0FE848F1236A83100C30"),
            ["id-400:draw"] = ("705,732,729,733", "8,2270,2270,0", "F7C1740EF685FD0A62101CBE6E45FA7658B62A4D1586642387B84792D9F03FCB"),
            ["id-400:live"] = ("729", "2270", "14BAE90E55432C79069F6F1C87FAAC71ADDDF2B77F486D7058C98808B7C9BAF3"),
            ["id-400:ordinary"] = ("705,732,729,733", "8,2270,2270,0", "5266ED61347C952A9F654920078091F208FCCDCDE3A9D7543E56DEB5B681FDA6"),
            ["id-400:time"] = ("705,732,729,733", "8,2270,2270,0", "5266ED61347C952A9F654920078091F208FCCDCDE3A9D7543E56DEB5B681FDA6"),
            ["kr-1256:draw"] = ("612,614,615", "8,593,1", "8234FE541159B2DD60DC4BFEA8A8EF80167C1F8E9846B4DBB40E5FA3C64CC5D1"),
            ["kr-1256:live"] = ("985,985", "17,17", "E1B9BC916421F8BCE71DCCFBB5CED57568F91A9EBE55666485DBDCFE6B035337"),
            ["kr-1256:ordinary"] = ("612,614,615", "8,593,1", "D35C8DAF67BABAC3DE8E7C9E4F97EC0D6A8A678779B853608AFE040E606FFC1E"),
            ["kr-1256:time"] = ("612,614,615", "8,593,1", "D35C8DAF67BABAC3DE8E7C9E4F97EC0D6A8A678779B853608AFE040E606FFC1E"),
            ["kr-812:draw"] = ("729,705,729,733", "4518,8,4518,0", "1EBD4523F4E653FB38709EA634F9F694A2071179936B7BB95D8DC580961161CC"),
            ["kr-812:live"] = ("986", "40", "8E6C9B628DFCA7A7714F59DC090962370BDAC5497381E3F4DC61BB5948BF7EA5"),
            ["kr-812:ordinary"] = ("729,705,729,733", "4518,8,4518,0", "5B31D6AC5A3FCA1D2A005F84BA28584D560393F16DE1797D21BE8985A0958D01"),
            ["kr-812:time"] = ("705,732,729,733", "8,2,4518,0", "C159C3ECEC0E408371C153A7287E5ECB675AFB72B8B5EEF5C312F8C9559D34B4"),
            ["tw-454:draw"] = ("603,627,631", "8,590,1", "75E1F4B0FBF14FC70A955766C2966906B9B882F28E7F0328FB5089C528A66091"),
            ["tw-454:live"] = ("984,984", "17,17", "E1B9BC916421F8BCE71DCCFBB5CED57568F91A9EBE55666485DBDCFE6B035337"),
            ["tw-454:ordinary"] = ("603,627,631", "8,590,1", "D3D72C35EE0F92A35C3B22F36453DD746A8FC3871A0D651FD4E968BE2E0AD7D9"),
            ["tw-454:time"] = ("603,627,631", "8,590,1", "D3D72C35EE0F92A35C3B22F36453DD746A8FC3871A0D651FD4E968BE2E0AD7D9"),
        };

    private sealed class Session(ClientProfile profile) : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        { Sent.Add((opcode, payload.ToArray())); return ValueTask.CompletedTask; }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Routes : IPacketRouter
    {
        private readonly Dictionary<(string, ushort), PacketHandler> _handlers = [];
        public IDisposable Register(string protocol, ushort opcode, PacketHandler handler)
        {
            _handlers.Add((protocol, opcode), handler);
            return new Removal(() => _handlers.Remove((protocol, opcode)));
        }
        public ValueTask DispatchAsync(string protocol, ISession session, InboundPacket packet, CancellationToken ct) =>
            _handlers[(protocol, packet.Opcode)](session, packet, ct);
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct)
            where T : IMessage => ValueTask.FromResult(true);
    }

    private sealed class Texts : IGameText
    {
        public string Format(string region, string key, IReadOnlyDictionary<string, string>? arguments = null) =>
            key == "ai.alpha" ? "Localized Alpha" : throw new InvalidOperationException(key);
        public bool TryFormat(string region, string key, out string text, IReadOnlyDictionary<string, string>? arguments = null)
        { text = Format(region, key, arguments); return true; }
    }
}
