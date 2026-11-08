using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Accepts the legacy relay's HP reports (<c>packets/relay-hp.yaml</c>). Every legacy client sends them all round,
/// because its game manager is built with the server-damage byte set (cn-324 sub_460042). Each report is read with
/// the build's own layout, so a malformed one closes the relay as any other; only the damage request has a reply.
/// </summary>
internal static class RelayHpReports
{
    private static readonly (string Name, string Structure)[] OneWay =
    [
        ("rcpSetDamageAffectingAttribute", "RelayDamageAttributeReport"),
        ("rcpCharacterRespawn", "RelayCharacterRespawnReport"),
        ("rcpChangeChrType", "RelayCharacterTypeReport"),
        ("rcpHpRecover", "RelayHpRecoverReport"),
        ("rcpSetChrState", "RelayCharacterStateReport"),
    ];

    /// <summary>Registers the reports and the damage reply on the build's relay protocol.</summary>
    internal static void Mount(IPluginContext context, IPacketRouter router, string protocol, string build,
        PacketTables tables, IOpcodeTable opcodes)
    {
        PacketLayoutContract.RequireFields(tables, build, "RelayHpDelta", "Relay HP reports",
            ("unknown_4", "u32"), ("unknown_8", "f32"));
        PacketLayoutContract.RequireFields(tables, build, "RelayDamageRequest", "Relay HP reports", ("unknown_4", "u32"));
        foreach (var (name, structure) in OneWay)
        {
            _ = tables.Resolve(build, structure);
            context.Effect(router.Register(protocol, opcodes.C2S(name), (session, packet, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                RequireBuild(session, build);
                _ = PacketCodec.Read(tables, build, structure, packet.Payload.Span);
                return ValueTask.CompletedTask;
            }));
        }
        var reply = opcodes.S2C("rspHpDeltaBroadcast");
        context.Effect(router.Register(protocol, opcodes.C2S("rcpReqDamageCalcAndBroadcast"), (session, packet, ct) =>
        {
            RequireBuild(session, build);
            return session.SendAsync(reply, Reply(tables, build, packet.Payload.Span), ct);
        }));
    }

    /// <summary>
    /// Answers one damage request. The consumers store the second word in a float that nothing reads
    /// (cn-324 sub_460C20 at +3104, kr-812 sub_506540 at +4752), so the reply echoes the request's first word
    /// with a zero value rather than inventing a damage result.
    /// </summary>
    internal static byte[] Reply(PacketTables tables, string build, ReadOnlySpan<byte> request)
    {
        var values = PacketCodec.Read(tables, build, "RelayDamageRequest", request);
        return PacketCodec.Write(tables, build, "RelayHpDelta", new Dictionary<string, object?>
        {
            ["unknown_4"] = values["unknown_4"], ["unknown_8"] = 0f,
        });
    }

    private static void RequireBuild(ISession session, string build)
    {
        if (session.Profile.Id != build)
            throw new InvalidOperationException("Relay HP report belongs to another client profile.");
    }
}
