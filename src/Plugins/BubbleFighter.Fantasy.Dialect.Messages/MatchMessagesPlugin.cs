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
    Provides = [], ProvidesMany = [],
    ConditionalProvidesMany = [typeof(IInboundMessageBinding<MatchCapabilityAuthenticate>),
        typeof(IInboundMessageBinding<MatchSceneIdentityRegistered>),
        typeof(IInboundMessageBinding<MatchAuthenticate>), typeof(IInboundMessageBinding<MatchSceneRegistered>),
        typeof(IInboundMessageBinding<MatchMapNamed>)])]
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
        RelayConnectionWire.RequireLayouts(tables, ownBuild);
        if (report)
            PacketLayoutContract.RequireFields(tables, ownBuild, "RelayValidationReport", "Native match requests",
                ("entity_id", "u32"), ("nickname", "wstr"));
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != ownBuild)
            throw new ConfigException("Native match requests require an opcode table for the owning relay connection.");
        var binding = new Binding(options.Protocol);
        var packets = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        if (options.NativeHandshake.Length > 0)
        {
            // The kr-1256 family's own handshake: the same relay opcodes, but its join tail, its reconnect map
            // and its scene report (readers below) instead of the legacy validation report.
            BindNativeHandshake();
            return ValueTask.CompletedTask;
        }
        var joinOpcode = opcodes.C2S("rcpReqJoinRelaySession");
        ushort? reportOpcode = report ? opcodes.C2S("rcpReqReportHackCheckData") : null;
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

        // The native handshake of the kr-1256 family. The join is the full RelayJoinRequest the client writes
        // (reader kr-1256 sub_95FD30); "capability" keeps only its key, as the typed-round admission and the
        // legacy builds do, while "account" also carries the account word and nickname the join states. The
        // scene report (relay C2S 12, reader kr-1256 sub_10F32C0) and the reconnect map (relay C2S 3, reader
        // kr-1256 sub_9F02F0) are the same bodies in both modes. The reply frames (S2C 22 settings, S2C 1
        // accepted, S2C 4/5/6 seed tables, S2C 13 scene accepted) belong to dialect.match-replies; they are
        // required here too so a row that turns the handshake on cannot miss its data.
        void BindNativeHandshake()
        {
            RelayHandshakeWire.RequireLayouts(tables, ownBuild);
            // Every opcode resolves before the first registration, so a missing row leaves no handler behind.
            var joinOpcode = opcodes.C2S("rcpReqJoinRelaySession");
            var sceneOpcode = opcodes.C2S("rcpReqReportSceneData_");
            var mapOpcode = opcodes.C2S("rcpReqRelayReconnectMap_");
            if (options.AccountHandshake)
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchAuthenticate>>(binding));
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneRegistered>>(binding));
                Register(joinOpcode, "rcpReqJoinRelaySession",
                    payload => RelayHandshakeWire.ReadAccountJoin(tables, ownBuild, payload.Span));
                Register(sceneOpcode, "rcpReqReportSceneData_",
                    payload => RelayHandshakeWire.ReadScene(tables, ownBuild, payload.Span));
            }
            else
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchCapabilityAuthenticate>>(binding));
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneIdentityRegistered>>(binding));
                Register(joinOpcode, "rcpReqJoinRelaySession",
                    payload => new MatchCapabilityAuthenticate(
                        RelayConnectionWire.ReadJoinTicket(tables, ownBuild, payload.Span)));
                Register(sceneOpcode, "rcpReqReportSceneData_",
                    payload => RelayHandshakeWire.ReadSceneIdentity(tables, ownBuild, payload.Span));
            }
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchMapNamed>>(binding));
            Register(mapOpcode, "rcpReqRelayReconnectMap_",
                payload => RelayHandshakeWire.ReadMap(tables, ownBuild, identity.Major, payload.Span));
        }

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
        IInboundMessageBinding<MatchSceneIdentityRegistered>, IInboundMessageBinding<MatchAuthenticate>,
        IInboundMessageBinding<MatchSceneRegistered>, IInboundMessageBinding<MatchMapNamed>
    {
        public string Protocol => protocol;
    }
}

/// <summary>Selects the native relay scope and audited packet data for an installed request adapter.</summary>
public sealed class MatchMessagesOptions : IValidatableOptions
{
    /// <summary>Gets or sets the owning build's relay protocol, kept separate from its game opcode space.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/relay";

    /// <summary>Gets or sets the directory containing the native join and validation packet tables.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";

    /// <summary>
    /// Gets or sets which shared admission the build's native relay join produces: <c>capability</c> keeps only
    /// the room-issued key (the typed-round admission of the kr-1256 family and the legacy builds' route), and
    /// <c>account</c> also carries the account word and nickname the native join states (the raw kr-1256
    /// path). YAML key <c>native_handshake</c>; unset keeps the row on the legacy validation-report route.
    /// </summary>
    public string NativeHandshake { get; set; } = "";

    /// <summary>Gets whether the native handshake projects the join onto the account-bearing admission.</summary>
    internal bool AccountHandshake =>
        string.Equals(NativeHandshake, "account", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (NativeHandshake.Length == 0)
            yield break;
        if (!AccountHandshake
            && !string.Equals(NativeHandshake, "capability", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult("native_handshake must be either 'capability' or 'account'.",
                [nameof(NativeHandshake)]);
    }
}
