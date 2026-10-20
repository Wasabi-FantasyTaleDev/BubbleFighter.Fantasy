using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>Shared mounting helpers for the modern typed room and plaza bindings.</summary>
internal static class ModernRoutes
{
    /// <summary>Resolves every structure and S2C name before anything is registered, so a gap fails at activation.</summary>
    internal static IReadOnlyDictionary<string, ushort> Preflight(IOpcodeTable table, PacketTables packets, string build,
        IEnumerable<string> structures, IEnumerable<string> serverNames)
    {
        foreach (var structure in structures)
            _ = packets.Resolve(build, structure);
        var opcodes = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var name in serverNames)
            opcodes[name] = table.Find(PacketDirection.ServerToClient, name)
                ?? throw new ConfigException($"The modern typed binding requires S2C {name}.");
        return opcodes;
    }

    /// <summary>
    /// Routes one C2S name to a typed message. A request whose structure is readable but whose variant has
    /// no audited operation (<see cref="NotSupportedException"/>) is logged and dropped; malformed bodies still fail.
    /// </summary>
    internal static void Bind<T>(IPluginContext context, string protocol, IOpcodeTable table, ILogger logger, string name,
        Func<ReadOnlySpan<byte>, T> decode) where T : class, IMessage
    {
        if (table.Find(PacketDirection.ClientToServer, name) is not { } opcode)
            throw new ConfigException($"The modern typed binding requires C2S {name}.");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
        {
            T message;
            try
            {
                message = decode(packet.Payload.Span);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning("Session {Id} C2S {Name} ignored: {Reason}", session.Id, name, ex.Message);
                return;
            }
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"No typed handler is mounted for {name}.");
        }));
    }

    /// <summary>
    /// Routes one C2S name whose layout and effect are not audited to a log line carrying only its length, so the
    /// report is accounted for without being parsed or answered.
    /// </summary>
    internal static void LogOnly(IPluginContext context, string protocol, IOpcodeTable table, ILogger logger, string name)
    {
        if (table.Find(PacketDirection.ClientToServer, name) is not { } opcode)
            throw new ConfigException($"The modern typed binding requires C2S {name}.");
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, opcode, (session, packet, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            logger.LogDebug("Session {Id} C2S {Name}, {Length} bytes, recorded without a typed operation.", session.Id, name, packet.Payload.Length);
            return ValueTask.CompletedTask;
        }));
    }

    /// <summary>Requires an empty request body, as the client's builders for these scene requests write no fields.</summary>
    internal static void RequireEmpty(ReadOnlySpan<byte> body, string name)
    {
        if (!body.IsEmpty)
            throw new PacketFormatException($"{name} must be empty.", body.Length);
    }
}
