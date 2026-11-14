using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks native title identities, independent medal snapshots and reversible route installation.</summary>
public sealed class TitleBindingTests
{
    /// <summary>Native request words preserve their meaning and reject any missing or trailing byte.</summary>
    [Theory]
    [InlineData("kr-812", "gcpMakeUserTitle", "2A000000811A0600")]
    [InlineData("kr-812", "gcpSetUserTitle", "2A00000001000000811A0600")]
    [InlineData("kr-812", "gcpReqAwardInfo", "FFFFFFFF")]
    [InlineData("kr-1256", "gcpMakeUserTitle", "2A000000811A0600")]
    [InlineData("kr-1256", "gcpSetUserTitle", "2A00000001000000811A0600")]
    [InlineData("kr-1256", "gcpReqAwardInfo", "FFFFFFFF")]
    public async Task RequestsReachTypedHandlersAndRejectMalformedBodies(string build, string name, string hex)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new TitleBinding(build + "/game", table, TestBuilds.Packets, build);
        try
        {
            binding.Bind(context, fixture.Messages);
            var body = Convert.FromHexString(hex);
            await Request(body);
            IMessage expected = name switch
            {
                "gcpMakeUserTitle" => new TitleMakeRequested(42, 400001),
                "gcpSetUserTitle" => new TitleSetRequested(42, 1, 400001),
                _ => new MedalInfoRequested(-1),
            };
            Assert.Equal(expected, Assert.Single(fixture.Messages.Received));
            for (var length = 0; length < body.Length; length++)
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(body[..length]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request([.. body, 0]));
            Assert.Single(fixture.Messages.Received);
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(body));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<TitleMakeRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<TitleSetRequested>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MedalInfoRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TitleMakeReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TitleSetReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TitleCatalogueConcealed>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<MedalInfoReplied>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<TitleMedalPresented>>());

        Task Request(byte[] body) => fixture.Packets.DispatchAsync(build + "/game", session,
            new(table.C2S(name), body), default).AsTask();
    }

    /// <summary>Every output pins its native identity, zero or empty wire-only defaults, byte length and symbolic opcode.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("kr-812", true)]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    public async Task RepliesKeepStockMedalAndConcealmentIdentitiesSeparate(string build, bool remap)
    {
        var full = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(full.Profile, full.All.Select(row => row with
        { Value = checked((ushort)(row.Value + 1000)) })) : full;
        var binding = new TitleBinding(build + "/game", table, TestBuilds.Packets, build);
        var session = new MatchMessagesMountTests.Session(table.Profile);
        await binding.SendAsync(session, new TitleMakeReplied(3, 400001, 1200001), default);
        AssertReply("gspResMakeUserTitle", "03000000814F120000000000", 12);
        await binding.SendAsync(session, new TitleMakeReplied(0, 400001, 1200001), default);
        AssertReply("gspResMakeUserTitle", build == "kr-1256"
            ? "00000000811A060000000000" : "00000000814F120000000000", 12);
        await binding.SendAsync(session, new TitleSetReplied(2, 400001), default);
        AssertReply("gspResSetUserTitle", build == "kr-1256"
            ? "0200000000000000811A0600" : "020000000000000000000000", 12);
        await binding.SendAsync(session, new TitleCatalogueConcealed([400001, 400002]), default);
        AssertReply("gspTitleSyncInfo", "02000000811A0600821A0600", 12);
        await binding.SendAsync(session, new MedalInfoReplied(1, [new(30007, 20), new(30008, 30)]), default);
        AssertReply("gspRepAwardInfo", "0100000000000000020000003775000014000000387500001E000000", 28);
        await binding.SendAsync(session, new MedalInfoReplied(1, []), default);
        AssertReply("gspRepAwardInfo", "010000000000000000000000", 12);
        await binding.SendAsync(session, new TitleMedalPresented(30007), default);
        AssertReply("gspProvideMedal", "3775000000000000", 8);
        session.FailSend = true;
        await Assert.ThrowsAsync<IOException>(async () =>
            await binding.SendAsync(session, new TitleSetReplied(1, 400001), default));

        void AssertReply(string name, string hex, int length)
        {
            Assert.Equal(table.S2C(name), session.Sent[^1]);
            Assert.Equal(length, session.Bodies[^1].Length);
            Assert.Equal(hex, Convert.ToHexString(session.Bodies[^1]));
        }
    }

    /// <summary>The collection size is rejected before output projection exceeds one native packet.</summary>
    [Fact]
    public void OversizedSnapshotsAreRejectedBeforeEncoding()
    {
        var binding = new TitleBinding("kr-812/game", TestBuilds.Opcodes("kr-812"), TestBuilds.Packets, "kr-812");
        Assert.Throws<ArgumentOutOfRangeException>(() => binding.Write(new TitleCatalogueConcealed(new int[16383])));
        Assert.Throws<ArgumentOutOfRangeException>(() => binding.Write(new MedalInfoReplied(1, new TitleMedalCount[8191])));
    }

    /// <summary>Each Korean build keeps the request and response numbers established by its own native paths.</summary>
    [Theory]
    [InlineData("kr-812", 929, 930, 116, 933, 934, 935, 125, 936)]
    [InlineData("kr-1256", 925, 926, 119, 930, 931, 932, 134, 934)]
    public void NativeOpcodeNumbersArePinned(string build, int make, int set, int query, int makeReply,
        int setReply, int conceal, int medals, int presentation)
    {
        var table = TestBuilds.Opcodes(build);
        Assert.Equal(make, table.C2S("gcpMakeUserTitle"));
        Assert.Equal(set, table.C2S("gcpSetUserTitle"));
        Assert.Equal(query, table.C2S("gcpReqAwardInfo"));
        Assert.Equal(makeReply, table.S2C("gspResMakeUserTitle"));
        Assert.Equal(setReply, table.S2C("gspResSetUserTitle"));
        Assert.Equal(conceal, table.S2C("gspTitleSyncInfo"));
        Assert.Equal(medals, table.S2C("gspRepAwardInfo"));
        Assert.Equal(presentation, table.S2C("gspProvideMedal"));
    }

    /// <summary>A missing or unaudited operation prevents any partial route or sender installation.</summary>
    [Theory]
    [InlineData("gcpMakeUserTitle")]
    [InlineData("gcpSetUserTitle")]
    [InlineData("gcpReqAwardInfo")]
    [InlineData("gspResMakeUserTitle")]
    [InlineData("gspResSetUserTitle")]
    [InlineData("gspTitleSyncInfo")]
    [InlineData("gspRepAwardInfo")]
    [InlineData("gspProvideMedal")]
    public async Task EveryOpcodeMustBeVerifiedBeforeBinding(string name)
    {
        var full = TestBuilds.Opcodes("kr-812");
        foreach (var remove in new[] { true, false })
        {
            var table = new OpcodeTable(full.Profile, full.All.Where(row => !remove || row.Name != name)
                .Select(row => !remove && row.Name == name ? row with { Evidence = Evidence.Inferred } : row));
            using var fixture = new MatchMessagesMountTests.Fixture(table);
            var context = fixture.Context("kr-812", "kr-812/game", "dialect.messages");
            try
            {
                Assert.Throws<ConfigException>(() =>
                    new TitleBinding("kr-812/game", table, TestBuilds.Packets, "kr-812").Bind(context, fixture.Messages));
                Assert.Empty(fixture.Packets.Handlers);
                Assert.Empty(fixture.Services.GetAll<IMessageSender<MedalInfoReplied>>());
            }
            finally { await context.UnwindAsync(); }
        }
    }

    /// <summary>Builds without the audited title lifecycle cannot inherit activation from physical packet shapes.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    public async Task UnauditedLifecyclesDoNotBind(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.messages");
        try
        {
            Assert.Throws<ConfigException>(() =>
                new TitleBinding(build + "/game", table, TestBuilds.Packets, build).Bind(context, fixture.Messages));
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }
}
