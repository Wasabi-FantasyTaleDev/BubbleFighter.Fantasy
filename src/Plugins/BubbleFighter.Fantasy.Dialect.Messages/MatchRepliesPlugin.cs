using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Publishes native relay success signals after the shared lifecycle has validated a room lease.</summary>
[Plugin("dialect.match-replies", Description = "Native legacy match success signals.",
    Requires = [typeof(IOpcodeTableRegistry)], OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IMessageSender<MatchAuthenticationAccepted>)],
    ConditionalProvidesMany = [typeof(IMessageSender<MatchSceneAccepted>)])]
public sealed class MatchRepliesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MatchRepliesOptions>();
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("dialect.match-replies requires an explicit client_build.");
        var build = $"{identity.Region}-{identity.Major}";
        if (options.Protocol != build + "/relay")
            throw new ConfigException("Native match replies require the owning client build's relay protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        var report = tables.CapabilityOf(build, "relay_validation_report");
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Native match replies require an opcode table for the owning relay connection.");
        var acceptedOpcode = opcodes.S2C("rspRelayJoinAccepted_");
        ushort? sceneOpcode = report ? opcodes.S2C("rspRelaySceneAccepted_") : null;
        var accepted = PacketCodec.Write(tables, build, "RelayJoinAcceptedSignal", new Dictionary<string, object?>());
        var scene = report ? PacketCodec.Write(tables, build, "RelaySceneAcceptedSignal", new Dictionary<string, object?>()) : null;
        var sender = new Sender(options.Protocol, build, acceptedOpcode, accepted, sceneOpcode, scene);
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchAuthenticationAccepted>>(sender));
        if (report) context.Effect(context.Services.RegisterMany<IMessageSender<MatchSceneAccepted>>(sender));
        return ValueTask.CompletedTask;
    }

    private sealed class Sender(string protocol, string build, ushort joinOpcode, byte[] join,
        ushort? sceneOpcode, byte[]? scene) : IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        public string Protocol => protocol;
        public ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct) =>
            SendAsync(session, joinOpcode, join, ct);

        public ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct) =>
            SendAsync(session, sceneOpcode ?? throw new InvalidOperationException("This native dialect has no scene success signal."),
                scene ?? throw new InvalidOperationException("This native dialect has no scene success body."), ct);

        private ValueTask SendAsync(ISession session, ushort opcode, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (session.Profile.Id != build)
                throw new InvalidOperationException("Native match reply belongs to another client profile.");
            return session.SendAsync(opcode, body, ct);
        }
    }
}

/// <summary>Selects the owning native relay opcode space and audited success-signal layouts.</summary>
public sealed class MatchRepliesOptions
{
    /// <summary>Gets or sets the owning build's relay protocol, independent of its game connection.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/relay";

    /// <summary>Gets or sets the directory containing the native relay success-signal tables.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
