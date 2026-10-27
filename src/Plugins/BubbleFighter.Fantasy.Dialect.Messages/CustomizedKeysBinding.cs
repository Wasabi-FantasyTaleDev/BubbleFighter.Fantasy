using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Adapts the customized-key update <c>gcpUserUpdateCustomizedKeyList</c> and the restoration reply
/// <c>gspRepCustomizedKeyList</c>, both through the packet tables: a defaults/apply flag followed by counted
/// UTF-16 control-name and key rows. Builders: kr-812 sub_4EB250, kr-1256 sub_60B150, cn-774 sub_761B90,
/// tw-454 sub_6F5B50; consumers: kr-812 sub_8357D0, kr-1256 sub_9C9BF0, cn-774 sub_BC5A20, tw-454 sub_AA49B0.
/// </summary>
/// <remarks>
/// The repository arrives from the persistence row, not from <c>player.settings</c>, so the row resolves it
/// lazily with <c>TryGet</c> and a profile without a persistence provider that states the option fails at mount.
/// </remarks>
internal sealed class CustomizedKeysBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build,
    CustomizedKeyLimits limits)
    : IInboundMessageBinding<CustomizedKeysUpdated>, IMessageSender<CustomizedKeysRestored>
{
    private ushort _outbound;

    public string Protocol => protocol;

    /// <summary>
    /// Registers the update route and the restoration sender on the row's scope. A table that names neither
    /// packet fails here, so a build without the audited pair keeps no half-mounted route.
    /// </summary>
    /// <param name="context">The owning row's context.</param>
    /// <param name="messages">The router the typed update dispatches through.</param>
    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        limits.Validate();
        // The update flag/list it reads and the reply flag/list it writes; each field is cast or sized by name.
        PacketLayoutContract.RequireFields(tables, build, "CustomizedKeysUpdate", "Customized keys",
            ("use_defaults", "bool"), ("mappings", "list<CustomizedKey>"));
        PacketLayoutContract.RequireFields(tables, build, "CustomizedKeysReply", "Customized keys",
            ("apply_mappings", "bool"), ("mappings", "list<CustomizedKey>"));
        var inbound = opcodes.C2S("gcpUserUpdateCustomizedKeyList");
        _outbound = opcodes.S2C("gspRepCustomizedKeyList");
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<CustomizedKeysUpdated>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<CustomizedKeysRestored>>(this));
        var readLimits = new PacketReadLimits(limits.MaxNameCharacters, limits.MaxMappings, 2 + 3 * limits.MaxMappings, 3);
        context.Effect(context.Services.Get<IPacketRouter>().Register(protocol, inbound, async (session, packet, ct) =>
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
            if (!await messages.DispatchAsync(protocol, session, update, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Customized-key updates have no typed handler.");
        }));
    }

    /// <summary>Writes the restoration reply through the tables, applying the saved map exactly as stored.</summary>
    public ValueTask SendAsync(ISession session, CustomizedKeysRestored message, CancellationToken ct)
    {
        var mappings = limits.Copy(message.Mappings);
        if (message.ApplyMappings != (mappings.Length != 0))
            throw new InvalidDataException("Customized-key restoration must apply exactly its nonempty saved map.");
        var rows = mappings.Select(row => new Dictionary<string, object?> { ["name"] = row.Name, ["key"] = row.Key }).ToArray();
        return session.SendAsync(_outbound, PacketCodec.Write(tables, build, "CustomizedKeysReply",
            new Dictionary<string, object?> { ["apply_mappings"] = message.ApplyMappings, ["mappings"] = rows }), ct);
    }
}
