using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins each audited Rhea request/reply, strict input decoding and reversible game routing.</summary>
public sealed class CharacterFormBindingTests
{
    /// <summary>Both native frames are actor u32 followed by bool32; invalid lengths and flag words fail.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeBodiesHaveExactEightByteShape(string build)
    {
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "rhea_character_skill"));
        var body = Convert.FromHexString("7856341201000000");
        Assert.Equal(8, body.Length);
        Assert.Equal(new RoundCharacterFormRequested(0x12345678, true), CharacterFormBinding.Read(TestBuilds.Packets, build, body));
        Assert.Equal("7856341201000000", Convert.ToHexString(CharacterFormBinding.Write(TestBuilds.Packets, build, new(0x12345678, true))));
        var ended = CharacterFormBinding.Write(TestBuilds.Packets, build, new(0x12345678, false));
        Assert.Equal(8, ended.Length);
        Assert.Equal("7856341200000000", Convert.ToHexString(ended));
        Assert.Equal(new RoundCharacterFormRequested(0x12345678, false), CharacterFormBinding.Read(TestBuilds.Packets, build, ended));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => CharacterFormBinding.Read(TestBuilds.Packets, build, body[..length]));
        foreach (var invalid in new[] { body.Concat(new byte[1]).ToArray(), Convert.FromHexString("7856341202000000"),
            Convert.FromHexString("78563412FFFFFFFF") })
            Assert.Throws<PacketFormatException>(() => CharacterFormBinding.Read(TestBuilds.Packets, build, invalid));
    }

    /// <summary>Build-owned game numbers and remapped numbers dispatch the same typed exchange and unload fully.</summary>
    [Theory]
    [InlineData("kr-1256", 2072, 1540, false)]
    [InlineData("kr-1256", 2072, 1540, true)]
    [InlineData("cn-774", 2071, 1539, false)]
    [InlineData("cn-774", 2071, 1539, true)]
    [InlineData("tw-454", 2067, 1538, false)]
    [InlineData("tw-454", 2067, 1538, true)]
    public async Task NativeAndRemappedRoutesDispatchAndUnload(string build, int requestCode, int replyCode, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        Assert.Equal(requestCode, full.C2S("gcpReqRheaForm_"));
        Assert.Equal(replyCode, full.S2C("gspRheaForm_"));
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.rooms");
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            new CharacterFormBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundCharacterFormRequested>>());
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpReqRheaForm_"),
                Convert.FromHexString("7856341201000000")), default);
            Assert.Equal(new RoundCharacterFormRequested(0x12345678, true), Assert.Single(fixture.Messages.Received));
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundCharacterFormChanged>>());
            await sender.SendAsync(session, new(0x12345678, true), default);
            Assert.Equal(table.S2C("gspRheaForm_"), Assert.Single(session.Sent));
            Assert.Equal("7856341201000000", Convert.ToHexString(Assert.Single(session.Bodies)));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpReqRheaForm_"), new byte[8]), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundCharacterFormRequested>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundCharacterFormChanged>>());
    }

    /// <summary>Normal profile activation stays off, and unreviewed clients do not inherit this exchange.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("cn-735")]
    public void CharacterSkillNeedsExplicitOptInAndNativeCapability(string build)
    {
        Assert.False(new RoomsOptions().RheaCharacterSkill);
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "rhea_character_skill"));
    }
}
