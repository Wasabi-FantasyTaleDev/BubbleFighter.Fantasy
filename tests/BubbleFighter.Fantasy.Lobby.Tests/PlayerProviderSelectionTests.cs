using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Lobby.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class PlayerProviderSelectionTests
{
    [Fact]
    public async Task SuppliedIdentityRepositoryWinsAndPreservesItsExistingIdentity()
    {
        var supplied = new InMemoryPlayerRepository();
        var player = await supplied.CreateAsync("known", "saved-nickname", default);
        var registry = DispatchProxy.Create<IServiceRegistry, RegistryProxy>();
        ((RegistryProxy)registry).PlayerRepository = supplied;
        var resolved = LobbyPlugin.ResolvePlayerRepository(registry);
        Assert.Same(supplied, resolved);
        Assert.Equal(player, await resolved.FindByUsernameAsync("known", default));
    }

    [Fact]
    public void NoProviderRetainsTheExistingExplicitMemoryIdentityPath()
    {
        var registry = DispatchProxy.Create<IServiceRegistry, RegistryProxy>();
        Assert.IsType<InMemoryPlayerRepository>(LobbyPlugin.ResolvePlayerRepository(registry));
    }

    public class RegistryProxy : DispatchProxy
    {
        public IPlayerRepository? PlayerRepository { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "TryGet" || args is null) throw new NotSupportedException();
            var type = targetMethod.GetGenericArguments().Single();
            if (type == typeof(IPlayerRepository))
            {
                args[0] = PlayerRepository;
                return PlayerRepository is not null;
            }
            args[0] = null;
            return false;
        }
    }
}
