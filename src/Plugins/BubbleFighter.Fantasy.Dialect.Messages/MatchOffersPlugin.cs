using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Mounts audited native game-side relay offers and complete connection reports, without opening a listener.</summary>
[Plugin("dialect.match-offers", Description = "Native legacy room endpoint offers and connection reports.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IMatchCapabilityOfferSender), typeof(IInboundMessageBinding<MatchConnectionReported>)])]
public sealed class MatchOffersPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchOffersOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.match-offers requires an explicit client_build.");
        var build = $"{identity.Region}-{identity.Major}";
        if (options.Protocol != build + "/game")
            throw new ConfigException("Native match offers require the owning client build's game protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        // The offer's endpoint and key, the report the client sends back, and the whole opaque session key whose
        // encoded length is the ticket length this row advertises. A field retyped under the same name fails
        // here, at activation, instead of on the first packet.
        RelayConnectionWire.RequireLayouts(tables, build);
        PacketLayoutContract.RequireSignature(tables, build, "RelayConnectionReport", "Native match offers",
            "unknown_4:bool", "unknown_4:bool,unknown_5:bool");
        var ticketLength = PacketCodec.Write(tables, build, "RelaySessionKey", new Dictionary<string, object?>()).Length;
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Game || opcodes.Profile.Id != build)
            throw new ConfigException("Native match offers require an opcode table for the owning game connection.");
        var offerOpcode = opcodes.S2C("gspConnectToRelayServer");
        var reportOpcode = opcodes.C2S("gcpReportRelayConnected");
        var sender = new Sender(options.Protocol, build, tables, offerOpcode, ticketLength);
        context.Effect(context.Services.RegisterMany<IMatchCapabilityOfferSender>(sender));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchConnectionReported>>(new Binding(options.Protocol)));
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, reportOpcode, async (session, packet, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            RequireProfile(session, build);
            var fields = PacketCodec.ReadScalars(tables, build, "RelayConnectionReport", packet.Payload.Span);
            // id-400 sub_794200 at 0x794551 stores +4=true and +5=false; id-400 sub_793CC0 reads both bool32s.
            if (fields.TryGetValue("unknown_5", out var extra))
                context.Logger.LogDebug("Native game-side relay report contains unknown_5={Flag}.", extra);
            var report = new MatchConnectionReported((bool)fields["unknown_4"]!);
            if (!await messages.DispatchAsync(options.Protocol, session, report, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No native game-side match connection handler is mounted.");
        }));
        return ValueTask.CompletedTask;
    }

    private static void RequireProfile(ISession session, string build)
    {
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Native game-side match message belongs to another client profile.");
    }

    private sealed class Binding(string protocol) : IInboundMessageBinding<MatchConnectionReported>
    {
        public string Protocol => protocol;
    }

    private sealed class Sender(string protocol, string build, PacketTables tables, ushort opcode, int ticketLength)
        : IMatchCapabilityOfferSender
    {
        public string Protocol => protocol;
        public int TicketLength => ticketLength;
        public ValueTask SendAsync(ISession session, MatchCapabilityOffered message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequireProfile(session, build);
            var body = RelayConnectionWire.WriteOffer(tables, build, message.Address, message.Port, message.Ticket.Span);
            return session.SendAsync(opcode, body, ct);
        }
    }
}

/// <summary>Selects the owning game opcode space and native endpoint, key and report tables.</summary>
public sealed class MatchOffersOptions
{
    /// <summary>Gets or sets the owning build's game protocol, separate from its relay protocol.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/game";

    /// <summary>Gets or sets the directory containing the audited native relay connection structures.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
