using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// The BFRunner round's exchange (round mode 21): it reads the host's goal report, the checkpoint report and the
/// once-a-second progress list and the track-item touch, and writes the checkpoint and race-over notices and the item
/// box's slot grant, through <c>packets/bfrunner.yaml</c>
/// numbered by the profile's opcode table. Who may report a goal, which checkpoint counts and when the round ends
/// are the round flow's decisions; this binding only translates.
/// </summary>
internal sealed class BfRunnerBinding(string protocol, IOpcodeTable table, PacketTables packets, string build, ILogger logger)
    : IInboundMessageBinding<RoundBfRunnerGoalReported>, IInboundMessageBinding<RoundBfRunnerCheckpointReported>,
      IInboundMessageBinding<RoundBfRunnerProgressReported>, IInboundMessageBinding<RoundBfRunnerItemTouched>,
      IMessageSender<RoundBfRunnerCheckpointReached>, IMessageSender<RoundBfRunnerFinished>,
      IMessageSender<RoundBfRunnerItemGranted>
{
    // kr-812 sub_50DFC0 walks seats 0..31, so a progress list never has more entries than that.
    private const int MaxSeats = 32;

    /// <summary>Gets the game connection whose opcode table this binding uses.</summary>
    public string Protocol => protocol;

    /// <summary>Validates the audited layouts, then registers the three reports and the two notices.</summary>
    internal void Mount(IPluginContext context)
    {
        RequireLayouts(packets, build);
        var goal = table.C2S("gcpBFRunnerOver");
        var checkpoint = table.C2S("gcpBFRunnerCheckPoint");
        var progress = table.C2S("gcpBFRunnerCheckPointValue");
        var item = table.C2S("gcpBFRunnerEatItem");
        _ = table.S2C("gspAddItemToSlot");
        _ = table.S2C("gspBFRunnerOver");
        _ = table.S2C("gspBFRunnerCheckPointValue");
        var services = context.Services;
        var router = services.Get<IPacketRouter>();
        var messages = services.Get<IMessageRouter>();
        context.Effect(router.Register(protocol, goal, (session, packet, ct) =>
            DispatchAsync(messages, session, ReadGoal(packets, build, packet.Payload.Span), ct)));
        context.Effect(router.Register(protocol, checkpoint, (session, packet, ct) =>
            DispatchAsync(messages, session, ReadCheckpoint(packets, build, packet.Payload.Span), ct)));
        context.Effect(router.Register(protocol, progress, (session, packet, ct) =>
            DispatchAsync(messages, session, ReadProgress(packets, build, packet.Payload.Span), ct)));
        context.Effect(router.Register(protocol, item, (session, packet, ct) =>
            DispatchAsync(messages, session, ReadItem(packets, build, packet.Payload.Span), ct)));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBfRunnerGoalReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBfRunnerCheckpointReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBfRunnerProgressReported>>(this));
        context.Effect(services.RegisterMany<IInboundMessageBinding<RoundBfRunnerItemTouched>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBfRunnerItemGranted>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBfRunnerCheckpointReached>>(this));
        context.Effect(services.RegisterMany<IMessageSender<RoundBfRunnerFinished>>(this));
        logger.LogInformation("dialect.rooms: BFRunner exchange of {Build} mounted for {Protocol}.", build, protocol);
    }

    private async ValueTask DispatchAsync<T>(IMessageRouter messages, ISession session, T message, CancellationToken ct)
        where T : IMessage
    {
        if (!await messages.DispatchAsync(protocol, session, message, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"No BFRunner handler is installed for {typeof(T).Name}.");
    }

    /// <summary>Writes S2C <c>gspBFRunnerCheckPointValue</c>: the seat and its checkpoint region.</summary>
    public ValueTask SendAsync(ISession session, RoundBfRunnerCheckpointReached message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBFRunnerCheckPointValue"), WriteCheckpointReached(packets, build, message), ct);

    /// <summary>Writes S2C <c>gspBFRunnerOver</c>: the winning team.</summary>
    public ValueTask SendAsync(ISession session, RoundBfRunnerFinished message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspBFRunnerOver"), WriteFinished(packets, build, message), ct);

    /// <summary>
    /// Reads C2S <c>gcpBFRunnerOver</c>: the finishing runner's team, a word in kr-812 and a byte in the modern builds,
    /// with no trailing bytes.
    /// </summary>
    internal static RoundBfRunnerGoalReported ReadGoal(PacketTables packets, string build, ReadOnlySpan<byte> payload) =>
        new(Convert.ToInt32(PacketCodec.ReadScalars(packets, build, "gcpBFRunnerOver", payload)["team"]));

    /// <summary>Writes S2C <c>gspAddItemToSlot</c>: the seat, the item and the held slot.</summary>
    public ValueTask SendAsync(ISession session, RoundBfRunnerItemGranted message, CancellationToken ct) =>
        session.SendAsync(table.S2C("gspAddItemToSlot"), WriteItemGranted(packets, build, message), ct);

    /// <summary>Reads C2S <c>gcpBFRunnerEatItem</c>: the constant -1 word, the kind and the region, no trailing bytes.</summary>
    internal static RoundBfRunnerItemTouched ReadItem(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(packets, build, "gcpBFRunnerEatItem", payload);
        return new((int)values["kind"], (int)values["region"]);
    }

    /// <summary>Writes the seat, item and slot words through the item-slot structure of packets/map-supplies.yaml.</summary>
    internal static byte[] WriteItemGranted(PacketTables packets, string build, RoundBfRunnerItemGranted message) =>
        PacketCodec.Write(packets, build, "SupplyItemGranted", new Dictionary<string, object?>
        {
            ["seat"] = message.Seat,
            ["kind"] = message.Kind,
            ["item_slot"] = message.ItemSlot,
        });

    /// <summary>Reads C2S <c>gcpBFRunnerCheckPoint</c>: one word, the region, no trailing bytes.</summary>
    internal static RoundBfRunnerCheckpointReported ReadCheckpoint(PacketTables packets, string build, ReadOnlySpan<byte> payload) =>
        new((int)PacketCodec.ReadScalars(packets, build, "gcpBFRunnerCheckPoint", payload)["region"]);

    /// <summary>Reads C2S <c>gcpBFRunnerCheckPointValue</c>: a counted list of seat and progress words.</summary>
    internal static RoundBfRunnerProgressReported ReadProgress(PacketTables packets, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.Read(packets, build, "gcpBFRunnerCheckPointValue", payload,
            new PacketReadLimits(MaxCollectionElements: MaxSeats, MaxDepth: 3));
        var entries = (object?[])values["progress"]!;
        var seats = new BfRunnerSeatProgress[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = (Dictionary<string, object?>)entries[index]!;
            seats[index] = new((int)entry["seat"]!, (int)entry["value"]!);
        }
        return new(seats);
    }

    /// <summary>Writes the seat and region words.</summary>
    internal static byte[] WriteCheckpointReached(PacketTables packets, string build, RoundBfRunnerCheckpointReached message) =>
        PacketCodec.Write(packets, build, "gspBFRunnerCheckPointValue", new Dictionary<string, object?>
        {
            ["seat"] = message.Seat,
            ["region"] = message.Region,
        });

    /// <summary>Writes the team word.</summary>
    internal static byte[] WriteFinished(PacketTables packets, string build, RoundBfRunnerFinished message) =>
        PacketCodec.Write(packets, build, "gspBFRunnerOver", new Dictionary<string, object?> { ["team"] = message.Team });

    /// <summary>Checks every structure this binding reads or writes.</summary>
    internal static void RequireLayouts(PacketTables packets, string build)
    {
        if (PacketLayoutContract.Signature(packets, build, "gcpBFRunnerOver") is not ("team:u32" or "team:u8"))
            throw new ConfigException($"BFRunner: gcpBFRunnerOver of {build} is neither a team word nor a team byte.");
        PacketLayoutContract.RequireSignature(packets, build, "gcpBFRunnerCheckPoint", "BFRunner", "region:u32");
        PacketLayoutContract.RequireSignature(packets, build, "gcpBFRunnerCheckPointValue", "BFRunner",
            "progress:list<BFRunnerSeatValue>");
        PacketLayoutContract.RequireSignature(packets, build, "BFRunnerSeatValue", "BFRunner", "seat:u32,value:u32");
        if (PacketLayoutContract.Signature(packets, build, "gspBFRunnerOver") is not ("team:u32" or "team:u8"))
            throw new ConfigException($"BFRunner: gspBFRunnerOver of {build} is neither a team word nor a team byte.");
        PacketLayoutContract.RequireSignature(packets, build, "gspBFRunnerCheckPointValue", "BFRunner", "seat:u32,region:u32");
        PacketLayoutContract.RequireSignature(packets, build, "gcpBFRunnerEatItem", "BFRunner", "unknown_4:u32,kind:u32,region:u32");
        PacketLayoutContract.RequireSignature(packets, build, "SupplyItemGranted", "BFRunner", "seat:u32,kind:u32,item_slot:u32");
    }
}
