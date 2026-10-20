using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.CustomizedKeys;

/// <summary>Binds defaults/apply flags and counted UTF-16 control-name/key rows through the audited packet tables.</summary>
/// <remarks>Builders: kr-812 sub_4EB250, kr-1256 sub_60B150, cn-774 sub_761B90, tw-454 sub_6F5B50.
/// Consumers: kr-812 sub_8357D0, kr-1256 sub_9C9BF0, cn-774 sub_BC5A20, tw-454 sub_AA49B0.</remarks>
[Plugin("dialect.customized-keys", Description = "Table-driven customized-key updates and restoration.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry), typeof(IPlayerSettingsRepository)],
    OptionalMany = [typeof(IOpcodeTable)],
    ProvidesMany = [typeof(IInboundMessageBinding<CustomizedKeysUpdated>), typeof(IMessageSender<CustomizedKeysRestored>)])]
public sealed class CustomizedKeysDialectPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<CustomizedKeysDialectOptions>();
        if (string.IsNullOrWhiteSpace(options.Protocol)) throw new ConfigException("dialect.customized-keys requires a protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        var build = tables.NearestBuild(context.Row.LineageOrBuild())
            ?? throw new ConfigException("dialect.customized-keys requires a build named in the packet tables.");
        var limits = context.Services.Get<IPlayerSettingsRepository>().Limits;
        limits.Validate();
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        var inbound = opcodes.C2S("gcpUserUpdateCustomizedKeyList");
        var outbound = opcodes.S2C("gspRepCustomizedKeyList");
        // The update flag/list it reads and the reply flag/list it writes; each field is cast or sized by name.
        PacketLayoutContract.RequireFields(tables, build, "CustomizedKeysUpdate", "Customized keys",
            ("use_defaults", "bool"), ("mappings", "list<CustomizedKey>"));
        PacketLayoutContract.RequireFields(tables, build, "CustomizedKeysReply", "Customized keys",
            ("apply_mappings", "bool"), ("mappings", "list<CustomizedKey>"));
        var binding = new Binding(options.Protocol, tables, build, limits, outbound);
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CustomizedKeysUpdated>>(binding));
        context.Effect(context.Services.RegisterMany<IMessageSender<CustomizedKeysRestored>>(binding));
        var messages = context.Services.Get<IMessageRouter>();
        var readLimits = new PacketReadLimits(limits.MaxNameCharacters, limits.MaxMappings, 2 + 3 * limits.MaxMappings, 3);
        context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, inbound, async (session, packet, ct) =>
        {
            var fields = PacketCodec.Read(tables, build, "CustomizedKeysUpdate", packet.Payload.Span, readLimits,
                requireCanonicalBooleans: true);
            // Reject UTF-16 replacement during decoding rather than persisting a different control name.
            if (!packet.Payload.Span.SequenceEqual(PacketCodec.Write(tables, build, "CustomizedKeysUpdate", fields)))
                throw new PacketFormatException("Customized-key names must preserve their original UTF-16 encoding.", 0);
            var rows = (object?[])fields["mappings"]!;
            var mappings = new CustomizedKey[rows.Length];
            for (var i = 0; i < mappings.Length; i++)
            {
                var row = (Dictionary<string, object?>)rows[i]!;
                mappings[i] = new((string)row["name"]!, (int)row["key"]!);
            }
            var update = new CustomizedKeysUpdated((bool)fields["use_defaults"]!, limits.Copy(mappings));
            if (update.UseDefaults != (update.Mappings.Count == 0))
                throw new PacketFormatException("Customized-key defaults flag disagrees with its override list.", 0);
            if (!await messages.DispatchAsync(options.Protocol, session, update, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Customized-key updates have no typed handler.");
        }));
        return ValueTask.CompletedTask;
    }

    private sealed class Binding(string protocol, PacketTables tables, string build, CustomizedKeyLimits limits, ushort opcode)
        : IInboundMessageBinding<CustomizedKeysUpdated>, IMessageSender<CustomizedKeysRestored>
    {
        public string Protocol => protocol;

        public ValueTask SendAsync(ISession session, CustomizedKeysRestored message, CancellationToken ct)
        {
            var mappings = limits.Copy(message.Mappings);
            if (message.ApplyMappings != (mappings.Length != 0))
                throw new InvalidDataException("Customized-key restoration must apply exactly its nonempty saved map.");
            var rows = mappings.Select(row => new Dictionary<string, object?> { ["name"] = row.Name, ["key"] = row.Key }).ToArray();
            return session.SendAsync(opcode, PacketCodec.Write(tables, build, "CustomizedKeysReply",
                new Dictionary<string, object?> { ["apply_mappings"] = message.ApplyMappings, ["mappings"] = rows }), ct);
        }
    }
}

/// <summary>Selects the protocol and audited layout directory for the customized-key adapter.</summary>
public sealed class CustomizedKeysDialectOptions
{
    /// <summary>Gets or sets the game protocol whose opcode table supplies both customized-key names.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the directory containing builds.yaml and the customized-key structures.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
