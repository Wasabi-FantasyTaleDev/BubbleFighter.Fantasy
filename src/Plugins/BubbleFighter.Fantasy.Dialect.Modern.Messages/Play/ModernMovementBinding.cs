using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>
/// The modern typed round's movement: the full and the partial movement reports become one typed report, and the
/// merged record goes out on the other players' relay connections. Both reports keep kr-812's class names: kr-1256
/// sub_95FAE0 writes the full record and the OptimizeWanderInfo delta as kr-812 sub_80DBB0 writes gppWanderLean and
/// gppWanderLeanOptimize, in wider fields (four-byte floats for kr-812's two-byte coordinates).
/// </summary>
internal sealed class ModernMovementBinding(string protocol, IOpcodeTable game, IOpcodeTable relay, ILogger logger)
    : IInboundMessageBinding<RoundMovementReported>, IMessageSender<RoundMovementRelayed>
{
    private ushort _relayed;

    public string Protocol => protocol;

    internal void Mount(IPluginContext context)
    {
        // The record goes out on the relay connection (kr-1256 sub_95E920 writes the same 21 bytes it reads back).
        _relayed = relay.Find(PacketDirection.ServerToClient, "gppWanderLean")
            ?? throw new ConfigException("The modern typed movement requires the relay S2C gppWanderLean.");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundMovementReported>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundMovementRelayed>>(this));
        ModernRoutes.Bind(context, protocol, game, logger, "gppWanderLean", body =>
        {
            var report = MovementReport.Parse(body);
            return new RoundMovementReported(report.ObjectId, PlayerMovementState.FullMask, report.X, report.Y, report.Z,
                report.Yaw, report.Pitch, report.State, report.Tick);
        });
        ModernRoutes.Bind(context, protocol, game, logger, "gppWanderLeanOptimize", body =>
        {
            var sample = MovementSample.Parse(body);
            return new RoundMovementReported(sample.Actor, sample.Mask, sample.X, sample.Y, sample.Z,
                sample.Yaw, sample.Pitch, sample.State, sample.Tick);
        });
    }

    /// <summary>Sends one merged movement record (kr-1256 sub_709700 reads it: four words, three bytes, the tick).</summary>
    public ValueTask SendAsync(ISession session, RoundMovementRelayed message, CancellationToken ct)
    {
        var movement = message.Movement;
        return session.SendAsync(_relayed, GameToClient.MovementStateFrame(message.ActorId, movement.X, movement.Y, movement.Z,
            movement.Yaw, movement.Pitch, movement.State, message.Tick), ct);
    }
}
