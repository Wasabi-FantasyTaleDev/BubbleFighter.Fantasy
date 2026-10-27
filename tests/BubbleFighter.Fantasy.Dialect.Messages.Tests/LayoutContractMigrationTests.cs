using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Rejection tests for the four layout-contract migrations: a table whose structure keeps a consumed field's name
/// but changes its type must fail when the binding's layout is checked, not on the first packet.
/// </summary>
public sealed class LayoutContractMigrationTests
{
    /// <summary>The legacy login reply writer checks the result word it writes as u32.</summary>
    [Fact]
    public void LoginMessageCodecRejectsARetypedReplyField()
    {
        var tables = Edited("LoginReply", "result", "u32", "wstr");
        var values = LoginValues.From("kr-812", tables.ValuesOf("kr-812"));
        var error = Assert.Throws<ConfigException>(() => LoginMessageCodec.RequireLayouts(tables, "kr-812", values));
        Assert.Contains("LoginReply.result", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The party snapshot writer checks the member fields it fills.</summary>
    [Fact]
    public void LoginPartySnapshotRejectsARetypedMemberField()
    {
        var tables = Edited("SoloPartyMember", "user_number", "u32", "wstr");
        var error = Assert.Throws<ConfigException>(() => LoginPartySnapshot.RequireLayouts(tables, "kr-1256"));
        Assert.Contains("SoloPartyMember.user_number", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The main-menu install checks the seat fields it fills. The kr-1256 seat is its own
    /// restated structure, so the retyped field is edited there; the baseline block never reaches the
    /// kr-1256 view.</summary>
    [Fact]
    public void MainMenuInstallRejectsARetypedSeatField()
    {
        var tables = Edited("UserSlot@kr-1256", "level", "u32", "wstr");
        var error = Assert.Throws<ConfigException>(() => MainMenuInstall.RequireLayouts(tables, "kr-1256"));
        Assert.Contains("UserSlot.level", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The native offer row checks the endpoint field the offer writer copies.</summary>
    [Fact]
    public async Task MatchOffersRejectsARetypedOfferEndpoint()
    {
        var edited = PacketTableEdits.WithFieldType("RelayConnectionOffer", "endpoint", "RelayEndpoint", "u32");
        var profile = Profile("kr-812");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.match-offers", edited.FullName);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() =>
                new MatchOffersPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("RelayConnectionOffer.endpoint", error.Message, StringComparison.Ordinal);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The relay connection wire checks the endpoint words the offer writer copies.</summary>
    [Fact]
    public void RelayConnectionWireRejectsARetypedEndpointPort()
    {
        var tables = Edited("RelayEndpoint", "port", "u16", "wstr");
        var error = Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(
            () => RelayConnectionWire.RequireLayouts(tables, "kr-812"));
        Assert.Contains("RelayEndpoint.port", error.Message, StringComparison.Ordinal);
    }

    private static PacketTables Edited(string structure, string field, string from, string to) =>
        PacketTables.Load(PacketTableEdits.WithFieldType(structure, field, from, to).FullName);

    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
}
