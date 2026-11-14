using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Maps native relay delay probes and opaque acknowledgements through the audited packet tables.</summary>
[Plugin("dialect.relay-latency", Category = "network", Names = ["en=Relay latency replies", "zh-Hans=中继延迟回应", "zh-Hant=中繼延遲回應", "ko=릴레이 지연 응답"], Description = "Native relay delay probe adapter.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IInboundMessageBinding<RelayLatencyRequested>), typeof(IMessageSender<RelayLatencyAcknowledged>)])]
public sealed class RelayLatencyPlugin : IPlugin
{
    /// <summary>Installs audited relay request decoding and reply encoding as reversible effects for the owning build.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<RelayLatencyDialectOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.relay-latency requires an explicit client_build.");
        var build = $"{identity.Region}-{identity.Major}";
        if (options.Protocol != build + "/relay")
            throw new ConfigException("Relay latency requires the owning client build's relay protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        if (!tables.ValuesOf(build).TryGetValue("relay_latency_builds", out var raw)
            || raw is not string[] audited || audited.Length == 0
            || audited.Distinct(StringComparer.Ordinal).Count() != audited.Length
            || audited.Any(item => !tables.Builds.Contains(item, StringComparer.Ordinal))
            || !audited.Contains(build, StringComparer.Ordinal))
            throw new ConfigException($"relay_latency_builds must explicitly include the audited build '{build}'.");
        RelayLatencyWire.RequireLayouts(tables, build);
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Relay latency requires the owning relay opcode table.");
        var requestOpcode = opcodes.C2S("rcpReqLagDisPing");
        var replyOpcode = opcodes.S2C("rspRepLagDisPing");
        var sender = new Sender(options.Protocol, build, tables, replyOpcode);
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RelayLatencyRequested>>(sender));
        context.Effect(context.Services.RegisterMany<IMessageSender<RelayLatencyAcknowledged>>(sender));
        context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, requestOpcode,
            async (session, packet, ct) =>
            {
                if (session.Profile.Id != build)
                    throw new InvalidOperationException("Relay latency request belongs to another client profile.");
                var request = RelayLatencyWire.ReadRequest(tables, build, packet.Payload.Span);
                if (!await messages.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No relay latency handler is installed for '{options.Protocol}'.");
            }));
        return ValueTask.CompletedTask;
    }

    private sealed class Sender(string protocol, string build, PacketTables tables, ushort opcode)
        : IInboundMessageBinding<RelayLatencyRequested>, IMessageSender<RelayLatencyAcknowledged>
    {
        /// <summary>Gets the exact relay scope shared by the request binding and reply encoder.</summary>
        public string Protocol => protocol;

        /// <summary>Sends the opaque acknowledgement only to a session of the audited client build.</summary>
        public ValueTask SendAsync(ISession session, RelayLatencyAcknowledged message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (session.Profile.Id != build)
                throw new InvalidOperationException("Relay latency reply belongs to another client profile.");
            return session.SendAsync(opcode, RelayLatencyWire.WriteReply(tables, build, message), ct);
        }
    }
}

/// <summary>Selects the native relay protocol and packet-data directory for the delay adapter.</summary>
public sealed class RelayLatencyDialectOptions
{
    /// <summary>Gets or sets the owning build's relay protocol, separate from its game and UDP scopes.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the directory containing the request and reply scalar layouts.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
