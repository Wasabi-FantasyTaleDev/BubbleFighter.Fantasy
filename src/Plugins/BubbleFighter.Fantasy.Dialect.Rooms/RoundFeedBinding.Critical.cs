using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

internal sealed partial class RoundFeedBinding
{
    private void MountCriticalTransitions(IPluginContext context, IPacketRouter router, IMessageRouter messages)
    {
        if (packets.ValuesOf(build).GetValueOrDefault("weapon_critical_seed_builds") is not string[] members
            || !members.Contains(build, StringComparer.Ordinal)) return;
        PacketLayoutContract.RequireFields(packets, build, "CriticalWeaponState", "Critical weapon transitions",
            ("specification", "u32"), ("slot", "u32"), ("actor", "u32"), ("unknown_16", "u32"));
        PacketLayoutContract.RequireFields(packets, build, "CriticalEffectTrigger", "Critical timed transitions",
            ("unknown_4", "u8"), ("actor", "u32"), ("initiator", "u32"),
            ("position_x", "f32"), ("position_y", "f32"), ("position_z", "f32"), ("unknown_28", "u8"));
        Register("gppUpdateWeaponInfo", weapon: true);
        Register("gppKicked", weapon: false);

        void Register(string name, bool weapon)
        {
            var outbound = relay.S2C(name);
            Mount(protocol, game.C2S(name), false);
            if (AcceptRelayFrames) Mount(relayProtocol, relay.C2S(name), true);

            void Mount(string inputProtocol, ushort opcode, bool onRelay) => context.Effect(
                router.Register(inputProtocol, opcode, async (session, packet, ct) =>
                {
                    var feed = ReadCriticalTransition(packets, build, outbound, packet.Payload.Span, weapon)
                        with { OnRelay = onRelay };
                    if (!await messages.DispatchAsync(protocol, session, feed, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("Critical transition has no typed round feed handler.");
                }));
        }
    }

    internal static RoundFeedSent ReadCriticalTransition(PacketTables tables, string build, ushort outbound,
        ReadOnlySpan<byte> body, bool weapon)
    {
        var values = PacketCodec.ReadScalars(tables, build, weapon ? "CriticalWeaponState" : "CriticalEffectTrigger", body);
        var actor = (int)values["actor"];
        var result = new RoundFeedSent(outbound, body.ToArray()) { ReportedActors = new[] { actor } };
        if (weapon)
        {
            var slot = (int)values["slot"];
            if (slot is < 0 or > 4)
                throw new PacketFormatException("Native critical weapon slot is outside the five character slots.", 4);
            return result with { CriticalWeaponState = new(actor, slot, (int)values["specification"]) };
        }
        foreach (var coordinate in new[] { "position_x", "position_y", "position_z" })
            if (!float.IsFinite((float)values[coordinate]))
                throw new PacketFormatException("Native critical trigger position is not finite.", 9);
        // cn-324 sub_478AE0 resolves +12 and calls cn-324 sub_4B0260 only when +4 and +28 are nonzero.
        return (byte)values["unknown_4"] != 0 && (byte)values["unknown_28"] != 0
            ? result with { CriticalEffectActor = (int)values["initiator"] } : result;
    }
}
