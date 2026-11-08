using BubbleFighter.Fantasy.Contracts.Rooms;
using System.Collections.Frozen;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Encodes the script team request and carries only approved owned peer conversions.</summary>
internal sealed class ScriptZombieBinding(string gameProtocol, string relayProtocol, IOpcodeTable game, IOpcodeTable relay,
    PacketTables packets, string build, IReadOnlySet<GameModeId> modes)
    : IMessageSender<RoundScriptZombieTeamChanged>, IMessageSender<RoundScriptZombieConversionReported>, IScriptZombieModes
{
    /// <summary>Gets the enabled native rule set without allowing callers to change the dialect configuration.</summary>
    public IReadOnlySet<GameModeId> Modes { get; } = modes.ToFrozenSet();

    /// <summary>Gets the game protocol on which recipients consume the accepted conversion.</summary>
    public string Protocol => gameProtocol;

    internal void Mount(IPluginContext context)
    {
        PacketLayoutContract.RequireSignature(packets, build, "ScriptZombieTeamChange", "script zombie",
            "seat:u32,team:u8,conversion:optional<0x3A,ScriptZombieConversion>,team_info:ServerTeamInfo",
            "seat:u32,team:u8,conversion:ScriptZombieConversion,team_info:ServerTeamInfo");
        PacketLayoutContract.RequireSignature(packets, build, "ScriptZombiePeerConversion", "script zombie",
            "team:u8,seat:u32,operation:u32,kind:optional<0x3A,ScriptZombieKind>,weapon_item_id:u32",
            "team:u8,seat:u32,operation:u32,kind:ScriptZombieKind,weapon_item_id:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ScriptZombieConversion", "script zombie",
            "zombie_kind:u32,weapon_item_id:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ScriptZombieKind", "script zombie", "zombie_kind:u32");
        PacketLayoutContract.RequireSignature(packets, build, "ServerTeamInfo", "script zombie",
            "team_slots:list<ServerTeamSlots>,unknown_12:list<u32>,unknown_24:list<u32>");
        PacketLayoutContract.RequireSignature(packets, build, "ServerTeamSlots", "script zombie", "team:u8,slots:list<u32>");
        _ = game.S2C("gspRoomChangeTeam");
        _ = game.S2C("gppScriptZombieConversion_");
        var router = context.Services.Get<IPacketRouter>();
        var messages = context.Services.Get<IMessageRouter>();
        var passthrough = packets.ValuesOf(build).GetValueOrDefault("round_feed_passthrough") is "true";
        Register(gameProtocol, game.C2S("gppScriptZombieConversion_"));
        Register(relayProtocol, relay.C2S("gppScriptZombieConversion_"));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScriptZombieTeamChanged>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<RoundScriptZombieConversionReported>>(this));
        context.Effect(context.Services.RegisterMany<IScriptZombieModes>(this));

        void Register(string protocol, ushort opcode)
        {
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                var report = ReadPeer(packets, build, packet.Payload.Span) with
                {
                    // Claiming this shared operation must not grant forwarding outside the enabled mode
                    // when the build permits only explicitly audited round-feed routes.
                    OtherModeFeed = passthrough ? new(game.S2C("gppScriptZombieConversion_"), packet.Payload.ToArray())
                    {
                        IncludeSender = packets.CapabilityOf(build, "round_feed_echo_sender"),
                    } : null,
                };
                if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No script zombie conversion handler is installed.");
            }));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoundScriptZombieConversionReported>>(new Input(protocol)));
        }
    }

    /// <summary>Publishes the accepted team and full native team panel.</summary>
    public ValueTask SendAsync(ISession session, RoundScriptZombieTeamChanged message, CancellationToken ct) =>
        session.SendAsync(game.S2C("gspRoomChangeTeam"), WriteTeam(packets, build, message), ct);

    /// <summary>Echoes the approved peer operation so every client, including its producer, applies the conversion.</summary>
    public ValueTask SendAsync(ISession session, RoundScriptZombieConversionReported message, CancellationToken ct) =>
        session.SendAsync(game.S2C("gppScriptZombieConversion_"), WritePeer(packets, build, message), ct);

    internal static byte[] WriteTeam(PacketTables packets, string build, RoundScriptZombieTeamChanged message) =>
        PacketCodec.Write(packets, build, "ScriptZombieTeamChange", new Dictionary<string, object?>
        {
            ["seat"] = message.Conversion.Seat, ["team"] = (byte)34,
            ["conversion"] = new Dictionary<string, object?>
            {
                ["zombie_kind"] = message.Conversion.ZombieKind, ["weapon_item_id"] = message.Conversion.WeaponItemId,
            },
            ["team_info"] = new Dictionary<string, object?>
            {
                ["team_slots"] = message.Members.GroupBy(member => member.Team).OrderBy(group => group.Key)
                    .Select(group => new Dictionary<string, object?>
                    {
                        ["team"] = checked((byte)group.Key), ["slots"] = group.Select(member => member.Slot).ToArray(),
                    }).ToArray(),
                ["unknown_12"] = Array.Empty<int>(), ["unknown_24"] = Array.Empty<int>(),
            },
        });

    internal static RoundScriptZombieConversionReported ReadPeer(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(packets, build, "ScriptZombiePeerConversion", body);
        int? kind = fields["kind"] switch
        {
            Dictionary<string, object?> value => Convert.ToInt32(value["zombie_kind"], System.Globalization.CultureInfo.InvariantCulture),
            (byte)0xBA => null,
            _ => throw new PacketFormatException("Zombie conversion kind tag must be 0x3A or 0xBA.", 9),
        };
        return new((int)fields["seat"]!, (byte)fields["team"]!, (int)fields["operation"]!, kind, (int)fields["weapon_item_id"]!);
    }

    internal static byte[] WritePeer(PacketTables packets, string build, RoundScriptZombieConversionReported message) =>
        PacketCodec.Write(packets, build, "ScriptZombiePeerConversion", new Dictionary<string, object?>
        {
            ["seat"] = message.Seat, ["team"] = checked((byte)message.Team), ["operation"] = message.Operation,
            ["kind"] = message.ZombieKind is { } kind
                ? new Dictionary<string, object?> { ["zombie_kind"] = kind } : (object)(byte)0xBA,
            ["weapon_item_id"] = message.WeaponItemId,
        });

    private sealed record Input(string Protocol) : IInboundMessageBinding<RoundScriptZombieConversionReported>;
}
