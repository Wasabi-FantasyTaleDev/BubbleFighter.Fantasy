using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Own-couple refreshes remain available when the build does not enable inventory-use routing.</summary>
public sealed class CoupleServicesTests
{
    /// <summary>Exactly one native user-stat publisher is available and unloads with the dialect row.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    public async Task OwnProfileRefreshDoesNotDependOnInventoryUse(string build, bool itemUse)
    {
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol, "dialect.messages", extra: new()
        {
            ["login"] = false, ["inventory_item_use"] = itemUse,
        });
        context.BindServices(typeof(MessagesPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            await new MessagesPlugin().ActivateAsync(context, default);
            Assert.Equal(protocol, Assert.Single(fixture.Services.GetAll<IMessageSender<UserStatUpdated>>()).Protocol);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Services.GetAll<IMessageSender<UserStatUpdated>>());
    }
}
