using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Adapts audited title requests, inventory-dependent acknowledgements and medal state messages.</summary>
internal sealed class TitleBinding(string protocol, IOpcodeTable opcodes, PacketTables tables, string build)
    : IInboundMessageBinding<TitleMakeRequested>, IInboundMessageBinding<TitleSetRequested>,
      IInboundMessageBinding<MedalInfoRequested>, IMessageSender<TitleMakeReplied>,
      IMessageSender<TitleSetReplied>, IMessageSender<TitleCatalogueConcealed>,
      IMessageSender<MedalInfoReplied>, IMessageSender<TitleMedalPresented>
{
    /// <summary>Gets the game protocol whose native title messages this binding adapts.</summary>
    public string Protocol => protocol;

    internal void Bind(IPluginContext context, IMessageRouter messages)
    {
        if (!tables.CapabilityOf(build, "title_operations"))
            throw new ConfigException($"Build '{build}' has no audited title operation capability.");
        RequireLayout("MakeTitleRequest", "user_number:u32,title_id:u32");
        RequireLayout("SetTitleRequest", "user_number:u32,action:u32,title_id:u32");
        RequireLayout("MakeTitleReply", "result:u32,stock_id:u32,unknown_12:wstr",
            "result:u32,title_id:u32,unknown_12:wstr");
        RequireLayout("MakeTitleRewardReply", "result:u32,stock_id:u32,unknown_12:wstr");
        RequireLayout("SetTitleReply", "result:u32,unknown_8:u32,unknown_12:u32",
            "result:u32,unknown_8:u32,title_id:u32");
        RequireLayout("TitleSyncInfo", "concealed_title_ids:list<u32>");
        RequireLayout("ProvideMedal", "reference:u32,unknown_8:u32");
        RequireLayout("MedalInfoRequest", "target_user_number:u32");
        RequireLayout("MedalInfoReply", "result:u32,unknown_8:u32,medals:list<TitleMedalCount>");
        RequireLayout("TitleMedalCount", "medal_id:u32,count:u32");
        var make = RequireOpcode(PacketDirection.ClientToServer, "gcpMakeUserTitle");
        var set = RequireOpcode(PacketDirection.ClientToServer, "gcpSetUserTitle");
        var medal = RequireOpcode(PacketDirection.ClientToServer, "gcpReqAwardInfo");
        foreach (var name in new[] { "gspResMakeUserTitle", "gspResSetUserTitle", "gspTitleSyncInfo",
                     "gspRepAwardInfo", "gspProvideMedal" })
            _ = RequireOpcode(PacketDirection.ServerToClient, name);

        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<TitleMakeRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<TitleSetRequested>>(this));
        context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MedalInfoRequested>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<TitleMakeReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<TitleSetReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<TitleCatalogueConcealed>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<MedalInfoReplied>>(this));
        context.Effect(context.Services.RegisterMany<IMessageSender<TitleMedalPresented>>(this));
        var packets = context.Services.Get<IPacketRouter>();
        context.Effect(packets.Register(protocol, make, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadMake(packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Title acquisition requests have no typed handler.");
        }));
        context.Effect(packets.Register(protocol, set, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadSet(packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Title equipment requests have no typed handler.");
        }));
        context.Effect(packets.Register(protocol, medal, async (session, packet, ct) =>
        {
            if (!await messages.DispatchAsync(protocol, session, ReadMedals(packet.Payload.Span), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Medal information requests have no typed handler.");
        }));
    }

    internal TitleMakeRequested ReadMake(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "MakeTitleRequest", body);
        return new((int)fields["user_number"], (int)fields["title_id"]);
    }

    internal TitleSetRequested ReadSet(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "SetTitleRequest", body);
        return new((int)fields["user_number"], (int)fields["action"], (int)fields["title_id"]);
    }

    internal MedalInfoRequested ReadMedals(ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "MedalInfoRequest", body);
        return new((int)fields["target_user_number"]);
    }

    internal byte[] Write(TitleMakeReplied message)
    {
        // kr-1256 sub_629480 resolves Stock_id for result 3; kr-1256 sub_9B0F70 resolves title_id for ordinary success.
        return PacketCodec.Write(tables, build, message.Result == 3 ? "MakeTitleRewardReply" : "MakeTitleReply",
            new Dictionary<string, object?>
        {
            ["result"] = message.Result, ["title_id"] = message.TitleId,
            ["stock_id"] = message.StockId, ["unknown_12"] = string.Empty,
        });
    }

    internal byte[] Write(TitleSetReplied message) => PacketCodec.Write(tables, build, "SetTitleReply",
        new Dictionary<string, object?>
        {
            ["result"] = message.Result, ["title_id"] = message.TitleId,
        });

    internal byte[] Write(TitleCatalogueConcealed message)
    {
        ArgumentNullException.ThrowIfNull(message.TitleIds);
        // kr-812 sub_840820 reads a four-byte count followed by words within the native 65,535-byte body.
        if (message.TitleIds.Count > (ushort.MaxValue - 4) / 4)
            throw new ArgumentOutOfRangeException(nameof(message), "The title concealment list exceeds one native body.");
        return PacketCodec.Write(tables, build, "TitleSyncInfo", new Dictionary<string, object?>
        { ["concealed_title_ids"] = message.TitleIds });
    }

    internal byte[] Write(MedalInfoReplied message)
    {
        ArgumentNullException.ThrowIfNull(message.Medals);
        // kr-812 sub_83DC50 reads twelve bytes of header/count and eight bytes per sMedalInfo record.
        if (message.Medals.Count > (ushort.MaxValue - 12) / 8)
            throw new ArgumentOutOfRangeException(nameof(message), "The medal snapshot exceeds one native body.");
        var rows = message.Medals.Select(row => new Dictionary<string, object?>
        { ["medal_id"] = row.MedalId, ["count"] = row.Count }).ToArray();
        return PacketCodec.Write(tables, build, "MedalInfoReply", new Dictionary<string, object?>
        { ["result"] = message.Result, ["medals"] = rows });
    }

    internal byte[] Write(TitleMedalPresented message) => PacketCodec.Write(tables, build, "ProvideMedal",
        new Dictionary<string, object?> { ["reference"] = message.Reference });

    private void RequireLayout(string structure, params string[] signatures) =>
        PacketLayoutContract.RequireSignature(tables, build, structure, "Title operations", signatures);

    private ushort RequireOpcode(PacketDirection direction, string name) =>
        opcodes.All.FirstOrDefault(row => row.Direction == direction && row.Name == name) is { Evidence: Evidence.Verified } entry
            ? entry.Value : throw new ConfigException($"Title operations require verified opcode '{name}' for '{build}'.");

    /// <summary>Writes the acquisition status after the feature publishes its inventory and medal changes.</summary>
    public ValueTask SendAsync(ISession session, TitleMakeReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResMakeUserTitle"), Write(message), ct);

    /// <summary>Writes the equipment status after the feature publishes the changed worn flags.</summary>
    public ValueTask SendAsync(ISession session, TitleSetReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspResSetUserTitle"), Write(message), ct);

    /// <summary>Sets additional catalogue conceal flags without claiming to synchronize title ownership.</summary>
    public ValueTask SendAsync(ISession session, TitleCatalogueConcealed message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspTitleSyncInfo"), Write(message), ct);

    /// <summary>Installs the complete medal balance map and opens or refreshes the native information view.</summary>
    public ValueTask SendAsync(ISession session, MedalInfoReplied message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspRepAwardInfo"), Write(message), ct);

    /// <summary>Displays a medal or item reward without changing the native medal balance map.</summary>
    public ValueTask SendAsync(ISession session, TitleMedalPresented message, CancellationToken ct) =>
        session.SendAsync(opcodes.S2C("gspProvideMedal"), Write(message), ct);
}
