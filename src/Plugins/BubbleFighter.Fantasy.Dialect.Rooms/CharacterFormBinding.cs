using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Translates Rhea's actor and bool32 transformation exchange through the build's packet data.</summary>
internal sealed class CharacterFormBinding(string protocol, IOpcodeTable table, PacketTables packets, string build)
    : IInboundMessageBinding<RoundCharacterFormRequested>, IMessageSender<RoundCharacterFormChanged>
{
    public string Protocol => protocol;

    /// <summary>Mounts the audited game request and the matching form sender as reversible row effects.</summary>
    internal void Mount(IPluginContext context)
    {
        if (!packets.CapabilityOf(build, "rhea_character_skill"))
            throw new ConfigException($"rhea_character_skill requires the build's audited native capability: {build}.");
        PacketLayoutContract.RequireSignature(packets, build, "CharacterFormRequest", "Rhea character skill", "actor:u32,active:bool");
        PacketLayoutContract.RequireSignature(packets, build, "CharacterFormReply", "Rhea character skill", "actor:u32,active:bool");
        var request = table.C2S("gcpReqRheaForm_");
        _ = table.S2C("gspRheaForm_");
        var services = context.Services;
        var messages = services.Get<IMessageRouter>();
        context.Effect(services.Get<IPacketRouter>().Register(protocol, request, async (session, packet, ct) =>
        {
            var message = Read(packets, build, packet.Payload.Span);
            if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
                throw new InvalidOperationException("No Rhea character skill handler is installed.");
        }));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundCharacterFormRequested>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundCharacterFormChanged>>(this));
    }

    public ValueTask SendAsync(ISession session, RoundCharacterFormChanged message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspRheaForm_"), Write(packets, build, message), ct);

    /// <summary>Reads the scene actor and canonical native flag, requiring exactly eight bytes.</summary>
    internal static RoundCharacterFormRequested Read(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "CharacterFormRequest", payload, requireCanonicalBooleans: true);
        return new((int)values["actor"], (bool)values["active"]);
    }

    /// <summary>Writes the authenticated actor and accepted form consumed by every receiving character.</summary>
    internal static byte[] Write(PacketTables packets, string build, RoundCharacterFormChanged message) =>
        PacketCodec.Write(packets, build, "CharacterFormReply", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor,
            ["active"] = message.Active,
        });
}
