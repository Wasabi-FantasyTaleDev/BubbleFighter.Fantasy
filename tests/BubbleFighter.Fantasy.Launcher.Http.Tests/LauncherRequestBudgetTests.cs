using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherRequestBudgetTests
{
    [Fact]
    public async Task HttpBudgetUsesThePeerAndIgnoresForwardedAddresses()
    {
        var options = new LauncherOptions { Listen = "127.0.0.1:0", RequestsPerMinute = 1, PasswordIterations = 100000 };
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4),
            new LauncherSessions(options, new MemoryLauncherSessions(4)));
        using var first = Client("127.0.0.1");
        using var second = Client("127.0.0.2");
        using var allowed = await first.GetAsync(server.Addresses.Single() + "/v1/catalog");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        first.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.99");
        using var blocked = await first.GetAsync(server.Addresses.Single() + "/v1/catalog");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        using var independent = await second.GetAsync(server.Addresses.Single() + "/v1/catalog");
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
    }

    private static HttpClient Client(string source) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    });

    [Fact]
    public void ExhaustingOneAddressDoesNotThrottleAnother()
    {
        var budget = new LauncherRequestBudget(1, 4);
        Assert.True(budget.TryTake(IPAddress.Parse("192.0.2.1")));
        Assert.False(budget.TryTake(IPAddress.Parse("192.0.2.1")));
        Assert.True(budget.TryTake(IPAddress.Parse("192.0.2.2")));
        Assert.False(budget.TryTake(IPAddress.Parse("::ffff:192.0.2.1")));
        Assert.False(budget.TryTake(null));
    }

    [Fact]
    public void FullTableEvictsTheOldestWindowAndWindowsExpire()
    {
        var clock = new Clock();
        var budget = new LauncherRequestBudget(1, 2, clock);
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");
        Assert.True(budget.TryTake(first));
        clock.Advance(1);
        Assert.True(budget.TryTake(second));
        clock.Advance(1);
        Assert.True(budget.TryTake(IPAddress.Parse("192.0.2.3")));
        Assert.False(budget.TryTake(second));
        Assert.True(budget.TryTake(first));
        clock.Advance(60);
        Assert.True(budget.TryTake(first));
        Assert.False(budget.TryTake(first));
    }

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        internal void Advance(long seconds) => _seconds += seconds;
    }
}
