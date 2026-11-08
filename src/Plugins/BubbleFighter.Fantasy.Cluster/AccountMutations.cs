using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Runs decoded game work under the originating connection's mutation authority without changing protocol handlers.</summary>
[Plugin("cluster.account-mutations", Description = "Session-bound mutation authority for packet dispatch.",
    Requires = [typeof(IAccountMutationScopes), typeof(IAccountSessionAdmission)], Decorates = [typeof(IPacketRouter)])]
public sealed class AccountMutationsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var scopes = context.Services.Get<IAccountMutationScopes>();
        context.Effect(context.Services.Decorate<IPacketRouter>(inner => new SessionRouter(inner, scopes)));
        return ValueTask.CompletedTask;
    }

    private sealed class SessionRouter(IPacketRouter inner, IAccountMutationScopes scopes) : IPacketRouter
    {
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler) => inner.Register(protocolName, opcode, handler);

        public async ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct)
        {
            using var scope = scopes.Enter(session);
            await inner.DispatchAsync(protocolName, session, packet, ct).ConfigureAwait(false);
        }
    }
}
