using BubbleFighter.Fantasy.Contracts.Launcher;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class LauncherLoginAccountTests
{
    [Fact]
    public void OrdinaryAccountsRemainUnchangedWhenTicketModeIsEnabledOrDisabled()
    {
        var service = new Tickets();
        Assert.Equal("native-user", LauncherLoginAccount.Resolve("native-user", "cn-406", false, service));
        Assert.Equal("native-user", LauncherLoginAccount.Resolve("native-user", "cn-406", true, service));
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData(false, "cn-406")]
    [InlineData(true, "kr-812")]
    public void DisabledAndWrongBuildTicketsCannotFallBackToAccountCreation(bool enabled, string build)
    {
        Assert.Null(LauncherLoginAccount.Resolve(LauncherLoginAccount.Prefix + new string('a', 64), build, enabled, new Tickets()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedTicketsNeverReachTheRedemptionService(string token)
    {
        var service = new Tickets();
        Assert.Null(LauncherLoginAccount.Resolve(LauncherLoginAccount.Prefix + token, "cn-406", true, service));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void ValidTicketResolvesTheAccountOnceAndReplayIsRejected()
    {
        var service = new Tickets();
        var marked = LauncherLoginAccount.Prefix + new string('a', 64);
        Assert.Equal("native-user", LauncherLoginAccount.Resolve(marked, "cn-406", true, service));
        Assert.Null(LauncherLoginAccount.Resolve(marked, "cn-406", true, service));
        Assert.Null(LauncherLoginAccount.Resolve(marked, "cn-406", true, null));
    }

    [Fact]
    public void UnmarkedTicketIsRedeemedForItsAccountAndNeverBecomesAnAccountName()
    {
        // A kr-812 login carried the bare 64-digit ticket; it must resolve to the launcher's account, and a spent or
        // unknown one must be refused instead of creating a player named after the ticket.
        var service = new Tickets();
        var bare = new string('a', 64);
        Assert.Equal("native-user", LauncherLoginAccount.Resolve(bare, "cn-406", true, service));
        Assert.Null(LauncherLoginAccount.Resolve(bare, "cn-406", true, service));
        Assert.Null(LauncherLoginAccount.Resolve(new string('b', 64), "kr-812", false, service));
        Assert.Null(LauncherLoginAccount.Resolve(new string('b', 64), "kr-812", true, null));
    }

    [Theory]
    [InlineData("native-user")]
    [InlineData("")]
    [InlineData("guest")]
    public void TicketAuthenticationRefusesPlainAccountNamesWithoutAskingTheService(string account)
    {
        var service = new Tickets();
        Assert.Null(LauncherLoginAccount.Resolve(account, "cn-406", true, service, requireTicket: true));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void TicketAuthenticationStillRedeemsMarkedAndBareTickets()
    {
        Assert.Equal("native-user", LauncherLoginAccount.Resolve(LauncherLoginAccount.Prefix + new string('a', 64), "cn-406", true,
            new Tickets(), requireTicket: true));
        Assert.Equal("native-user", LauncherLoginAccount.Resolve(new string('a', 64), "cn-406", true, new Tickets(), requireTicket: true));
        Assert.Null(LauncherLoginAccount.Resolve(new string('a', 64), "cn-406", true, null, requireTicket: true));
    }

    [Theory]
    [InlineData("native-user", false)]
    [InlineData("", false)]
    [InlineData("bff-ticket:", true)]
    [InlineData("bff-ticket:short", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void TicketModeAdmitsOnlyTicketShapedAccounts(string account, bool ticketForm)
    {
        // Ticket mode refuses everything else before lookup; a ticket-shaped value still has to be redeemed.
        Assert.Equal(ticketForm, LauncherLoginAccount.IsTicketForm(account));
    }

    [Theory]
    [InlineData("ticket", true)]
    [InlineData("open", true)]
    [InlineData("Ticket", false)]
    [InlineData("", false)]
    public void AuthenticationAcceptsOnlyTicketOrOpen(string value, bool valid)
    {
        var options = new LobbyOptions { Authentication = value };
        Assert.Equal(valid, !options.Validate().Any(result => result.MemberNames.Contains(nameof(LobbyOptions.Authentication))));
    }

    private sealed class Tickets : ILauncherTicketService
    {
        internal int Calls { get; private set; }
        public string? Redeem(string ticket, string build)
        {
            Calls++;
            return Calls == 1 && build == "cn-406" && ticket == new string('a', 64) ? "native-user" : null;
        }
    }
}
