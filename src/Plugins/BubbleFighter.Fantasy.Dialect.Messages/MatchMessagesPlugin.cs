using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Projects native relay joins and complete validation reports into the shared match contracts.
/// Replies, listener framing and room-issued offers are separate services; mounting this row
/// alone neither grants a room lease nor enables the native relay handshake.
/// </summary>
[Plugin("dialect.match", Description = "Native legacy match request adapters.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IInboundMessageBinding<MatchCapabilityAuthenticate>)],
    ConditionalProvidesMany = [typeof(IInboundMessageBinding<MatchSceneIdentityRegistered>)])]
public sealed class MatchMessagesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchMessagesOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.match requires an explicit client_build.");
        var ownBuild = $"{identity.Region}-{identity.Major}";
        if (!string.Equals(options.Protocol, ownBuild + "/relay", StringComparison.Ordinal))
            throw new ConfigException("Native match requests require the owning client build's relay protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        var report = tables.CapabilityOf(ownBuild, "relay_validation_report");
        // The relay frames the wire helpers consume or write: the join key, the offer built from it, and the
        // identity the validation report supplies. A field retyped under the same name must fail here.
        _ = tables.Resolve(ownBuild, "RelaySessionKey");
        PacketLayoutContract.RequireFields(tables, ownBuild, "RelayJoinRequest", "Native match requests",
            ("key", "RelaySessionKey"));
        PacketLayoutContract.RequireFields(tables, ownBuild, "RelayConnectionOffer", "Native match requests",
            ("endpoint", "RelayEndpoint"), ("key", "RelaySessionKey"));
        if (report)
            PacketLayoutContract.RequireFields(tables, ownBuild, "RelayValidationReport", "Native match requests",
                ("entity_id", "u32"), ("nickname", "wstr"));
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != ownBuild)
            throw new ConfigException("Native match requests require an opcode table for the owning relay connection.");
        var joinOpcode = opcodes.C2S("rcpReqJoinRelaySession");
        ushort? reportOpcode = report ? opcodes.C2S("rcpReqReportHackCheckData") : null;
        var binding = new Binding(options.Protocol);
        var packets = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchCapabilityAuthenticate>>(binding));
        if (report)
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneIdentityRegistered>>(binding));
        Register(joinOpcode, "rcpReqJoinRelaySession", payload =>
            new MatchCapabilityAuthenticate(RelayConnectionWire.ReadJoinTicket(tables, ownBuild, payload.Span)));
        if (reportOpcode is { } scene)
            Register(scene, "rcpReqReportHackCheckData", payload =>
            {
                var identity = RelayValidationWire.ReadIdentity(tables, ownBuild, payload.Span);
                return new MatchSceneIdentityRegistered(identity.EntityId, identity.Nickname);
            });
        return ValueTask.CompletedTask;

        void Register<T>(ushort opcode, string name, Func<ReadOnlyMemory<byte>, T> read) where T : IMessage =>
            context.Effect(packets.Register(options.Protocol, opcode, async (session, packet, ct) =>
            {
                if (session.Profile.Id != ownBuild)
                    throw new InvalidOperationException("Native match request belongs to another client profile.");
                var request = read(packet.Payload);
                if (!await messages.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No native match handler is installed for {name} on '{options.Protocol}'.");
            }));
    }

    private sealed class Binding(string protocol) : IInboundMessageBinding<MatchCapabilityAuthenticate>,
        IInboundMessageBinding<MatchSceneIdentityRegistered>
    {
        public string Protocol => protocol;
    }
}

/// <summary>Selects the native relay scope and audited packet data for an installed request adapter.</summary>
public sealed class MatchMessagesOptions
{
    /// <summary>Gets or sets the owning build's relay protocol, kept separate from its game opcode space.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/relay";

    /// <summary>Gets or sets the directory containing the native join and validation packet tables.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
