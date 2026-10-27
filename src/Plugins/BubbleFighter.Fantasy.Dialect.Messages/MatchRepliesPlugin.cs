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
        // The join signal is empty on the legacy relay builds; kr-1256 states its one ignored word.
        if (tables.Resolve(build, "RelayJoinAcceptedSignal").Count == 0)
            PacketLayoutContract.RequireEmpty(tables, build, "RelayJoinAcceptedSignal", "Native relay success");
        else
            PacketLayoutContract.RequireSignature(tables, build, "RelayJoinAcceptedSignal", "Native relay success",
                "unknown_n0:u32");
        // The scene success signal has an empty body in every audited build, including kr-1256.
        PacketLayoutContract.RequireEmpty(tables, build, "RelaySceneAcceptedSignal", "Native relay success");
        // The kr-1256 reply sequence: S2C 22 settings arm the handshake record (kr-1256 sub_9601E0 case 22,
        // block 0x96113C) before the accepted signal starts the C2S 12 exchange, and a scene accept first
        // initializes the entity's seed tables (S2C 4/5/6, readers kr-1256 sub_6CD590/sub_6CD810/sub_6CDA20,
        // consumers kr-1256 sub_4832D0/sub_483420/sub_483540) before the empty S2C 13. The same sequence appears
        // in cn-774 sub_B593D0 and tw-454 sub_A79210; legacy builds without settings keep their bare signal pair.
        var nativeSequence = PacketLayoutContract.Signature(tables, build, "RelayHandshakeSettings").Length > 0;
        if (nativeSequence)
        {
            PacketLayoutContract.RequireSignature(tables, build, "RelayHandshakeSettings", "Native relay success",
                "unknown_n0:bool,unknown_n1:u8,unknown_n2:u8,unknown_n3:u8,unknown_n4:u8,unknown_n5:u8,unknown_n6:u8");
            // The reply row can be installed independently; its wire contract cannot rely on the request row.
            RelayHandshakeWire.RequireSeedLayouts(tables, build);
        }
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        if (opcodes.Connection != OpcodeConnection.Relay || opcodes.Profile.Id != build)
            throw new ConfigException("Native match replies require an opcode table for the owning relay connection.");
        var acceptedOpcode = opcodes.S2C("rspRelayJoinAccepted_");
        ushort? settingsOpcode = nativeSequence ? opcodes.S2C("rspRelayHandshakeSettings_") : null;
        ushort? sceneOpcode = report || nativeSequence ? opcodes.S2C("rspRelaySceneAccepted_") : null;
        var accepted = PacketCodec.Write(tables, build, "RelayJoinAcceptedSignal", new Dictionary<string, object?>());
        var settings = nativeSequence
            ? PacketCodec.Write(tables, build, "RelayHandshakeSettings", new Dictionary<string, object?>())
            : null;
        var sender = new Sender(options.Protocol, build, tables, acceptedOpcode, accepted, settingsOpcode, settings,
            sceneOpcode, nativeSequence ? opcodes.S2C("rspSendCrtSeedTable") : null,
            nativeSequence ? opcodes.S2C("rspSendDefSeedTable") : null,
            nativeSequence ? opcodes.S2C("rspSendCrtCancelSeedTable") : null);
        context.Effect(context.Services.RegisterMany<IMessageSender<MatchAuthenticationAccepted>>(sender));
        if (sceneOpcode is not null) context.Effect(context.Services.RegisterMany<IMessageSender<MatchSceneAccepted>>(sender));
        return ValueTask.CompletedTask;
    }

    private sealed class Sender(string protocol, string build, PacketTables tables, ushort joinOpcode, byte[] join,
        ushort? settingsOpcode, byte[]? settings, ushort? sceneOpcode, ushort? criticalOpcode,
        ushort? defenceOpcode, ushort? cancelOpcode)
        : IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        // The counted seed vectors the entity tables carry: two word vectors of 32 and one of 32 words plus
        // 1000 bytes. The sizes are the client's own per-entity state, not tunables: kr-1256 sub_4832D0 and
        // sub_483420 reset two 32-word bitsets and kr-1256 sub_483540 a 1000-byte state.
        private static readonly object?[] SeedWords = Enumerable.Repeat<object?>(0u, 32).ToArray();
        private static readonly object?[] SeedBytes = Enumerable.Repeat<object?>(0u, 1000).ToArray();

        public string Protocol => protocol;

        public async ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct)
        {
            // The settings frame precedes the accepted signal; a dialect without it sends the signal alone.
            if (settingsOpcode is { } opcode)
                await SendAsync(session, opcode, settings!, ct).ConfigureAwait(false);
            await SendAsync(session, joinOpcode, join, ct).ConfigureAwait(false);
        }

        public async ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct)
        {
            if (sceneOpcode is null)
                throw new InvalidOperationException("This native dialect has no scene success signal.");
            if (criticalOpcode is { } critical)
            {
                await SendAsync(session, critical, Seeds("RelayCriticalSeedTable", message.EntityId,
                    [("unknown_8", SeedWords), ("unknown_24", SeedWords)]), ct).ConfigureAwait(false);
                await SendAsync(session, defenceOpcode ?? throw new InvalidOperationException(
                    "A critical seed table without the defence table is not a stated native sequence."),
                    Seeds("RelayDefenceSeedTable", message.EntityId, [("unknown_8", SeedBytes)]), ct).ConfigureAwait(false);
                await SendAsync(session, cancelOpcode ?? throw new InvalidOperationException(
                    "A critical seed table without its cancel table is not a stated native sequence."),
                    Seeds("RelayCriticalSeedCancelTable", message.EntityId, [("unknown_8", SeedWords)]), ct).ConfigureAwait(false);
            }
            await SendAsync(session, sceneOpcode.Value,
                PacketCodec.Write(tables, build, "RelaySceneAcceptedSignal", new Dictionary<string, object?>()), ct).ConfigureAwait(false);
        }

        private byte[] Seeds(string structure, int entity, params (string Name, object?[] Values)[] vectors)
        {
            var values = new Dictionary<string, object?> { ["entity_id"] = (uint)entity };
            foreach (var (name, vector) in vectors)
                values[name] = vector;
            return PacketCodec.Write(tables, build, structure, values);
        }

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
    /// <summary>Gets or sets the owning native relay protocol, independent of its game connection.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/relay";

    /// <summary>Gets or sets the directory containing the native relay success-signal tables.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
