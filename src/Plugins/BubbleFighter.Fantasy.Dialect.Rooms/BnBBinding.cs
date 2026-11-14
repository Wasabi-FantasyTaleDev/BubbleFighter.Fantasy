using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Reads and publishes BnB placement, explosion and pickup bodies through the relay opcode table on either transport.
/// The round handler owns actor and map validation, event ordering and publication to the reporting participant.
/// </summary>
internal sealed class BnBBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundBnBPlacementReported>, IInboundMessageBinding<RoundBnBExplosionReported>,
      IInboundMessageBinding<RoundBnBPickupReported>, IMessageSender<RoundBnBPlacementPublished>,
      IMessageSender<RoundBnBExplosionPublished>, IMessageSender<RoundBnBPickupPublished>
{
    // kr-1256 sub_6E78A0 writes an actor, a count and eight-byte pairs within the native 16-bit body ceiling.
    private const int MaxExplosionBombs = (ushort.MaxValue - 8) / 8;
    private static readonly PacketReadLimits ExplosionLimits = new(MaxStringCharacters: 0,
        MaxCollectionElements: MaxExplosionBombs, MaxValues: 2 + 3 * MaxExplosionBombs, MaxDepth: 3);

    /// <summary>Gets the game or relay transport carrying the BnB relay-space reports and publications.</summary>
    public string Protocol => protocol;

    /// <summary>Validates every layout and opcode before registering the three reports and publication senders.</summary>
    internal void Mount(IPluginContext context)
    {
        if (table.Connection != OpcodeConnection.Relay)
            throw new ConfigException("BnB requires a relay opcode table.");
        RequireLayouts(packets, build);
        var placement = table.C2S("gcpBnBPlaceBomb_");
        var explosion = table.C2S("gcpBnBExplodeBombs_");
        var pickup = table.C2S("gcpBnBPickUpItem_");
        _ = table.S2C("gspBnBPlaceBomb_");
        _ = table.S2C("gspBnBExplodeBombs_");
        _ = table.S2C("gspBnBPickUpItem_");

        var services = context.Services;
        var router = services.Get<IPacketRouter>();
        var messages = services.Get<IMessageRouter>();
        Bind(placement, body => ReadPlacement(packets, build, body.Span), "placement");
        Bind(explosion, body => ReadExplosion(packets, build, body.Span), "explosion");
        Bind(pickup, body => ReadPickup(packets, build, body.Span), "pickup");
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBnBPlacementReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBnBExplosionReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBnBPickupReported>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBnBPlacementPublished>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBnBExplosionPublished>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBnBPickupPublished>>(this));
        logger.LogInformation("dialect.rooms: BnB exchange of {Build} mounted for {Protocol}.", build, protocol);

        void Bind<T>(ushort opcode, Func<ReadOnlyMemory<byte>, T> read, string action) where T : IMessage
        {
            context.Effect(router.Register(protocol, opcode, async (session, packet, ct) =>
            {
                var report = read(packet.Payload);
                if (!await messages.DispatchAsync(protocol, session, report, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No BnB {action} handler is installed.");
            }));
        }
    }

    /// <summary>Publishes an accepted actor and placement cell using the relay-space opcode.</summary>
    public ValueTask SendAsync(ISession session, RoundBnBPlacementPublished message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBnBPlaceBomb_"), WritePlacement(packets, build, message), ct);

    /// <summary>Publishes an accepted actor and ordered generation/grid list using the relay-space opcode.</summary>
    public ValueTask SendAsync(ISession session, RoundBnBExplosionPublished message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBnBExplodeBombs_"), WriteExplosion(packets, build, message), ct);

    /// <summary>Publishes an accepted actor and item cell using the relay-space opcode.</summary>
    public ValueTask SendAsync(ISession session, RoundBnBPickupPublished message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBnBPickUpItem_"), WritePickup(packets, build, message), ct);

    /// <summary>Reads the actor and grid words written by kr-1256 sub_B43E40, rejecting every extra or missing byte.</summary>
    internal static RoundBnBPlacementReported ReadPlacement(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(packets, build, "BnBPlacement", body);
        return new((int)values["actor"], (int)values["grid_index"]);
    }

    /// <summary>Reads the actor and bounded ordered list written by kr-1256 sub_6E78A0, consuming the entire body.</summary>
    internal static RoundBnBExplosionReported ReadExplosion(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.Read(packets, build, "BnBExplosion", body, ExplosionLimits);
        var entries = (object?[])values["bombs"]!;
        var bombs = new RoundBnBBomb[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = (IReadOnlyDictionary<string, object?>)entries[index]!;
            bombs[index] = new((int)entry["generation"]!, (int)entry["grid_index"]!);
        }
        return new((int)values["actor"]!, Array.AsReadOnly(bombs));
    }

    /// <summary>Reads the actor and grid words written by kr-1256 sub_B44490, rejecting every extra or missing byte.</summary>
    internal static RoundBnBPickupReported ReadPickup(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(packets, build, "BnBPickup", body);
        return new((int)values["actor"], (int)values["grid_index"]);
    }

    /// <summary>Writes the actor and grid consumed by kr-1256 sub_6D97D0 and kr-1256 sub_6FC0C0.</summary>
    internal static byte[] WritePlacement(PacketTables packets, string build, RoundBnBPlacementPublished message) =>
        PacketCodec.Write(packets, build, "BnBPlacement", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor, ["grid_index"] = message.GridIndex,
        });

    /// <summary>Writes the actor and counted pairs consumed by kr-1256 sub_10F4AE0 and kr-1256 sub_BFF540.</summary>
    internal static byte[] WriteExplosion(PacketTables packets, string build, RoundBnBExplosionPublished message)
    {
        if (message.Bombs.Count > MaxExplosionBombs)
            throw new ArgumentOutOfRangeException(nameof(message), "BnB explosion list exceeds the native frame bound.");
        return PacketCodec.Write(packets, build, "BnBExplosion", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor,
            ["bombs"] = message.Bombs.Select(bomb => (object?)new Dictionary<string, object?>
            {
                ["generation"] = bomb.Generation, ["grid_index"] = bomb.GridIndex,
            }).ToArray(),
        });
    }

    /// <summary>Writes the actor and grid consumed by kr-1256 sub_6D97D0 and kr-1256 sub_6FC240.</summary>
    internal static byte[] WritePickup(PacketTables packets, string build, RoundBnBPickupPublished message) =>
        PacketCodec.Write(packets, build, "BnBPickup", new Dictionary<string, object?>
        {
            ["actor"] = message.Actor, ["grid_index"] = message.GridIndex,
        });

    /// <summary>Rejects layouts that change any consumed word or the counted generation/grid element structure.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        PacketLayoutContract.RequireSignature(packets, build, "BnBPlacement", "BnB", "actor:u32,grid_index:u32");
        PacketLayoutContract.RequireSignature(packets, build, "BnBExplosion", "BnB", "actor:u32,bombs:list<BnBBomb>");
        PacketLayoutContract.RequireSignature(packets, build, "BnBBomb", "BnB", "generation:u32,grid_index:u32");
        PacketLayoutContract.RequireSignature(packets, build, "BnBPickup", "BnB", "actor:u32,grid_index:u32");
    }
}
