using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Answers the native latency probe on its originating connection without forwarding it to room peers.</summary>
internal static class RoundPingBinding
{
    internal static void Mount(IPluginContext context, string protocol, string relayProtocol,
        IOpcodeTable game, IOpcodeTable relay, PacketTables packets, string build)
    {
        if (!packets.CapabilityOf(build, "round_ping"))
            return;
        PacketLayoutContract.RequireSignature(packets, build, "RoundPing", "Round latency probe",
            "unknown_4:u32,unknown_8:u32");
        var routes = new[] { (Protocol: protocol, Table: game), (Protocol: relayProtocol, Table: relay) }
            .Select(route => (route.Protocol, Inbound: route.Table.C2S("gppPing"), Outbound: route.Table.S2C("gppPing")))
            .ToArray();
        var router = context.Services.Get<IPacketRouter>();
        foreach (var route in routes)
            context.Effect(router.Register(route.Protocol, route.Inbound, (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _ = PacketCodec.ReadScalars(packets, build, "RoundPing", packet.Payload.Span);
                // kr-1256 sub_6D55D0 case 32840 checks word 0; kr-1256 sub_762570 accepts only its saved clock word.
                // Both words must return unchanged to the requesting client, including during round loading.
                return session.SendAsync(route.Outbound, packet.Payload, ct);
            }));
    }
}
